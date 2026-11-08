using System.Globalization;
using Serilog;
using StfsSharp;
using StfsSharp.Models;
using XISOSharp;
using XISOSharp.Models;
using XISOStudio.Interfaces;
using XISOStudio.Models;

namespace XISOStudio.Services;

/// <summary>
/// <see cref="IImageExplorer"/> over a single-file Xbox 360 STFS package (XBLA, XBLIG or
/// Community Games), backed by <see cref="StfsPackage"/>. The package is kept open for the
/// explorer's lifetime; navigation and copy-out share one handle.
/// </summary>
internal sealed class StfsImageExplorer : IImageExplorer
{
    /// <summary>Depth cap for directory copy-out, mirroring the reader's own package limit.</summary>
    private const int MaxCopyDepth = 64;

    private readonly StfsPackage _package;
    private readonly string _packagePath;
    private readonly Dictionary<string, StfsEntry> _byPath;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StfsImageExplorer"/> class and opens the
    /// package with write sharing so sync clients and antivirus scanners can hold it too.
    /// </summary>
    /// <param name="packagePath">Path of the STFS package file.</param>
    /// <param name="logger">Optional logger used for diagnostics.</param>
    internal StfsImageExplorer(string packagePath, ILogger? logger = null)
    {
        _logger = logger?.ForContext<StfsImageExplorer>();
        _packagePath = packagePath;
        _package = StfsPackage.Open(packagePath, FileShare.ReadWrite);

        _byPath = new Dictionary<string, StfsEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _package.Entries)
        {
            // The file table never has a root entry, but guard against one so "/" stays synthetic.
            if (entry.Path.Length == 0) continue;
            _byPath["/" + entry.Path] = entry;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ImageEntry> ListChildren(string internalPath)
    {
        try
        {
            var path = ImagePaths.Normalize(internalPath);
            if (!string.Equals(path, "/", StringComparison.Ordinal))
            {
                if (!_byPath.TryGetValue(path, out var node) || !node.IsDirectory)
                {
                    throw new InvalidDataException($"Not a directory: {internalPath}");
                }
            }

            var prefix = string.Equals(path, "/", StringComparison.Ordinal) ? string.Empty : path[1..] + "/";
            var entries = new List<ImageEntry>();
            foreach (var entry in _package.Entries)
            {
                if (entry.Path.Length == 0) continue;
                if (!entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var remainder = entry.Path[prefix.Length..];
                if (remainder.Length == 0 || remainder.Contains('/')) continue;

                entries.Add(new ImageEntry(entry.Name, "/" + entry.Path, entry.IsDirectory,
                    entry.IsDirectory ? 0 : entry.Size));
            }

            return entries;
        }
        catch (Exception ex)
        {
            LogListFailure(ex, internalPath);
            throw;
        }
    }

    /// <inheritdoc/>
    public void CopyOut(string internalPath, string destPath)
    {
        try
        {
            var path = ImagePaths.Normalize(internalPath);
            if (!_byPath.TryGetValue(path, out var entry))
            {
                throw new InvalidDataException($"Path not found: {internalPath}");
            }

            CopyEntryOut(entry, destPath, 0);
        }
        catch (Exception ex)
        {
            LogCopyFailure(ex, internalPath, destPath);
            throw;
        }
    }

    /// <inheritdoc/>
    public int ExtractAll(string destinationDirectory, bool skipExisting, IProgress<ProgressInfo>? progress,
        CancellationToken token)
    {
        try
        {
            var adapter = progress is null
                ? null
                : new SynchronousProgress<StfsProgress>(value => progress.Report(value.Type switch
                {
                    StfsProgressKind.DirectoryAdded => new ProgressInfo(ProgressInfoType.DirAdded,
                        Path: value.Path, Sector: value.Sector),
                    _ => new ProgressInfo(ProgressInfoType.FileAdded, Path: value.Path,
                        Sector: value.Sector, Size: value.Size)
                }));

            // StfsPackage.ExtractTo returns the number of files written, not a result code;
            // the IImageExplorer contract uses 0 for success.
            var written = _package.ExtractTo(destinationDirectory,
                StfsSharp.Models.ExtractNamePolicy.Sanitize, skipExisting, adapter,
                warning: message => _logger?.Information("STFS extraction: {Warning:l}", message), ct: token);
            _logger?.Information("Extracted {FileCount} file(s) from the STFS package.", written);
            return 0;
        }
        catch (Exception ex)
        {
            LogExtractFailure(ex, destinationDirectory);
            throw;
        }
    }

    /// <inheritdoc/>
    public ImageMetadata GetMetadata()
    {
        var info = _package.Info;

        XexTitleInfo? xex = null;
        StfsEntry? executable = _package.Find("default.xex") ?? _package.Find("default_mp.xex");
        if (executable is { IsDirectory: false })
        {
            try
            {
                xex = XisoReader.GetXexTitleInfo(_package.ReadAllBytes(executable), executable.Name);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // A corrupt or unreadable executable must not hide the package metadata.
                _logger?.Information(ex, "Could not read the XDBF metadata of '{ExecutablePath}': {Reason}",
                    executable.Path, ex.Message);
            }
        }

        var kind = info.Kind == StfsPackageKind.Xbla ? "XBLA" : "XBLIG";
        var discNumber = info.DiscNumber != 0 ? info.DiscNumber : (uint)(xex?.Info.DiscNumber ?? 0);
        var discCount = info.DiscCount != 0 ? info.DiscCount : (uint)(xex?.Info.DiscCount ?? 0);

        return new ImageMetadata
        {
            Format = "Stfs",
            Path = _packagePath,
            Length = info.PackageLength,
            TitleName = string.IsNullOrEmpty(xex?.TitleName) ? info.TitleName : xex.TitleName,
            TitleIdHex = info.TitleIdHex,
            Region = FormatRegion(xex?.Info.Region ?? 0),
            DiscNumber = discNumber,
            DiscCount = discCount,
            Version = xex?.Info.Version ?? 0,
            Publisher = xex?.Publisher ?? "",
            Developer = xex?.Developer ?? "",
            Genre = xex?.Genre ?? "",
            Description = xex?.Description ?? "",
            ExecutablePath = executable is null ? "" : "/" + executable.Path,
            Kind = kind,
            TitleThumbnail = xex?.Thumbnail,
            Details = BuildDetails(info)
        };
    }

    /// <inheritdoc/>
    public XbeImage? GetXbeImage(string internalPath, XbeImageKind kind)
    {
        // STFS packages are Xbox 360 content and carry XEX files, not XBE image sections.
        return null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _package.Dispose();
    }

    /// <summary>
    /// Copies a file or directory tree out of the package, enforcing the depth cap and
    /// skipping host-unsafe entry names.
    /// </summary>
    /// <param name="entry">Entry to copy.</param>
    /// <param name="destPath">Destination path on disk.</param>
    /// <param name="depth">Current recursion depth.</param>
    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
    private void CopyEntryOut(StfsEntry entry, string destPath, int depth)
    {
        if (depth > MaxCopyDepth)
        {
            throw new InvalidDataException(
                $"STFS package directory nesting exceeds the supported depth ({MaxCopyDepth}).");
        }

        if (!entry.IsDirectory)
        {
            var directory = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var destination = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            _package.CopyTo(entry, destination);
            return;
        }

        Directory.CreateDirectory(destPath);
        var prefix = entry.Path + "/";
        foreach (var child in _package.Entries)
        {
            if (!child.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var remainder = child.Path[prefix.Length..];
            if (remainder.Length == 0 || remainder.Contains('/')) continue;

            // Entry names come from the package; skip names that would escape the
            // destination root (zip-slip), mirroring the package extractor.
            if (!IsSafeEntryName(child.Name))
            {
                _logger?.Information("Skipping unsafe STFS entry name: '{EntryName}'", child.Name);
                continue;
            }

            CopyEntryOut(child, Path.Combine(destPath, child.Name), depth + 1);
        }
    }

    /// <summary>
    /// Builds the package detail rows shown in the explorer.
    /// </summary>
    /// <param name="info">Package summary.</param>
    /// <returns>The detail rows.</returns>
    private static IReadOnlyList<ImageMetadataDetail> BuildDetails(StfsPackageInfo info)
    {
        return
        [
            new ImageMetadataDetail("Package kind", info.Kind == StfsPackageKind.Xbla ? "XBLA" : "XBLIG"),
            new ImageMetadataDetail("Content type", $"0x{info.ContentType:X8}"),
            new ImageMetadataDetail("Files", info.FileCount.ToString("N0", CultureInfo.InvariantCulture)),
            new ImageMetadataDetail("Directories",
                info.DirectoryCount.ToString("N0", CultureInfo.InvariantCulture)),
            new ImageMetadataDetail("Content size", Formatter.FormatBytes(info.TotalFileSize)),
            new ImageMetadataDetail("Total blocks",
                info.TotalBlockCount.ToString("N0", CultureInfo.InvariantCulture)),
            new ImageMetadataDetail("Free blocks",
                info.FreeBlockCount.ToString("N0", CultureInfo.InvariantCulture)),
            new ImageMetadataDetail("Layout", info.IsReadOnlyFormat ? "Read-only" : "Writable")
        ];
    }

    /// <summary>
    /// Formats the XEX region flags as a readable list.
    /// </summary>
    /// <param name="flags">Raw region flags from the XEX security info.</param>
    /// <returns>A comma-separated region list, or an empty string when no flags are set.</returns>
    private static string FormatRegion(uint flags)
    {
        if (flags == 0) return string.Empty;

        var regions = new List<string>(4);
        if ((flags & 0x000000FF) != 0) regions.Add("NTSC-U");
        if ((flags & 0x0000FF00) != 0) regions.Add("NTSC-J");
        if ((flags & 0x00FF0000) != 0) regions.Add("PAL");
        if ((flags & 0xFF000000) != 0) regions.Add("Other");
        return string.Join(", ", regions);
    }

    /// <summary>
    /// Logs a listing failure at the level that matches the condition.
    /// </summary>
    /// <param name="exception">Caught exception.</param>
    /// <param name="internalPath">Path that failed.</param>
    private void LogListFailure(Exception exception, string internalPath)
    {
        if (ImageErrorClassifier.IsUserInputImageError(exception) ||
            ImageErrorClassifier.IsEnvironmentalIoError(exception))
        {
            _logger?.Information(exception, "Could not list '{InternalPath}' in the STFS package: {Reason}",
                internalPath, HostIoErrorDescriber.TryDescribe(exception, "reading the package", _packagePath)
                              ?? exception.Message);
        }
        else
        {
            _logger?.Error(exception, "Failed to list '{InternalPath}' in the STFS package", internalPath);
        }
    }

    /// <summary>
    /// Logs a copy-out failure at the level that matches the condition.
    /// </summary>
    /// <param name="exception">Caught exception.</param>
    /// <param name="internalPath">Path that failed.</param>
    /// <param name="destPath">Destination path involved.</param>
    private void LogCopyFailure(Exception exception, string internalPath, string destPath)
    {
        if (ImageErrorClassifier.IsUserInputImageError(exception) ||
            ImageErrorClassifier.IsEnvironmentalIoError(exception))
        {
            _logger?.Information(exception, "Could not copy out '{InternalPath}' from the STFS package: {Reason}",
                internalPath, HostIoErrorDescriber.TryDescribe(exception, "writing the extracted file", destPath)
                              ?? exception.Message);
        }
        else
        {
            _logger?.Error(exception, "Failed to copy out '{InternalPath}' from the STFS package", internalPath);
        }
    }

    /// <summary>
    /// Logs a whole-package extraction failure at the level that matches the condition.
    /// </summary>
    /// <param name="exception">Caught exception.</param>
    /// <param name="destinationDirectory">Destination folder involved.</param>
    private void LogExtractFailure(Exception exception, string destinationDirectory)
    {
        if (ImageErrorClassifier.IsUserInputImageError(exception) ||
            ImageErrorClassifier.IsEnvironmentalIoError(exception))
        {
            _logger?.Information(exception, "Could not extract the STFS package to '{Directory}': {Reason}",
                destinationDirectory,
                HostIoErrorDescriber.TryDescribe(exception, "extracting the package", destinationDirectory)
                ?? exception.Message);
        }
        else
        {
            _logger?.Error(exception, "Failed to extract the STFS package to '{Directory}'", destinationDirectory);
        }
    }

    /// <summary>
    /// True when an entry name is a single, plain filesystem component.
    /// </summary>
    /// <param name="name">Entry name to validate.</param>
    /// <returns><c>true</c> when the name is safe to use as a destination path component.</returns>
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
    /// <returns><c>true</c> when the name is a reserved device name.</returns>
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
}
