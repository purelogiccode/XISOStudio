using System.Globalization;
using XISOSharp;
using XISOSharp.Models;
using XISOStudio.Interfaces;
using XISOStudio.Models;
using ZArchiveSharp;
using Serilog;

namespace XISOStudio.Services;

/// <summary>
/// <see cref="IImageExplorer"/> over a ZAR archive (ZArchive/zstd), backed by
/// <see cref="ZArchiveReader"/>. The reader is kept open for the explorer's
/// lifetime, so navigation and copy-out share one archive handle and the block
/// cache.
/// </summary>
internal sealed class ZarImageExplorer : IImageExplorer
{
    private readonly ZArchiveReader _reader;
    private readonly string _archivePath;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZarImageExplorer"/> class and opens the
    /// ZAR archive in keep-open mode.
    /// </summary>
    /// <param name="zarPath">Path of the ZAR archive to explore.</param>
    /// <param name="logger">Optional logger used for diagnostics.</param>
    internal ZarImageExplorer(string zarPath, ILogger? logger = null)
    {
        _logger = logger?.ForContext<ZarImageExplorer>();
        _archivePath = zarPath;
        _reader = ZArchiveReader.TryOpen(zarPath, CreateReaderOptions(), out var failure)
                  ?? throw new InvalidDataException($"Not a valid ZAR archive ({failure}): {zarPath}");
    }

    /// <summary>
    /// Reader options shared by the explorer and the integrity test: extended names are
    /// decoded (the default silently drops stored names of 0x80+ characters) and the archive
    /// is opened with write sharing so antivirus scanners and sync clients can hold it too.
    /// </summary>
    /// <returns>The reader options used for every ZAR open in the application.</returns>
    internal static ZArchiveReaderOptions CreateReaderOptions() => new()
    {
        DecodeExtendedNames = true,
        FileShare = FileShare.ReadWrite
    };

