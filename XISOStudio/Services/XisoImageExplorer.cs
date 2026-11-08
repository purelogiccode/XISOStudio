using System.Globalization;
using XISOStudio.Interfaces;
using XISOStudio.Models;
using XISOSharp;
using Serilog;
using XISOSharp.Models;
using GodHeader = StfsSharp.GodHeader;
using GodTitleInfo = StfsSharp.Models.GodTitleInfo;

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
                    HostIoErrorDescriber.TryDescribe(ex, "reading the image", _image.Path) ?? ex.Message);
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
                    HostIoErrorDescriber.TryDescribe(ex, "writing the extracted file", destPath) ?? ex.Message);
            }
            else
            {
                _logger?.Error(ex, "Failed to copy out '{InternalPath}' from the image", internalPath);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public int ExtractAll(string destinationDirectory, bool skipExisting, IProgress<ProgressInfo>? progress,
        CancellationToken token)
    {
        try
        {
            var options = new UnpackOptions
            {
                SkipExisting = skipExisting,
                ContinueOnError = true,
                MaxDegreeOfParallelism = 4,
                NamePolicy = ExtractNamePolicy.Sanitize
            };

            return _image.Extract(destinationDirectory, options, progress, token);
        }
        catch (Exception ex)
        {
            if (ImageErrorClassifier.IsUserInputImageError(ex) || ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                _logger?.Information(ex, "Could not extract the image to '{Directory}': {Reason}", destinationDirectory,
                    HostIoErrorDescriber.TryDescribe(ex, "extracting the image", destinationDirectory) ?? ex.Message);
            }
            else
            {
                _logger?.Error(ex, "Failed to extract the image to '{Directory}'", destinationDirectory);
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
                HasSaveImage = xbe?.HasSaveImage ?? false,
                TitleThumbnail = title?.Xex?.Thumbnail,
                Details = BuildDetails()
            };
        }
        catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex)
                                   || ImageErrorClassifier.IsEnvironmentalIoError(ex))
        {
            _logger?.Information(ex, "Could not read image metadata from '{ImagePath}': {Reason}",
                _image.Path, HostIoErrorDescriber.TryDescribe(ex, "reading the image", _image.Path) ?? ex.Message);
            return ImageMetadata.Unavailable(_image.Source.Format.ToString(), _image.Path, _image.Length,
                $"Metadata could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the container-specific detail rows: detected layout, volume geometry,
    /// optimized tag, split parts and compression ratio for CISO/CCI inputs.
    /// </summary>
    /// <returns>The detail rows, or the rows gathered before a non-fatal read failure.</returns>
    private IReadOnlyList<ImageMetadataDetail> BuildDetails()
    {
        var details = new List<ImageMetadataDetail>();

        try
        {
            var format = _image.Source.Format.ToString();
            if (_image.Source.IsSplit) format += " (split)";
            details.Add(new ImageMetadataDetail("Container", format));

            if (_image.Volume.IsValid)
            {
                details.Add(new ImageMetadataDetail("Disc layout", _image.Volume.DiscFormat));
                details.Add(new ImageMetadataDetail("Root sector",
                    _image.Volume.RootDirSector.ToString(CultureInfo.InvariantCulture)));
                details.Add(new ImageMetadataDetail("Total sectors",
                    _image.Volume.TotalSectors.ToString("N0", CultureInfo.InvariantCulture)));
            }
            else
            {
                details.Add(new ImageMetadataDetail("Volume", "Invalid"));
            }

            details.Add(new ImageMetadataDetail("Optimized tag", _image.IsOptimized ? "Present" : "Not present"));
            details.Add(new ImageMetadataDetail("Parts",
                _image.Source.Parts.Count.ToString(CultureInfo.InvariantCulture)));

            AddCompressionDetails(details);
            AddDiscLayoutDetails(details);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or InvalidDataException)
        {
            _logger?.Information(ex, "Could not read container details from '{ImagePath}': {Reason}",
                _image.Path, ex.Message);
        }

        return details;
    }

    /// <summary>
    /// Adds the compressed size and ratio when the source is a CISO/CCI container.
    /// </summary>
    /// <param name="details">Detail rows being built.</param>
    private void AddCompressionDetails(List<ImageMetadataDetail> details)
    {
        if (!_image.Source.IsCompressed) return;

        long compressed = 0;
        foreach (var part in _image.Source.Parts)
        {
            try
            {
                compressed += new FileInfo(part).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _logger?.Debug(ex, "Could not read the compressed size of '{PartPath}'", part);
            }
        }

        if (compressed <= 0) return;

        details.Add(new ImageMetadataDetail("Compressed size", Formatter.FormatBytes(compressed)));
        if (_image.Length > 0)
        {
            details.Add(new ImageMetadataDetail("Ratio",
                $"{(double)compressed * 100 / _image.Length:F1}%"));
        }
    }

    /// <summary>
    /// Adds the Redump/XGD layout rows: XGD generation, known-size match, partition ranges,
    /// layer break and security-sector ranges.
    /// </summary>
    /// <param name="details">Detail rows being built.</param>
    private void AddDiscLayoutDetails(List<ImageMetadataDetail> details)
    {
        try
        {
            var layout = XisoDiscInfo.Read(_image.Path, quiet: true);

            if (!string.IsNullOrEmpty(layout.XgdType))
            {
                details.Add(new ImageMetadataDetail("XGD type", layout.XgdType));
            }

            details.Add(new ImageMetadataDetail("Redump size",
                layout.RedumpSizeMatch ? layout.RedumpTypeName ?? "Match" : "No match"));

            if (layout.GamePartition is { } gamePartition)
            {
                details.Add(new ImageMetadataDetail("Game partition",
                    $"{Formatter.FormatBytes(gamePartition.ByteOffset)} offset, " +
                    $"{Formatter.FormatBytes(gamePartition.ByteLength)}"));
            }

            if (layout.Pfi is { LayerCount: > 1 } pfi)
            {
                details.Add(new ImageMetadataDetail("Layer break",
                    $"sector {pfi.Layer0EndSector.ToString("N0", CultureInfo.InvariantCulture)}"));
            }

            if (!layout.IsContainer)
            {
                details.Add(new ImageMetadataDetail("Security sectors",
                    layout.SecuritySectors.Count == 0
                        ? "None"
                        : $"{layout.SecuritySectors.Count} range(s), " +
                          $"{layout.SecuritySectorsSource ?? "unknown source"}"));
            }

            foreach (var warning in layout.Warnings.Take(2))
            {
                _logger?.Information("Disc layout warning for '{ImagePath}': {Warning}", _image.Path, warning);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                                       or UnauthorizedAccessException)
        {
            // Layout inspection is informational; a container it cannot read must not hide
            // the rest of the metadata.
            _logger?.Debug(ex, "Could not inspect the disc layout of '{ImagePath}'", _image.Path);
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
                kind, internalPath, HostIoErrorDescriber.TryDescribe(ex, "reading the image", _image.Path) ?? ex.Message);
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _image.Dispose();
    }
}
