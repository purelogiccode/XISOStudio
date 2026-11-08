using System.Globalization;
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
    /// <summary>Decompressed hunks kept in the reader's LRU cache (hunks are 4 KiB for DVD CHDs).</summary>
    private const int HunkCacheSize = 32;

    /// <summary>Hunks pre-decompressed in the background to keep sequential reads ahead of the UI.</summary>
    private const int ReadAheadHunks = 8;

    /// <summary>Open CHD handle; owned and disposed by <see cref="_stream"/>.</summary>
    private readonly ChdFile _chd;

    private readonly ChdImageStream _stream;
    private readonly string _imagePath;
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
        _imagePath = chdPath;
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

        var error = ChdFile.Open(chdPath, out var chd);
        if (error != ChdError.Chderrnone || chd is null)
        {
            throw new InvalidDataException($"Not a valid CHD image ({error.GetMessage()}): {chdPath}");
        }

        // Browsing and extraction walk hunks in order. The defaults retain a single hunk and
        // decode synchronously, so configure an LRU cache and background read-ahead first.
        chd.ConfigureCache(HunkCacheSize);
        chd.ConfigureReadAhead(ReadAheadHunks);

        var streamError = ChdFile.OpenAsStream(chd, ownsChd: true, out var stream);
        if (streamError != ChdError.Chderrnone)
        {
            chd.Dispose();
            throw new InvalidDataException($"Not a valid CHD image ({streamError.GetMessage()}): {chdPath}");
        }

        _chd = chd;
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
                    HostIoErrorDescriber.TryDescribe(ex, "reading the CHD image", _imagePath) ?? ex.Message);
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
                    internalPath, HostIoErrorDescriber.TryDescribe(ex, "writing the extracted file", destPath)
                                  ?? ex.Message);
            }
            else
            {
                _logger?.Error(ex, "Failed to copy out '{InternalPath}' from the CHD image", internalPath);
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

            return XisoReader.UnpackImage(_stream, _imageName, destinationDirectory, token,
                options: options, progress: progress);
        }
        catch (Exception ex)
        {
            if (ImageErrorClassifier.IsUserInputImageError(ex) || ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                _logger?.Information(ex, "Could not extract the CHD image to '{Directory}': {Reason}",
                    destinationDirectory,
                    HostIoErrorDescriber.TryDescribe(ex, "extracting the CHD image", destinationDirectory)
                    ?? ex.Message);
            }
            else
            {
                _logger?.Error(ex, "Failed to extract the CHD image to '{Directory}'", destinationDirectory);
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
                return ImageMetadata.Unavailable("Chd", _imagePath, _stream.Length,
                    "No root default.xbe/default.xex was found in the decompressed image.");
            }

            XbeInfo? xbe = title.Xbe;
            XexTitleInfo? xex = title.Xex;
            return new ImageMetadata
            {
                Format = "Chd",
                Path = _imagePath,
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
                HasSaveImage = xbe?.HasSaveImage ?? false,
                TitleThumbnail = xex?.Thumbnail,
                Details = BuildDetails()
            };
        }
        catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex)
                                   || ImageErrorClassifier.IsEnvironmentalIoError(ex))
        {
            _logger?.Information(ex, "Could not read title metadata from the CHD image: {Reason}",
                HostIoErrorDescriber.TryDescribe(ex, "reading the CHD image", _imagePath) ?? ex.Message);
            return ImageMetadata.Unavailable("Chd", _imagePath, _stream.Length,
                $"Metadata could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the container detail rows from the CHD header: format version, hunk geometry,
    /// codec list, combined SHA-1 and any textual metadata (GAME, DISC, …).
    /// </summary>
    /// <returns>The detail rows, or the rows gathered before a non-fatal read failure.</returns>
    private IReadOnlyList<ImageMetadataDetail> BuildDetails()
    {
        var details = new List<ImageMetadataDetail>();

        try
        {
            details.Add(new ImageMetadataDetail("Container", $"CHD v{_chd.Version}"));
            details.Add(new ImageMetadataDetail("Hunk size",
                $"{_chd.HunkBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes"));
            details.Add(new ImageMetadataDetail("Unit size",
                $"{_chd.UnitBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes"));
            details.Add(new ImageMetadataDetail("Hunks",
                _chd.HunkCount.ToString("N0", CultureInfo.InvariantCulture)));
            if (_chd.TotalBytes > 0)
            {
                details.Add(new ImageMetadataDetail("Raw size",
                    Formatter.FormatBytes((long)Math.Min(_chd.TotalBytes, long.MaxValue))));
            }

            if (_chd.RequiresParent) details.Add(new ImageMetadataDetail("Parent", "Required"));

            if (Chd.ReadHeader(_imagePath, out var header) == ChdError.Chderrnone && header is not null)
            {
                details.Add(new ImageMetadataDetail("Codecs", FormatCodecs(header.Compression)));
            }

            if (_chd.Sha1 is { Length: > 0 } sha1)
            {
                details.Add(new ImageMetadataDetail("SHA-1", Convert.ToHexStringLower(sha1)));
            }

            foreach (var entry in _chd.Metadata.Where(static entry => entry.IsText).Take(4))
            {
                var text = entry.GetText();
                if (!string.IsNullOrWhiteSpace(text)) details.Add(new ImageMetadataDetail(entry.Tag, text));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or NotSupportedException)
        {
            _logger?.Information(ex, "Could not read CHD container details from '{ImagePath}': {Reason}",
                _imagePath, ex.Message);
        }

        return details;
    }

    /// <summary>
    /// Renders the header codec slots as their four-character CHD tags.
    /// </summary>
    /// <param name="codecs">Codec slots from <see cref="ChdHeaderInfo.Compression"/>.</param>
    /// <returns>A comma-separated tag list, or <c>none</c> for an uncompressed CHD.</returns>
    private static string FormatCodecs(IReadOnlyList<ChdCodec> codecs)
    {
        var tags = new List<string>(codecs.Count);
        foreach (var codec in codecs)
        {
            if (codec == ChdCodec.None) continue;

            if (codec == ChdCodec.Error)
            {
                tags.Add("error");
                continue;
            }

            var value = (uint)codec;
            tags.Add(new string(
            [
                (char)((value >> 24) & 0xFF),
                (char)((value >> 16) & 0xFF),
                (char)((value >> 8) & 0xFF),
                (char)(value & 0xFF)
            ]));
        }

        return tags.Count == 0 ? "none" : string.Join(", ", tags);
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
                kind, internalPath, HostIoErrorDescriber.TryDescribe(ex, "reading the CHD image", _imagePath)
                                   ?? ex.Message);
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stream.Dispose();
    }
}