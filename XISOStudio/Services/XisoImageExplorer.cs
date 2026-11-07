using XISOStudio.Interfaces;
using XISOStudio.Models;
using XISOSharp;
using Serilog;
using XISOSharp.Models;

namespace XISOStudio.Services;

/// <summary>
/// <see cref="IImageExplorer"/> over a plain ISO, CISO/CCI image or GOD/SVOD
/// container, backed by the open-once <see cref="XisoImage"/> facade (one image
/// view for the explorer's lifetime).
/// </summary>
internal sealed class XisoImageExplorer : IImageExplorer
{
    private readonly XisoImage _image;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="XisoImageExplorer"/> class and opens the
    /// image in keep-open mode, sharing reads and writes so an image held open by another
    /// process (sync client, antivirus, another reader) can still be browsed.
    /// </summary>
    /// <param name="imagePath">Path of the ISO/CISO/CCI image, GOD header or GOD <c>.data</c> folder.</param>
    /// <param name="logger">Optional logger used for diagnostics.</param>
    internal XisoImageExplorer(string imagePath, ILogger? logger = null)
    {
        _logger = logger?.ForContext<XisoImageExplorer>();

        // XisoImage reports a missing path as XisoFormatException; preserve the
        // explorer factory's FileNotFoundException contract.
        if (!File.Exists(imagePath) && !Directory.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image not found: {imagePath}", imagePath);
        }

        _image = XisoImage.Open(imagePath, new XisoImageOptions
        {
            // The explorer only reads. Widening the share mode is what lets it coexist
            // with writers (cloud sync clients, antivirus scanners, download managers)
            // that keep the image open while this running copy is being browsed.
            Share = FileShare.ReadWrite
        });
    }

    /// <inheritdoc/>
    public IReadOnlyList<ImageEntry> ListChildren(string internalPath)
    {
        try
        {
            return _image.List(internalPath)
                .Select(static node => new ImageEntry(node.Name, node.FullPath, node.IsDirectory, node.Size))
                .ToList();
        }
        catch (Exception ex)
        {
            // Expected input problems (corrupt image) and environmental failures (device
            // removed) are handled by the caller and must stay below the automatic
            // bug-report threshold.
            if (ImageErrorClassifier.IsUserInputImageError(ex) || ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                _logger?.Information(ex, "Could not list '{InternalPath}' in the image: {Reason}", internalPath,
                    ex.Message);
            }
            else
            {
                _logger?.Error(ex, "Failed to list '{InternalPath}' in the image", internalPath);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public void CopyOut(string internalPath, string destPath)
    {
        try
        {
            _image.CopyOut(ImagePaths.Normalize(internalPath), destPath);
        }
        catch (Exception ex)
        {
            // Disk-full, access-denied, and corrupt/truncated entries are expected
            // conditions: report them to the user but not as application defects.
            if (ImageErrorClassifier.IsUserInputImageError(ex) || ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                _logger?.Information(ex, "Could not copy out '{InternalPath}' from the image: {Reason}", internalPath,
                    ex.Message);
            }
            else
            {
                _logger?.Error(ex, "Failed to copy out '{InternalPath}' from the image", internalPath);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public ImageMetadata GetMetadata()
    {
        try
        {
            GodTitleInfo? god = GodHeader.TryReadTitle(_image.Path, out GodTitleInfo? godTitle) ? godTitle : null;

            TitleInfo? title = null;
            try
            {
                title = _image.GetTitleInfo();
            }
            catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex))
            {
                // A corrupt root executable/XDBF resource must not hide the rest of the
                // image metadata (or break the explorer).
                _logger?.Information(ex, "Could not read title metadata from '{ImagePath}': {Reason}",
                    _image.Path, ex.Message);
            }

            if (title is null && god is null)
            {
                return ImageMetadata.Unavailable(
                    _image.Source.Format.ToString(),
                    _image.Path,
                    _image.Length,
                    "No root default.xbe/default.xex or XContent title header was found.");
            }

            XbeInfo? xbe = title?.Xbe;
            XexTitleInfo? xex = title?.Xex;
            return new ImageMetadata
            {
                Format = _image.Source.Format.ToString(),
                Path = _image.Path,
                Length = _image.Length,
                TitleName = god?.TitleName ?? title?.TitleName ?? "",
                TitleIdHex = god?.TitleIdHex ?? title?.TitleIdHex ?? "",
                Region = title?.GameRegionName ?? "",
                DiscNumber = god?.DiscNumber ?? title?.DiskNumber ?? 0,
                DiscCount = god?.DiscCount ?? 0,
                Version = title?.Version ?? 0,
                Publisher = xex?.Publisher ?? "",
                Developer = xex?.Developer ?? "",
                Genre = xex?.Genre ?? "",
                Description = xex?.Description ?? "",
                ExecutablePath = title?.XbePath ?? "",
                Kind = god is not null ? "GOD/SVOD" : title?.Kind.ToString() ?? "Unknown",
                HasLogo = xbe?.HasLogoBitmap ?? false,
                HasTitleImage = xbe?.HasTitleImage ?? false,
                HasSaveImage = xbe?.HasSaveImage ?? false
            };
        }
        catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex)
                                   || ImageErrorClassifier.IsEnvironmentalIoError(ex))
        {
            _logger?.Information(ex, "Could not read image metadata from '{ImagePath}': {Reason}",
                _image.Path, ex.Message);
            return ImageMetadata.Unavailable(_image.Source.Format.ToString(), _image.Path, _image.Length,
                $"Metadata could not be read: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public XbeImage? GetXbeImage(string internalPath, XbeImageKind kind)
    {
        try
        {
            return XbeAssets.GetImage(_image.Path, ImagePaths.Normalize(internalPath), kind);
        }
        catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex)
                                   || ImageErrorClassifier.IsEnvironmentalIoError(ex))
        {
            _logger?.Information(ex, "Could not read the {Kind} XBE image from '{InternalPath}': {Reason}",
                kind, internalPath, ex.Message);
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _image.Dispose();
    }
}