    /// <inheritdoc/>
    public IReadOnlyList<ImageEntry> ListChildren(string internalPath)
    {
        try
        {
            var path = ImagePaths.Normalize(internalPath);
            var node = LookUpOrThrow(path, internalPath);
            if (!_reader.IsDirectory(node))
            {
                throw new InvalidDataException($"Not a directory: {internalPath}");
            }

            var count = _reader.GetDirEntryCount(node);
            var entries = new List<ImageEntry>((int)Math.Min(count, int.MaxValue));
            for (uint i = 0; i < count; i++)
            {
                if (!_reader.TryGetDirEntry(node, i, out _, out var entry)) continue;

                // Never expose names that cannot be used as a single destination path
                // component (separators, "..", device names): the UI turns listed names
                // into paths, and copy-out skips them anyway.
                if (!IsSafeEntryName(entry.Name))
                {
                    _logger?.Information("Skipping unsafe archive entry name: '{EntryName}'", entry.Name);
                    continue;
                }

                var fullPath = ImagePaths.Combine(path, entry.Name);
                entries.Add(new ImageEntry(entry.Name, fullPath, entry.IsDirectory,
                    entry.IsDirectory ? 0 : (long)Math.Min(entry.Size, long.MaxValue)));
            }

            return entries;
        }
        catch (Exception ex)
        {
            // Missing paths, corrupt archives, and environmental I/O failures are expected
            // conditions; only unexpected defects belong at Error (the bug-report threshold).
            if (ImageErrorClassifier.IsUserInputImageError(ex) || ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                _logger?.Information(ex, "Could not list '{InternalPath}' in the archive: {Reason}", internalPath,
                    HostIoErrorDescriber.TryDescribe(ex, "reading the archive", _archivePath) ?? ex.Message);
            }
            else
            {
                _logger?.Error(ex, "Failed to list '{InternalPath}' in the archive", internalPath);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public void CopyOut(string internalPath, string destPath)
    {
        try
        {
            var path = ImagePaths.Normalize(internalPath);
            var node = LookUpOrThrow(path, internalPath);
            CopyNodeOut(node, destPath);
        }
        catch (Exception ex)
        {
            // Disk-full, access-denied, and missing/corrupt entries are expected conditions.
            if (ImageErrorClassifier.IsUserInputImageError(ex) || ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                _logger?.Information(ex, "Could not copy out '{InternalPath}' from the archive: {Reason}",
                    internalPath, HostIoErrorDescriber.TryDescribe(ex, "writing the extracted file", destPath)
                                  ?? ex.Message);
            }
            else
            {
                _logger?.Error(ex, "Failed to copy out '{InternalPath}' from the archive", internalPath);
            }

            throw;
        }
    }

    /// <summary>
    /// Resolves an archive path to its node handle.
    /// </summary>
    /// <param name="normalizedPath">Normalized path of the entry to resolve.</param>
    /// <param name="internalPath">Original path reported in the error message.</param>
    /// <returns>Node handle of the resolved entry.</returns>
    private uint LookUpOrThrow(string normalizedPath, string internalPath)
    {
        var node = _reader.LookUp(normalizedPath);
        if (node == ZArchiveReader.InvalidNode)
        {
            throw new InvalidDataException($"Path not found: {internalPath}");
        }

        return node;
    }

    /// <summary>
    /// Recursion depth cap for directory copy-out, mirroring
    /// <c>ZArchiveSharp.Pipeline.ZarPackEngine.MaxExtractDepth</c> and
    /// <c>XisoIntegrityService</c>, so a crafted archive fails catchably instead of
    /// overflowing the stack.
    /// </summary>
    private const int MaxCopyDepth = 1024;

    /// <summary>
    /// Copies a file or directory tree out of the archive to the destination path.
    /// </summary>
    /// <param name="node">Node handle of the entry to copy.</param>
    /// <param name="destPath">Destination path on disk.</param>
    /// <param name="depth">Current recursion depth, used to enforce the nesting cap.</param>
    // Recursion-depth bound threaded through the copy recursion by design:
    // it exists precisely to reject over-deep archives during recursion.
    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
    private void CopyNodeOut(uint node, string destPath, int depth = 0)
    {
        if (depth > MaxCopyDepth)
        {
            throw new InvalidDataException(
                $"ZAR archive directory nesting exceeds the supported depth ({MaxCopyDepth}).");
        }

        if (_reader.IsFile(node))
        {
            var directory = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var source = _reader.OpenRead(node);
            using var destination = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
            return;
        }

        Directory.CreateDirectory(destPath);
        var count = _reader.GetDirEntryCount(node);
        for (uint i = 0; i < count; i++)
        {
            // Skip undecodable entries instead of failing the whole directory: the listing
            // hides them as well, so navigation and copy-out stay consistent.
            if (!_reader.TryGetDirEntry(node, i, out var child, out var entry))
            {
                _logger?.Information("Skipping undecodable archive entry in '{DestPath}'", destPath);
                continue;
            }

            // Entry names come from the archive; skip names that would escape the
            // destination root (zip-slip), mirroring ZArchiveSharp's extractor.
            if (!IsSafeEntryName(entry.Name))
            {
                _logger?.Information("Skipping unsafe archive entry name: '{EntryName}'", entry.Name);
                continue;
            }

            CopyNodeOut(child, Path.Combine(destPath, entry.Name), depth + 1);
        }
    }

    /// <summary>
    /// True when an archive entry name is a single, plain filesystem component:
    /// no separators or alternate-data-stream colon, not relative (".", ".."), not
    /// rooted, and not a reserved Windows device name.
    /// </summary>
    /// <param name="name">Archive entry name to validate.</param>
    /// <returns><c>true</c> when the name is safe to use as a single destination path component; otherwise <c>false</c>.</returns>
    private static bool IsSafeEntryName(string name)
    {
        if (name.Length == 0 || name is "." or ".."
                             || name.Contains('/') || name.Contains('\\') || name.Contains(':')
                             || Path.IsPathRooted(name))
        {
            return false;
        }

        return !IsReservedDeviceName(name);
    }

    /// <summary>
    /// Checks whether a name stem matches a reserved Windows device name.
    /// </summary>
    /// <param name="name">Entry name to check.</param>
    /// <returns><c>true</c> when the name is a reserved device name; otherwise <c>false</c>.</returns>
    private static bool IsReservedDeviceName(string name)
    {
        // Win32 strips trailing dots and spaces before resolving a name, so "CON " and
        // "NUL." still open the device.
        var trimmed = name.TrimEnd('.', ' ');
        var dot = trimmed.IndexOf('.');
        var stem = dot < 0 ? trimmed : trimmed[..dot];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
               && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                   || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
               && stem[3] is >= '1' and <= '9';
    }

    /// <inheritdoc/>
    public int ExtractAll(string destinationDirectory, bool skipExisting, IProgress<ProgressInfo>? progress,
        CancellationToken token)
    {
        try
        {
            return XisoZarchive.ExtractZar(_archivePath, destinationDirectory, ExtractNamePolicy.Sanitize,
                skipExisting, progress, token);
        }
        catch (Exception ex)
        {
            if (ImageErrorClassifier.IsUserInputImageError(ex) || ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                _logger?.Information(ex, "Could not extract the archive to '{Directory}': {Reason}",
                    destinationDirectory,
                    HostIoErrorDescriber.TryDescribe(ex, "extracting the archive", destinationDirectory)
                    ?? ex.Message);
            }
            else
            {
                _logger?.Error(ex, "Failed to extract the archive to '{Directory}'", destinationDirectory);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public ImageMetadata GetMetadata()
    {
        var uncompressedLength = _reader.TotalUncompressedSize > long.MaxValue
            ? long.MaxValue
            : (long)_reader.TotalUncompressedSize;

        return ImageMetadata.Unavailable(
            "Zar",
            _archivePath,
            uncompressedLength,
            "ZAR archives expose a file tree, not an Xbox disc image, so title metadata is unavailable.",
            BuildDetails(uncompressedLength));
    }

    /// <summary>
    /// Builds the archive detail rows: entry count, uncompressed size, on-disk size and ratio.
    /// </summary>
    /// <param name="uncompressedLength">Total uncompressed size reported by the reader.</param>
    /// <returns>The detail rows.</returns>
    private IReadOnlyList<ImageMetadataDetail> BuildDetails(long uncompressedLength)
    {
        var details = new List<ImageMetadataDetail>
        {
            new("Entries", _reader.EntryCount.ToString("N0", CultureInfo.InvariantCulture)),
            new("Uncompressed", Formatter.FormatBytes(uncompressedLength))
        };

        try
        {
            var compressed = new FileInfo(_archivePath).Length;
            if (compressed <= 0) return details;

            details.Add(new ImageMetadataDetail("Archive size", Formatter.FormatBytes(compressed)));
            if (uncompressedLength > 0)
            {
                details.Add(new ImageMetadataDetail("Ratio",
                    $"{(double)compressed * 100 / uncompressedLength:F1}%"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger?.Debug(ex, "Could not read the archive size of '{ArchivePath}'", _archivePath);
        }

        return details;
    }

    /// <inheritdoc/>
    public XbeImage? GetXbeImage(string internalPath, XbeImageKind kind)
    {
        // XBE assets require the surrounding XDVDFS image geometry; a ZAR tree has none.
        return null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _reader.Dispose();
    }
}