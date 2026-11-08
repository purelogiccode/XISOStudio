using System.Buffers;
using System.Globalization;
using CCISharp;
using CHDSharp;
using CHDSharp.Models;
using StfsSharp;
using StfsSharp.Models;
using XISOStudio.Interfaces;
using XISOStudio.Models;
using Serilog;
using XISOSharp;
using XISOSharp.Interfaces;
using ZArchiveSharp;

namespace XISOStudio.Services;

/// <summary>
/// Validates Xbox images using the XISOSharp, CCISharp, ZArchiveSharp and CHDSharp libraries: a deep
/// structural audit of the XDVDFS directory tree (plain ISO, CISO, CCI and Xbox CHD images) or the
/// ZAR archive tree, plus an optional sequential surface/deep scan that reads all data
/// (every sector, decompressed block or CHD hunk) to detect media or decompression errors.
/// CHD parsing is limited to Xbox DVD images; the CHD container is verified first and the
/// filesystem structure is then audited over the decompressed image.
/// </summary>
public class XisoIntegrityService : IXisoIntegrityService
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="XisoIntegrityService"/> class.
    /// </summary>
    /// <param name="logger">Logger used for diagnostics.</param>
    public XisoIntegrityService(ILogger logger)
    {
        _logger = logger.ForContext<XisoIntegrityService>();
    }

    /// <summary>
    /// Tests the integrity of the image at <paramref name="isoPath"/>, using the structural
    /// audit that matches its format (plain ISO, CISO, ZAR or Xbox CHD).
    /// </summary>
    /// <param name="isoPath">Path of the image to test.</param>
    /// <param name="performDeepScan">Whether to read all image or archive data in addition to the structural audit.</param>
    /// <param name="progress">Receives progress updates during the test.</param>
    /// <param name="token">Token used to cancel the test.</param>
    /// <returns><c>true</c> when the image passes the test; otherwise <c>false</c>.</returns>
    public Task<bool> TestIsoIntegrityAsync(string isoPath, bool performDeepScan,
        IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        return Task.Run(() =>
        {
            var fileName = Path.GetFileName(isoPath);

            try
            {
                // Single-file STFS packages (XBLA/XBLIG) are validated through their SHA-1
                // hash tree instead of the XDVDFS audit.
                if (StfsPackage.IsStfsPackage(isoPath))
                {
                    var stfsPassed = TestStfsIntegrity(isoPath, progress, token);
                    if (!stfsPassed && File.Exists(isoPath)) ReportInvalidIso(progress);
                    return stfsPassed;
                }

                if (Path.GetExtension(isoPath).Equals(".zar", StringComparison.OrdinalIgnoreCase))
                {
                    var zarPassed = TestZarIntegrity(isoPath, performDeepScan, progress, token);
                    // A file that vanished or cannot be opened is missing, not an invalid
                    // image, so it must not count toward the "not valid Xbox ISOs" warning.
                    if (!zarPassed && File.Exists(isoPath)) ReportInvalidIso(progress);
                    return zarPassed;
                }

                if (Path.GetExtension(isoPath).Equals(".chd", StringComparison.OrdinalIgnoreCase))
                {
                    var chdPassed = TestChdIntegrity(isoPath, performDeepScan, progress, token);
                    // Same as ZAR: only a readable-but-invalid image is counted.
                    if (!chdPassed && File.Exists(isoPath)) ReportInvalidIso(progress);
                    return chdPassed;
                }

                if (Path.GetExtension(isoPath).Equals(CciFormat.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    var cciPassed = TestCciIntegrity(isoPath, performDeepScan, progress, token);
                    // Same as ZAR/CHD: only a readable-but-invalid image is counted.
                    if (!cciPassed && File.Exists(isoPath)) ReportInvalidIso(progress);
                    return cciPassed;
                }

                _logger.Information("Starting structural integrity test for: {FileName}", fileName);
                _logger.Information(
                    "Note: This verifies filesystem structure and readability, not data checksums.");

                // 1. Optional deep surface scan: read the entire image sequentially to test physical media
                if (performDeepScan && !PerformSurfaceScan(isoPath, progress, token))
                {
                    ReportInvalidIso(progress);
                    return false;
                }

                // 2. Logical structure test: deep audit of the XDVDFS directory tree.
                // The audit does not require the optimized tag; raw (unconverted) dumps
                // legitimately lack it, and the tag state is reported via IsOptimized.
                _logger.Information("Validating XDVDFS directory structure...");
                var audit = XisoReader.AuditXiso(isoPath, requireOptimizedTag: false);

                if (audit.Issues.Count > 0)
                {
                    _logger.Information("Structural validation failed for {FileName}:", fileName);
                    foreach (var issue in audit.Issues)
                    {
                        _logger.Information("  - {Issue:l}", issue);
                    }

                    ReportInvalidIso(progress);
                    return false;
                }

                if (!audit.IsOptimized)
                {
                    _logger.Information(
                        "Image is not optimized (raw ISO); the optimized tag is written during conversion.");
                }

                _logger.Information("Structure is valid ({FilesChecked} files, {DirsChecked} directories checked).",
                    audit.FilesChecked, audit.DirsChecked);
                LogDiscLayout(isoPath);
                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.Information("Integrity check of {FileName} was canceled.", fileName);
                throw;
            }
            catch (Exception ex) when (IsInputError(ex))
            {
                _logger.Information(ex, "Integrity check failed for {FileName}", fileName);
                // A file that vanished is missing, not an invalid image.
                if (ex is not FileNotFoundException) ReportInvalidIso(progress);
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Permission and I/O problems are environmental, not an application defect.
                _logger.Information(ex, "Integrity check could not access {FileName}", fileName);
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Integrity check failed for {FileName}", fileName);
                return false;
            }
        }, token);
    }

    /// <summary>
    /// Errors that indicate the input image is missing, unsupported, or corrupt rather
    /// than an application defect. XISOSharp's low-level reader reports truncated
    /// images as a plain IOException with a "Read error" message.
    /// </summary>
    /// <param name="ex">Exception raised while testing an image.</param>
    /// <returns><c>true</c> when the exception indicates a missing, unsupported, or corrupt input image; otherwise <c>false</c>.</returns>
    private static bool IsInputError(Exception ex)
    {
        return ex is XisoFormatException or XisoEmptyException or InvalidDataException or ExtractErrorException
                   or CciException or FileNotFoundException or EndOfStreamException ||
               (ex is IOException ioException &&
                ioException.Message.StartsWith("Read error", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Signals that the failed test was caused by an invalid image, so the UI can count
    /// genuinely invalid files instead of treating every failure as one.
    /// </summary>
    /// <param name="progress">Progress receiver that counts the invalid image.</param>
    private static void ReportInvalidIso(IProgress<BatchOperationProgress> progress)
    {
        progress.Report(new BatchOperationProgress { InvalidIsoCount = 1 });
    }

    /// <summary>
    /// Validates a single-file STFS package (XBLA/XBLIG) by verifying its SHA-1 hash tree:
    /// every data block, file block chain, level-0/1/2 table, top hash and header hash.
    /// Package signatures are not checked.
    /// </summary>
    /// <param name="packagePath">Path of the STFS package to validate.</param>
    /// <param name="progress">Receives progress updates during validation.</param>
    /// <param name="token">Token used to cancel the validation.</param>
    /// <returns><c>true</c> when every hash and block chain matched; otherwise <c>false</c>.</returns>
    private bool TestStfsIntegrity(string packagePath, IProgress<BatchOperationProgress> progress,
        CancellationToken token)
    {
        var fileName = Path.GetFileName(packagePath);
        _logger.Information("Starting integrity test for STFS package: {FileName}", fileName);
        _logger.Information(
            "Note: This verifies the package SHA-1 hash tree; package signatures are not checked.");

        StfsPackage? package;
        try
        {
            package = StfsPackage.Open(packagePath, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.Information(ex, "STFS package could not be opened: {FileName}", fileName);
            return false;
        }

        using (package)
        {
            progress.Report(new BatchOperationProgress { StatusText = "Validating STFS hash tree..." });
            var report = package.Validate(token);

            if (!report.IsValid)
            {
                _logger.Information("STFS validation failed for {FileName}:", fileName);
                foreach (var issue in report.Issues.Take(20))
                {
                    _logger.Information("  - {Area}: {Reason}", issue.Area, issue.Reason);
                }

                return false;
            }

            _logger.Information(
                "STFS package is valid ({FilesChecked} files, {BlocksChecked} blocks, {TablesChecked} hash tables, {BytesChecked} checked).",
                report.FilesChecked, report.BlocksChecked, report.HashTablesChecked,
                Formatter.FormatBytes(report.BytesChecked));
            return true;
        }
    }

    /// <summary>
    /// Validates a CCI/CCIM image with CCISharp: the container header, part set and
    /// (with <paramref name="performDeepScan"/>) every LZ4 sector are decoded, then the
    /// decompressed XDVDFS directory tree is audited through XISOSharp.
    /// </summary>
    /// <param name="cciPath">Path of the CCI image or one of its split parts.</param>
    /// <param name="performDeepScan">Whether to decode every sector in addition to the header/index checks.</param>
    /// <param name="progress">Receives progress updates during the test.</param>
    /// <param name="token">Token used to cancel the test.</param>
    /// <returns><c>true</c> when the CCI container and filesystem pass the test; otherwise <c>false</c>.</returns>
    private bool TestCciIntegrity(string cciPath, bool performDeepScan,
        IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        var fileName = Path.GetFileName(cciPath);
        _logger.Information("Starting structural integrity test for: {FileName}", fileName);
        _logger.Information(
            "Note: This verifies CCI container structure, sector decoding and the Xbox filesystem; it doesn't verify game data checksums.");

        token.ThrowIfCancellationRequested();
        CciError openError = CciFile.Open(cciPath, new CciReaderOptions { Share = FileShare.Read },
            out CciFile? cci);
        if (openError != CciError.None || cci is null)
        {
            _logger.Information("CCI image could not be opened ({Error}): {FileName}",
                openError.ToMessage(), fileName);
            return false;
        }

        using (cci)
        {
            _logger.Information("Validating CCI structure ({Parts} part(s), {Sectors} sectors)...",
                cci.Parts.Count, cci.TotalSectors);
            progress.Report(new BatchOperationProgress { StatusText = "Validating CCI structure..." });

            CciError validationError = cci.Validate(performDeepScan, out CciValidationReport? report);
            if (validationError != CciError.None || report is null || !report.IsValid)
            {
                _logger.Information("CCI validation failed for {FileName}:", fileName);
                if (report is null)
                {
                    _logger.Information("  - {Error}", validationError.ToMessage());
                }
                else
                {
                    foreach (var issue in report.Issues.Take(20))
                    {
                        _logger.Information("  - part {PartIndex}, sector {Sector}: {Reason}",
                            issue.PartIndex, issue.Sector?.ToString() ?? "-", issue.Reason);
                    }
                }

                return false;
            }

            _logger.Information(
                "CCI structure is valid ({Parts} part(s), {Compressed} compressed, {Uncompressed} uncompressed, ratio {Ratio:P1}).",
                report.Parts.Count, Formatter.FormatBytes(report.CompressedSize),
                Formatter.FormatBytes((long)Math.Min(report.UncompressedSize, long.MaxValue)), report.Ratio);
        }

        // 2. Logical structure test over the decompressed image. XISOSharp's audit
        // accepts CCI inputs through XisoImageSource, so the filesystem sees the same
        // bytes CCISharp decoded above.
        _logger.Information("Validating XDVDFS directory structure...");
        var audit = XisoReader.AuditXiso(cciPath, requireOptimizedTag: false);
        if (audit.Issues.Count > 0)
        {
            _logger.Information("Structural validation failed for {FileName}:", fileName);
            foreach (var issue in audit.Issues)
            {
                _logger.Information("  - {Issue:l}", issue);
            }

            return false;
        }

        if (!audit.IsOptimized)
        {
            _logger.Information(
                "Image is not optimized (raw ISO); the optimized tag is written during conversion.");
        }

        _logger.Information("Structure is valid ({FilesChecked} files, {DirsChecked} directories checked).",
            audit.FilesChecked, audit.DirsChecked);
        return true;
    }

    /// <summary>
    /// Validates a ZAR archive (ZArchive/zstd): opens the container (header, offset
    /// records, name table and file tree), walks the whole tree, and with
    /// <paramref name="performDeepScan"/> decompresses every file to prove all blocks
    /// are readable. ZAR holds compressed blocks, so there is no separate surface scan.
    /// </summary>
    /// <param name="zarPath">Path of the ZAR archive to validate.</param>
    /// <param name="performDeepScan">Whether to decompress and read every file in the archive.</param>
    /// <param name="progress">Receives progress updates during the test.</param>
    /// <param name="token">Token used to cancel the test.</param>
    /// <returns><c>true</c> when the archive passes the test; otherwise <c>false</c>.</returns>
    private bool TestZarIntegrity(string zarPath, bool performDeepScan,
        IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        var fileName = Path.GetFileName(zarPath);
        _logger.Information("Starting structural integrity test for: {FileName}", fileName);
        _logger.Information(
            "Note: This verifies archive structure and readability, not data checksums.");

        _logger.Information("Validating ZAR archive structure...");
        using var reader = ZArchiveReader.TryOpen(zarPath, ZarImageExplorer.CreateReaderOptions(), out var failure);
        if (reader is null)
        {
            _logger.Information("ZAR archive could not be opened ({Failure}): {FileName}", failure, fileName);
            return false;
        }

        var files = new List<ZarFileEntry>();
        var directoryCount = 0;
        WalkZarDirectory(reader, ZArchiveReader.RootNode, "/", files, ref directoryCount, token);

        _logger.Information(
            "ZAR structure is valid ({FilesChecked} files, {DirsChecked} directories, {TotalSize} uncompressed).",
            files.Count, directoryCount, Formatter.FormatBytes((long)Math.Min(reader.TotalUncompressedSize,
                long.MaxValue)));

        if (!performDeepScan)
        {
            return true;
        }

        _logger.Information("Performing deep scan (decompressing all archive data)...");
        var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024);
        try
        {
            // Saturating total computed by the reader (no archive I/O).
            var totalBytes = reader.TotalUncompressedSize;
            ulong bytesRead = 0;
            var lastReportedPercent = -1;

            foreach (var file in files)
            {
                using var stream = reader.OpenRead(file.Node);
                var expected = file.Size;
                ulong fileRead = 0;
                while (fileRead < expected)
                {
                    token.ThrowIfCancellationRequested();

                    var toRead = (int)Math.Min((ulong)buffer.Length, expected - fileRead);
                    var read = stream.Read(buffer, 0, toRead);
                    if (read == 0)
                    {
                        // EntryStream reports a failed block as a short read, so a
                        // zero-length read before the entry's end is corrupt data.
                        _logger.Information(
                            "Deep scan failed: {Path} is truncated or contains a corrupt block at offset {Offset}.",
                            file.Path, fileRead);
                        return false;
                    }

                    fileRead += (ulong)read;
                    bytesRead += (ulong)read;

                    if (totalBytes != 0)
                    {
                        var percent = (int)(bytesRead * 100 / totalBytes);
                        if (percent > lastReportedPercent)
                        {
                            lastReportedPercent = percent;
                            progress.Report(new BatchOperationProgress { StatusText = $"Deep scan: {percent}%" });
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _logger.Information("ZAR deep scan completed successfully.");
        return true;
    }

    /// <summary>
    /// Validates a CHD (Compressed Hunks of Data) image. Parsing is limited to Xbox DVD
    /// images: other CHD media (CD, GD-ROM, hard disk, laserdisc) are rejected. The CHD
    /// container is verified first — header-only, or all hunks and hashes when
    /// <paramref name="performDeepScan"/> is set — and the Xbox filesystem structure is
    /// then audited over the decompressed image through XISOSharp.
    /// </summary>
    /// <param name="chdPath">Path of the CHD image to validate.</param>
    /// <param name="performDeepScan">Whether to verify every hunk and checksum in addition to the header.</param>
    /// <param name="progress">Receives progress updates during the deep verification.</param>
    /// <param name="token">Token used to cancel the test.</param>
    /// <returns><c>true</c> when the image passes the test; otherwise <c>false</c>.</returns>
    private bool TestChdIntegrity(string chdPath, bool performDeepScan,
        IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        var fileName = Path.GetFileName(chdPath);
        _logger.Information("Starting integrity test for CHD image: {FileName}", fileName);
        _logger.Information(
            "Note: This verifies the CHD container checksums and the Xbox filesystem structure.");

        // Only Xbox DVD images are supported; reject CD/GD-ROM/hard-disk/unknown CHDs early.
        // Child CHDs (which need a parent file) surface as an open error here.
        var classifyError = Chd.Classify(chdPath, out var classification);
        if (classifyError != ChdError.Chderrnone || !string.Equals(classification, "dvd", StringComparison.Ordinal))
        {
            var reason = classifyError != ChdError.Chderrnone
                ? classifyError.GetMessage()
                : classification ?? "unknown media type";
            _logger.Information("CHD image is not a supported Xbox DVD image ({Reason}): {FileName}",
                reason, fileName);
            return false;
        }

        // 1. Container verification: header-only by default, every hunk plus checksums for a deep scan.
        using (var stream = new FileStream(chdPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024))
        {
            var result = Chd.CheckFile(stream, fileName, performDeepScan,
                performDeepScan ? CreateChdVerifyProgressAdapter(progress) : null, token);

            if (!result.IsSuccess)
            {
                _logger.Information("CHD verification failed for {FileName}: {Error}", fileName,
                    result.Error.GetMessage());
                return false;
            }

            _logger.Information("CHD container is valid (V{Version}, SHA1: {Sha1}).", result.Version, result.Sha1Hex);
        }

        // 2. Xbox filesystem audit over the decompressed image.
        _logger.Information("Validating Xbox filesystem structure...");
        var chdOpenError = ChdFile.Open(chdPath, out var chd, token);
        if (chdOpenError != ChdError.Chderrnone || chd is null)
        {
            _logger.Information("Could not open CHD image for filesystem validation: {Error}",
                chdOpenError.GetMessage());
            return false;
        }

        // The audit walks the image sequentially; decode whole hunks ahead of the reader.
        chd.ConfigureCache(32);
        chd.ConfigureReadAhead(8);

        var openError = ChdFile.OpenAsStream(chd, ownsChd: true, out var chdStream);
        if (openError != ChdError.Chderrnone || chdStream is null)
        {
            chd.Dispose();
            _logger.Information("Could not open CHD image for filesystem validation: {Error}",
                openError.GetMessage());
            return false;
        }

        using var device = new ChdBlockDevice(chdStream);
        var audit = XisoReader.AuditXiso(device, fileName, requireOptimizedTag: false);

        if (audit.Issues.Count > 0)
        {
            _logger.Information("Structural validation failed for {FileName}:", fileName);
            foreach (var issue in audit.Issues)
            {
                _logger.Information("  - {Issue:l}", issue);
            }

            return false;
        }

        if (!audit.IsOptimized)
        {
            _logger.Information(
                "Image is not optimized (raw ISO); the optimized tag is written during conversion.");
        }

        _logger.Information("Structure is valid ({FilesChecked} files, {DirsChecked} directories checked).",
            audit.FilesChecked, audit.DirsChecked);
        return true;
    }

    /// <summary>Reports CHD deep-verification progress at 5% steps.</summary>
    /// <param name="progress">Progress receiver that receives the throttled updates.</param>
    /// <returns>An adapter that forwards CHD verification progress in 5% steps.</returns>
    private static IProgress<ChdProgress> CreateChdVerifyProgressAdapter(IProgress<BatchOperationProgress> progress)
    {
        var lastPercent = -1;
        return new SynchronousProgress<ChdProgress>(chdProgress =>
        {
            var percent = (int)chdProgress.Percent;
            if (percent < lastPercent + 5 && percent < 100) return;

            lastPercent = percent;
            progress.Report(new BatchOperationProgress { StatusText = $"CHD verification: {percent}%" });
        });
    }

    /// <summary>
    /// Adapts a decompressed CHD image stream to XISOSharp's <see cref="IBlockDevice"/>
    /// so the Xbox filesystem audit can run directly on the CHD contents. Disposing the
    /// device disposes the underlying stream (and the CHD file handle it owns).
    /// </summary>
    private sealed class ChdBlockDevice : IBlockDevice
    {
        private readonly ChdImageStream _stream;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChdBlockDevice"/> class.
        /// </summary>
        /// <param name="stream">Decompressed CHD image stream owned by the device.</param>
        internal ChdBlockDevice(ChdImageStream stream)
        {
            _stream = stream;
        }

        /// <summary>Length of the decompressed image in bytes.</summary>
        public long Length => _stream.Length;

        /// <summary>Reads decompressed image data at the specified offset.</summary>
        /// <param name="offset">Absolute byte offset in the decompressed image.</param>
        /// <param name="buffer">Destination buffer for the data read.</param>
        /// <returns>Number of bytes read into <paramref name="buffer"/>.</returns>
        public int Read(long offset, Span<byte> buffer)
        {
            _stream.Position = offset;
            return _stream.Read(buffer);
        }

        /// <summary>Not supported: CHD images are read-only, so this method always throws.</summary>
        /// <param name="offset">Absolute byte offset in the decompressed image.</param>
        /// <param name="buffer">Source buffer that would have been written.</param>
        public void Write(long offset, ReadOnlySpan<byte> buffer)
        {
            throw new NotSupportedException("CHD images are read-only.");
        }

        /// <summary>Disposes the underlying decompressed image stream.</summary>
        public void Dispose()
        {
            _stream.Dispose();
        }
    }

    /// <summary>A file discovered by the ZAR tree walk.</summary>
    /// <param name="Node">Archive node handle of the file.</param>
    /// <param name="Path">Full archive path of the file.</param>
    /// <param name="Size">Uncompressed size of the file in bytes.</param>
    private readonly record struct ZarFileEntry(uint Node, string Path, ulong Size);

    /// <summary>
    /// Recursion depth cap for the ZAR walk, mirroring
    /// <c>ZArchiveSharp.Pipeline.ZarPackEngine.MaxExtractDepth</c> so a crafted archive
    /// fails catchably instead of overflowing the stack.
    /// </summary>
    private const int MaxZarWalkDepth = 1024;

    /// <summary>
    /// Recursively walks the ZAR file tree, collecting every file with its node handle
    /// and full path. Directories whose stored names cannot be decoded are skipped by
    /// the reader, mirroring its mount/host behavior.
    /// </summary>
    /// <param name="reader">Reader positioned on the archive being walked.</param>
    /// <param name="directoryNode">Node handle of the directory to walk.</param>
    /// <param name="directoryPath">Full archive path of the directory being walked.</param>
    /// <param name="files">Collects every file discovered in the tree.</param>
    /// <param name="directoryCount">Counts the directories visited.</param>
    /// <param name="token">Token used to cancel the walk.</param>
    /// <param name="depth">Current recursion depth, used to enforce the nesting cap.</param>
    private static void WalkZarDirectory(ZArchiveReader reader, uint directoryNode, string directoryPath,
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        List<ZarFileEntry> files, ref int directoryCount, CancellationToken token, int depth = 0)
    {
        if (depth > MaxZarWalkDepth)
        {
            throw new InvalidDataException(
                $"ZAR archive directory nesting exceeds the supported depth ({MaxZarWalkDepth}).");
        }

        var count = reader.GetDirEntryCount(directoryNode);
        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();

            if (!reader.TryGetDirEntry(directoryNode, i, out var childNode, out var entry)) continue;

            var path = string.Equals(directoryPath, "/", StringComparison.Ordinal)
                ? "/" + entry.Name
                : directoryPath + "/" + entry.Name;

            if (entry.IsDirectory)
            {
                directoryCount++;
                WalkZarDirectory(reader, childNode, path, files, ref directoryCount, token, depth + 1);
            }
            else
            {
                files.Add(new ZarFileEntry(childNode, path, entry.Size));
            }
        }
    }

    /// <summary>
    /// Logs the Redump/XGD layout (generation, partitions, layer break, security sectors and
    /// warnings) for a passing image. Layout inspection is informational, so a container the
    /// inspector cannot read must not change the test verdict.
    /// </summary>
    /// <param name="isoPath">Path of the image that passed the structural test.</param>
    private void LogDiscLayout(string isoPath)
    {
        try
        {
            var layout = XisoDiscInfo.Read(isoPath, quiet: true);
            _logger.Information(
                "Disc layout: {DiscFormat}, {XgdType}, {TotalSectors} sectors, game partition at {GameOffset} ({GameLength}).",
                layout.DiscFormat, layout.XgdType,
                layout.TotalSectors.ToString("N0", CultureInfo.InvariantCulture),
                Formatter.FormatBytes(layout.GamePartition?.ByteOffset ?? 0),
                Formatter.FormatBytes(layout.GamePartition?.ByteLength ?? 0));

            foreach (var warning in layout.Warnings)
            {
                _logger.Information("  layout warning: {Warning:l}", warning);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                                       or UnauthorizedAccessException)
        {
            _logger.Debug(ex, "Could not inspect the disc layout of '{ImagePath}'", isoPath);
        }
    }

    /// <summary>
    /// Reads the whole image sequentially to exercise every sector, reporting progress in
    /// 1% steps.
    /// </summary>
    /// <param name="isoPath">Path of the image to scan.</param>
    /// <param name="progress">Receives progress updates during the scan.</param>
    /// <param name="token">Token used to cancel the scan.</param>
    /// <returns><c>true</c> when the entire image was read; otherwise <c>false</c>.</returns>
    private bool PerformSurfaceScan(string isoPath, IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        _logger.Information("Performing deep surface scan (sequential read of all sectors)...");

        try
        {
            using var stream = XisoReader.OpenImageStream(isoPath);
            var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024); // 4MB chunks
            try
            {
                var totalBytes = stream.Length;
                long bytesRead = 0;
                long lastReportedPercent = -1;

                while (bytesRead < totalBytes)
                {
                    token.ThrowIfCancellationRequested();

                    var toRead = (int)Math.Min(buffer.Length, totalBytes - bytesRead);
                    var read = stream.Read(buffer, 0, toRead);

                    if (read == 0)
                    {
                        _logger.Information("Surface scan failed: unexpected end of file at {BytesRead}.", bytesRead);
                        return false;
                    }

                    bytesRead += read;

                    // Report progress every 1%
                    var percent = bytesRead * 100 / totalBytes;
                    if (percent > lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        progress.Report(new BatchOperationProgress { StatusText = $"Surface scan: {percent}%" });
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Surface scan was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "Surface scan failed");
            return false;
        }

        _logger.Information("Surface scan completed successfully.");
        return true;
    }
}