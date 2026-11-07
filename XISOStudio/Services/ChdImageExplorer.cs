using CHDSharp;
using CHDSharp.Models;
using XISOStudio.Interfaces;
using XISOStudio.Models;
using XISOSharp;
using XISOSharp.Models;
using Serilog;

namespace XISOStudio.Services;

/// <summary>
/// <see cref="IImageExplorer"/> over a CHD (Compressed Hunks of Data) image. The CHD is
/// decompressed on demand through a keep-open <see cref="ChdImageStream"/> and the Xbox
/// filesystem is enumerated with XISOSharp, so navigation and copy-out share one handle.
/// Parsing is limited to Xbox DVD images; other CHD media types fail to open.
/// </summary>
internal sealed class ChdImageExplorer : IImageExplorer
{
    private readonly ChdImageStream _stream;
    private readonly string _imageName;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChdImageExplorer"/> class and opens the
    /// CHD image in keep-open mode.
    /// </summary>
    /// <param name="chdPath">Path of the Xbox DVD CHD image to explore.</param>
    /// <param name="logger">Optional logger used for diagnostics.</param>
    internal ChdImageExplorer(string chdPath, ILogger? logger = null)
    {
        _logger = logger?.ForContext<ChdImageExplorer>();
        _imageName = Path.GetFileName(chdPath);

        // Parsing is limited to Xbox DVD images: reject CD/GD-ROM/hard-disk/unknown CHDs at
        // open time instead of failing later in ListChildren/CopyOut.
        var classifyError = Chd.Classify(chdPath, out var classification);
        if (classifyError != ChdError.Chderrnone || !string.Equals(classification, "dvd", StringComparison.Ordinal))
        {
            var reason = classifyError != ChdError.Chderrnone
                ? classifyError.GetMessage()
                : classification ?? "unknown media type";
            throw new InvalidDataException($"CHD image is not an Xbox DVD image ({reason}): {chdPath}");
        }

        var error = ChdFile.OpenAsStream(chdPath, out var stream);
        if (error != ChdError.Chderrnone || stream is null)
        {
            throw new InvalidDataException($"Not a valid CHD image ({error.GetMessage()}): {chdPath}");
        }

        _stream = stream;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ImageEntry> ListChildren(string internalPath)
    {
        try
        {
            var path = ImagePaths.Normalize(internalPath);
            var entries = XisoReader.ListDirectory(_stream, _imageName, path);

            return entries.Select(entry => new ImageEntry(entry.Name,
                    ImagePaths.Combine(path, entry.Name), entry.IsDirectory,
                    entry.IsDirectory ? 0 : entry.FileSize))
                .ToList();
        }
        catch (Exception ex)
        {
            // Missing paths, corrupt images, and environmental I/O failures are expected
            // conditions; only unexpected defects belong at Error (the bug-report threshold).
            if (ImageErrorClassifier.IsUserInputImageError(ex) || ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                _logger?.Information(ex, "Could not list '{InternalPath}' in the CHD image: {Reason}", internalPath,
                    ex.Message);
            }
            else
            {
                _logger?.Error(ex, "Failed to list '{InternalPath}' in the CHD image", internalPath);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public void CopyOut(string internalPath, string destPath)
    {
        try
        {
            XisoReader.CopyOut(_stream, _imageName, ImagePaths.Normalize(internalPath), destPath);
        }
        catch (Exception ex)
        {
            // Disk-full, access-denied, and missing/corrupt entries are expected conditions.
            if (ImageErrorClassifier.IsUserInputImageError(ex) || ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                _logger?.Information(ex, "Could not copy out '{InternalPath}' from the CHD image: {Reason}",
                    internalPath, ex.Message);
            }
            else
            {
                _logger?.Error(ex, "Failed to copy out '{InternalPath}' from the CHD image", internalPath);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public ImageMetadata GetMetadata()
    {
        try
        {
            TitleInfo? title = XisoReader.GetTitleInfo(_stream, _imageName);
            if (title is null)
            {
                return ImageMetadata.Unavailable("Chd", _imageName, _stream.Length,
                    "No root default.xbe/default.xex was found in the decompressed image.");
            }

            XbeInfo? xbe = title.Xbe;
            XexTitleInfo? xex = title.Xex;
            return new ImageMetadata
            {
                Format = "Chd",
                Path = _imageName,
                Length = _stream.Length,
                TitleName = title.TitleName,
                TitleIdHex = title.TitleIdHex,
                Region = title.GameRegionName,
                DiscNumber = title.DiskNumber,
                Version = title.Version,
                Publisher = xex?.Publisher ?? "",
                Developer = xex?.Developer ?? "",
                Genre = xex?.Genre ?? "",
                Description = xex?.Description ?? "",
                ExecutablePath = title.XbePath,
                Kind = title.Kind.ToString(),
                HasLogo = xbe?.HasLogoBitmap ?? false,
                HasTitleImage = xbe?.HasTitleImage ?? false,
                HasSaveImage = xbe?.HasSaveImage ?? false
            };
        }
        catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex)
                                   || ImageErrorClassifier.IsEnvironmentalIoError(ex))
        {
            _logger?.Information(ex, "Could not read title metadata from the CHD image: {Reason}", ex.Message);
            return ImageMetadata.Unavailable("Chd", _imageName, _stream.Length,
                $"Metadata could not be read: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public XbeImage? GetXbeImage(string internalPath, XbeImageKind kind)
    {
        try
        {
            return XbeAssets.GetImage(_stream, _imageName, ImagePaths.Normalize(internalPath), kind);
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
        _stream.Dispose();
    }
}