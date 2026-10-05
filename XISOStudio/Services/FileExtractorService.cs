using System.Diagnostics;
using System.Runtime.InteropServices;
using XISOStudio.Interfaces;
using XISOStudio.Models;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace XISOStudio.Services;

/// <summary>
/// Extracts ISO images from archives using SharpCompress, with a 7-Zip command-line fallback
/// for unsupported compression methods. Guards against path traversal and checks for
/// sufficient disk space before writing.
/// </summary>
public class FileExtractorService : IFileExtractor
{
    private readonly ILogger _logger;
    private readonly string _sevenZipExePath;

    /// <summary>
    /// Locates the optional 7-Zip executable: an architecture-specific binary shipped with
    /// the application, a standard Windows installation, or a <c>7z</c> tool on the PATH
    /// of Linux and macOS.
    /// </summary>
    /// <returns>Full path of the 7-Zip executable, or <c>null</c> when none was found.</returns>
    private string? FindSevenZipExe()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;

        if (OperatingSystem.IsWindows())
        {
            var archExeName = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "7za.exe",
                Architecture.Arm64 => "7za_arm64.exe",
                _ => null
            };

            if (archExeName != null)
            {
                var archExe = Path.Combine(appDir, archExeName);
                if (File.Exists(archExe))
                    return archExe;
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var searchPaths = new[]
            {
                Path.Combine(programFiles, "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? programFiles, "7-Zip", "7z.exe")
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        // Linux/macOS: prefer the architecture-specific 7-Zip binary shipped next to the
        // application (release bundles include one), then fall back to a system 7-Zip on PATH.
        var bundledExeName = OperatingSystem.IsMacOS()
            ? "7zz_osx"
            : RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "7zz_linux_x64",
                Architecture.Arm64 => "7zz_linux_arm64",
                _ => null
            };

        if (bundledExeName != null)
        {
            var bundledExe = Path.Combine(appDir, bundledExeName);
            if (File.Exists(bundledExe)) return bundledExe;
        }

        // Look for a system 7-Zip on the PATH.
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable)) return null;

        string[] candidates = ["7z", "7za", "7zz", "7zr"];
        foreach (var dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    var fullPath = Path.Combine(dir.Trim('"'), candidate);
                    if (File.Exists(fullPath)) return fullPath;
                }
                catch (Exception ex)
                {
                    // Ignore malformed PATH entries
                    _logger.Debug(ex, "Ignoring malformed PATH entry while searching for 7-Zip: {PathEntry}", dir);
                }
            }
        }

        return null;
    }

    /// <summary>Platform-aware instructions for installing the optional 7-Zip CLI fallback.</summary>
    private static string SevenZipInstallHint =>
        OperatingSystem.IsWindows()
            ? "1. Install 7-Zip from https://7-zip.org/ — the app auto-detects it in Program Files.\n" +
              "2. Alternatively, place '7za.exe' (for x64) or '7za_arm64.exe' (for ARM64) in the application directory."
            : "1. Release bundles include 7-Zip next to the application ('7zz_linux_x64', '7zz_linux_arm64', or '7zz_osx'). " +
              "If it is missing, re-download the bundle or make the file executable with 'chmod +x'.\n" +
              "2. Alternatively install 7-Zip with your package manager (e.g. 'sudo apt install 7zip' or 'brew install sevenzip') so that '7z' is on the PATH.";

    // Cloud file attribute constants
    private const int FileAttributeRecallOnOpen = 0x00040000;
    private const int FileAttributeRecallOnDataAccess = 0x00400000;

    // Win32 error ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING (362 / 0x16A). IOException.HResult
    // always carries the full HRESULT (0x8007016A here), so only the low 16 bits are compared.
    private const int ErrorCloudFileProviderNotRunning = 0x16A;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileExtractorService"/> class and locates
    /// the optional 7-Zip executable.
    /// </summary>
    /// <param name="logger">Logger used to report extraction progress and failures.</param>
    public FileExtractorService(ILogger logger)
    {
        _logger = logger.ForContext<FileExtractorService>();
        _sevenZipExePath = FindSevenZipExe() ?? string.Empty;
    }

    /// <summary>
    /// Checks if a file is a cloud file (OneDrive, Dropbox, etc.) and not fully downloaded locally.
    /// </summary>
    /// <param name="filePath">Path of the file to inspect.</param>
    /// <returns><c>true</c> when the file is a placeholder that still needs to be downloaded; otherwise <c>false</c>.</returns>
    private bool IsCloudFile(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists) return false;

            var attributes = fileInfo.Attributes;
            return (attributes & (FileAttributes)FileAttributeRecallOnOpen) ==
                   (FileAttributes)FileAttributeRecallOnOpen ||
                   (attributes & (FileAttributes)FileAttributeRecallOnDataAccess) ==
                   (FileAttributes)FileAttributeRecallOnDataAccess;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not determine whether {FilePath} is a cloud file", filePath);
            return false;
        }
    }

    /// <summary>
    /// Attempts to ensure a cloud file is hydrated (downloaded locally) before accessing it.
    /// </summary>
    /// <param name="filePath">Path of the cloud file to hydrate.</param>
    /// <param name="token">Token used to cancel the hydration attempt.</param>
    /// <returns><c>true</c> when the file is available locally; otherwise <c>false</c>.</returns>
    private async Task<bool> EnsureCloudFileHydratedAsync(string filePath, CancellationToken token)
    {
        try
        {
            // Open the file with read access to trigger hydration
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous);

            // Read a small portion to ensure the file is fully available
            var buffer = new byte[1];
            _ = await fs.ReadAsync(buffer, token);

            return true;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a hydration failure: propagate it so the caller can
            // stop the batch instead of reporting a misleading cloud-provider error.
            throw;
        }
        catch (IOException ex) when
            ((ex.HResult & 0xFFFF) == ErrorCloudFileProviderNotRunning ||
             ex.Message.Contains("cloud file provider", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Debug(ex, "Cloud file provider is not running for {FilePath}", filePath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to hydrate cloud file {FilePath}", filePath);
            return false;
        }
    }

    /// <summary>
    /// Determines if an exception is related to cloud file provider issues.
    /// </summary>
    /// <param name="ex">Exception raised while accessing the file.</param>
    /// <returns><c>true</c> when the exception indicates a cloud file provider problem; otherwise <c>false</c>.</returns>
    private static bool IsCloudFileProviderError(Exception ex)
    {
        if (ex is IOException ioEx)
        {
            // Check for specific cloud file error codes and messages
            if ((ioEx.HResult & 0xFFFF) == ErrorCloudFileProviderNotRunning)
            {
                return true;
            }

            if (ioEx.Message.Contains("cloud file provider", StringComparison.OrdinalIgnoreCase) ||
                ioEx.Message.Contains("cloud file", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Verifies that the drive containing the specified path is ready.
    /// </summary>
    /// <param name="filePath">Path whose drive is probed.</param>
    private void VerifyDriveReady(string filePath)
    {
        try
        {
            var root = Path.GetPathRoot(filePath);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    throw new IOException($"The device is not ready. : '{filePath}'");
                }
            }
        }
        catch (IOException ex)
        {
            _logger.Debug(ex, "Drive readiness check failed for {FilePath}", filePath);
            throw;
        }
        catch (Exception ex)
        {
            // Environmental condition (drive probing failed); not an application defect.
            _logger.Information(ex, "Could not verify drive readiness: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Executes an asynchronous action with a brief retry on transient IOException
    /// (files locked by another process, network glitches). Permanent errors — corrupt
    /// archives, a full disk, unsupported content — are not retried.
    /// </summary>
    /// <param name="action">Asynchronous extraction step to execute.</param>
    /// <param name="operationDescription">Description used in the retry diagnostics.</param>
    /// <param name="token">Token used to cancel the retries and their delays.</param>
    private async Task ExecuteWithRetryAsync(Func<Task> action, string operationDescription, CancellationToken token)
    {
        const int maxRetries = 3;
        var attempt = 0;

        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (IOException ex) when (attempt < maxRetries && IsTransientIoError(ex))
            {
                attempt++;
                var delayMs = 1000 * (1 << (attempt - 1));
                _logger.Information(ex,
                    "{OperationDescription} failed on attempt {Attempt}/{MaxRetries}: {Message}. Retrying in {DelaySeconds}s...",
                    operationDescription, attempt, maxRetries, ex.Message, delayMs / 1000);
                await Task.Delay(delayMs, token);
            }
        }
    }

    /// <summary>
    /// Returns true for IO errors that may resolve on their own: a file locked by another
    /// process (antivirus, download manager) or a transient network failure.
    /// </summary>
    /// <param name="ex">IO exception to classify.</param>
    /// <returns><c>true</c> when the error may resolve on its own; otherwise <c>false</c>.</returns>
    internal static bool IsTransientIoError(IOException ex)
    {
        // Disk-full and hardware device failures are permanent conditions, not transient
        // glitches: retrying only delays the failure report. Device errors must be excluded
        // explicitly because the English "fatal device hardware error" message would
        // otherwise match the generic "device" network pattern below.
        if (PathHelper.IsDiskSpaceError(ex) || PathHelper.IsDeviceIoError(ex)) return false;

        // A file held open by another process (antivirus, download manager) is usually transient.
        if (PathHelper.IsFileInUseError(ex)) return true;

        return PathHelper.IsNetworkError(ex);
    }

    /// <summary>
    /// Checks if there is enough disk space on the target drive for the extraction.
    /// </summary>
    /// <param name="extractionPath">Destination folder whose drive is checked.</param>
    /// <param name="totalSize">Total uncompressed size of the extraction in bytes.</param>
    /// <param name="archiveFileName">Archive name used in the error message.</param>
    private void CheckDiskSpace(string extractionPath, long totalSize, string archiveFileName)
    {
        try
        {
            var fullPath = Path.GetFullPath(extractionPath);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return;

            var drive = new DriveInfo(root);
            // Saturating buffer: an archive declaring sizes near long.MaxValue must not wrap
            // around and make an undersized drive look sufficient.
            var requiredWithBuffer = PathHelper.AddSafetyBuffer(totalSize);
            if (drive.AvailableFreeSpace < requiredWithBuffer)
            {
                var requiredSpace = Formatter.FormatBytes(totalSize);
                var availableSpace = Formatter.FormatBytes(drive.AvailableFreeSpace);
                var errorMessage =
                    $"Not enough disk space to extract {archiveFileName}. Required: {requiredSpace} ({totalSize:N0} bytes), Available: {availableSpace} ({drive.AvailableFreeSpace:N0} bytes), with safety buffer requires: {Formatter.FormatBytes(requiredWithBuffer)}.";
                _logger.Information("{Message:l}", errorMessage);
                throw new IOException(errorMessage);
            }
        }
        catch (IOException ex)
        {
            _logger.Debug(ex, "Disk space check failed for {ExtractionPath}", extractionPath);
            throw;
        }
        catch (Exception ex)
        {
            // Environmental condition (drive probing failed); not an application defect.
            _logger.Information(ex, "Could not check disk space: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Reads the archive's table of contents and returns the total uncompressed size and file
    /// count of its non-directory entries.
    /// </summary>
    /// <param name="archivePath">Path of the archive to inspect.</param>
    /// <param name="token">Cancellation token for the operation.</param>
    /// <returns>A tuple containing the total uncompressed size and the number of files.</returns>
    public async Task<(long TotalUncompressedSize, int FileCount)> GetArchiveInfoAsync(string archivePath,
        CancellationToken token)
    {
        try
        {
            var (totalSize, fileCount) = await Task.Run(() =>
            {
                using var archive = ArchiveFactory.OpenArchive(archivePath);
                var entries = archive.Entries.Where(static e => !e.IsDirectory).ToList();
                var count = entries.Count;
                var size = entries.Sum(static e => e.Size);
                return (size, count);
            }, token);

            return (totalSize, fileCount);
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Archive info scan was canceled for {ArchivePath}", archivePath);
            throw;
        }
        catch (Exception ex) when (PathHelper.IsDiskSpaceError(ex) || PathHelper.IsNetworkError(ex) ||
                                   PathHelper.IsDeviceIoError(ex) || IsCloudFileProviderError(ex))
        {
            // Environmental failures (offline drive, network drop, cloud placeholder) are not
            // application defects and must not generate automatic bug reports.
            _logger.Information(ex, "Could not analyze archive {ArchivePath} due to an environmental error",
                archivePath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read archive info for {ArchivePath}", archivePath);
            throw;
        }
    }

    private bool IsFileLocked(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)) // ERROR_SHARING_VIOLATION
        {
            _logger.Debug(ex, "File is locked: {FilePath}", filePath);
            return true;
        }
    }

    /// <summary>
    /// Waits until the archive file is no longer locked by another process, using
    /// exponential backoff. Locks are typically transient (antivirus scans, downloads
    /// finishing, zip tools flushing) and resolve on their own within a few seconds.
    /// </summary>
    /// <param name="archivePath">Full path of the archive being waited on.</param>
    /// <param name="archiveFileName">Archive name used in the log and error messages.</param>
    /// <param name="token">Token used to cancel the wait.</param>
    private async Task WaitForFileUnlockedAsync(string archivePath, string archiveFileName, CancellationToken token)
    {
        const int maxAttempts = 6;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (!IsFileLocked(archivePath)) return;

            if (attempt < maxAttempts)
            {
                var delayMs = 1000 * (1 << (attempt - 1)); // 1s, 2s, 4s, 8s, 16s
                _logger.Information(
                    "{ArchiveFileName} is in use by another process. Waiting {DelaySeconds}s before retrying... (attempt {Attempt}/{MaxAttempts})",
                    archiveFileName, delayMs / 1000, attempt, maxAttempts - 1);
                await Task.Delay(delayMs, token);
            }
        }

        if (IsFileLocked(archivePath))
        {
            throw new IOException(
                $"The file '{archiveFileName}' is currently in use by another process. " +
                "Please close any programs that may have the file open (file explorer, zip tools, antivirus, download manager, etc.) and try again.");
        }
    }

    /// <summary>
    /// Extracts an archive with the 7-Zip command-line tool, killing the process when the
    /// operation is canceled.
    /// </summary>
    /// <param name="archivePath">Path of the archive to extract.</param>
    /// <param name="extractionPath">Folder that receives the extracted files.</param>
    /// <param name="token">Token used to cancel the extraction.</param>
    /// <returns><c>true</c> when 7-Zip extracted the archive successfully; otherwise <c>false</c>.</returns>
    private async Task<bool> TryExtractWithSevenZipCliAsync(string archivePath, string extractionPath,
        CancellationToken token)
    {
        var archiveFileName = Path.GetFileName(archivePath);
        _logger.Information("Extracting with 7-Zip CLI: {ArchiveFileName}", archiveFileName);

        try
        {
            var exeDir = Path.GetDirectoryName(_sevenZipExePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _sevenZipExePath,
                WorkingDirectory = exeDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // ArgumentList escapes each argument per platform; quotes in paths are legal on
            // Linux/macOS and would otherwise split the argument list.
            process.StartInfo.ArgumentList.Add("x");
            process.StartInfo.ArgumentList.Add(archivePath);
            process.StartInfo.ArgumentList.Add($"-o{extractionPath}");
            process.StartInfo.ArgumentList.Add("-y");

            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            var stderrTask = process.StandardError.ReadToEndAsync(token);

            await using (token.Register(state =>
                         {
                             var p = (Process?)state;
                             if (p is { HasExited: false })
                             {
                                 try
                                 {
                                     p.Kill();
                                 }
                                 catch (Exception ex)
                                 {
                                     _logger.Debug(ex, "Failed to kill 7-Zip CLI process after cancellation");
                                 }
                             }
                         }, process))
            {
                await process.WaitForExitAsync(token);
            }

            var errors = await stderrTask;

            try
            {
                await stdoutTask;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to read 7-Zip CLI stdout");
            }

            if (process.ExitCode == 0)
            {
                _logger.Information("Successfully extracted using 7-Zip CLI: {ArchiveFileName}", archiveFileName);
                return true;
            }

            _logger.Information("7-Zip CLI returned exit code {ExitCode}: {Errors}", process.ExitCode, errors);
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("7-Zip CLI extraction canceled for {ArchiveFileName}", archiveFileName);
            throw;
        }
        catch (Exception ex)
        {
            // The fallback failing is an input/environment condition, not an app defect.
            _logger.Information(ex, "7-Zip CLI fallback failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Extracts an archive into the specified folder, skipping unsafe or additional ISO entries
    /// and falling back to the 7-Zip CLI when SharpCompress cannot handle the compression method.
    /// </summary>
    /// <param name="archivePath">Path of the archive to extract.</param>
    /// <param name="extractionPath">Folder that receives the extracted files.</param>
    /// <param name="token">Cancellation token for the operation.</param>
    /// <returns>A result describing success and any entries that were deliberately skipped.</returns>
    public async Task<ArchiveExtractionResult> ExtractArchiveAsync(string archivePath, string extractionPath,
        CancellationToken token)
    {
        var archiveFileName = Path.GetFileName(archivePath);
        _logger.Information("Starting extraction: {ArchiveFileName}", archiveFileName);
        _logger.Information("Extraction target: {ExtractionPath}", extractionPath);

        // Entries the extractor deliberately leaves on disk (additional ISOs, unsafe paths).
        // The caller uses this to decide whether the source archive can be deleted safely.
        var skippedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Check for cloud files and attempt to hydrate before extraction
            if (IsCloudFile(archivePath))
            {
                _logger.Information(
                    "Detected cloud file: {ArchiveFileName}. Attempting to ensure local availability...",
                    archiveFileName);

                var hydrated = await EnsureCloudFileHydratedAsync(archivePath, token);
                if (!hydrated)
                {
                    throw new IOException(
                        $"The cloud file provider is not running. The file '{archivePath}' is stored in a cloud storage service " +
                        "(OneDrive, Dropbox, etc.) but the sync client is not available. Please ensure your cloud storage " +
                        "application is running and the file is fully synchronized before trying again.");
                }

                _logger.Information("Cloud file is now available locally.");
            }

            _logger.Information("Analyzing archive: {ArchiveFileName}...", archiveFileName);

            VerifyDriveReady(archivePath);

            // A locked archive is often transient (antivirus scanning the file right after
            // download, a zip tool still flushing, etc.) — wait and retry before giving up.
            await WaitForFileUnlockedAsync(archivePath, archiveFileName, token);

            await ExecuteWithRetryAsync(() =>
                {
                    return Task.Run(async () =>
                    {
                        try
                        {
                            using var archive = ArchiveFactory.OpenArchive(archivePath);
                            var entries = archive.Entries.Where(static e => !e.IsDirectory).ToList();
                            var fileCount = entries.Count;
                            var totalSize = entries.Sum(static e => e.Size);
                            var archiveFormat = archive.Type;

                            _logger.Information(
                                "Archive format: {ArchiveFormat}, Files to extract: {FileCount}, Total size: {TotalSize}",
                                archiveFormat, fileCount, Formatter.FormatBytes(totalSize));

                            CheckDiskSpace(extractionPath, totalSize, archiveFileName);

                            _logger.Information("Extracting files from {ArchiveFileName}...", archiveFileName);

                            var isoExtracted = false;

                            // Manually extract files to prevent "Zip Slip" (absolute paths or path traversal in archives)
                            foreach (var entry in entries)
                            {
                                token.ThrowIfCancellationRequested();

                                var entryPath = entry.Key;

                                // Strict Zip Slip check: Skip suspicious paths entirely
                                if (entryPath != null && (Path.IsPathRooted(entryPath) || entryPath.Split('\\', '/')
                                        .Any(static p => string.Equals(p, "..", StringComparison.OrdinalIgnoreCase))))
                                {
                                    _logger.Warning(
                                        "Skipping entry {EntryPath} - potential path traversal (Zip Slip) detected.",
                                        entryPath);
                                    skippedEntries.Add(entryPath);
                                    continue;
                                }

                                // Check for multiple ISOs
                                if (entryPath != null && Path.GetExtension(entryPath)
                                        .Equals(".iso", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (isoExtracted)
                                    {
                                        _logger.Information(
                                            "Skipping additional ISO: {EntryPath} (Only the first ISO is processed per archive).",
                                            entryPath);
                                        skippedEntries.Add(entryPath);
                                        continue;
                                    }

                                    isoExtracted = true;
                                }

                                if (entryPath != null)
                                {
                                    var fullDestPath = Path.GetFullPath(Path.Combine(extractionPath, entryPath));

                                    // Ensure the resulting path is still inside our extraction directory
                                    // Fix: base path must end with directory separator to prevent bypass via similar-named directories
                                    var basePath = Path.GetFullPath(extractionPath);
                                    if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString(),
                                            StringComparison.Ordinal))
                                    {
                                        basePath += Path.DirectorySeparatorChar;
                                    }

                                    if (!fullDestPath.StartsWith(basePath, PathHelper.PathComparison))
                                    {
                                        _logger.Warning(
                                            "Skipping entry {EntryPath} - potential path traversal (Zip Slip) detected.",
                                            entryPath);
                                        skippedEntries.Add(entryPath);
                                        continue;
                                    }

                                    Directory.CreateDirectory(Path.GetDirectoryName(fullDestPath) ??
                                                              throw new InvalidOperationException(
                                                                  "fullDestPath cannot be null"));
                                    await using var fs = new FileStream(fullDestPath, FileMode.Create,
                                        FileAccess.Write);
                                    entry.WriteTo(fs);
                                }
                            }
                        }
                        catch (NotSupportedException notSupportedEx) when (
                            notSupportedEx.Message.Contains("Unsupported compression method",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            // SharpCompress doesn't support this compression method (e.g., ZSTD/method 21).
                            // Fall back to 7-Zip CLI.
                            _logger.Warning(notSupportedEx,
                                "SharpCompress doesn't support this compression method ({Message}). Falling back to 7-Zip CLI...",
                                notSupportedEx.Message);

                            if (!File.Exists(_sevenZipExePath))
                            {
                                throw new IOException(
                                    "This archive uses a compression method (e.g., ZSTD) not supported by the built-in extractor.\n\n" +
                                    "To extract this file automatically, you can:\n" +
                                    SevenZipInstallHint + "\n\n" +
                                    "Alternatively, you can manually extract the archive and place the ISO file directly in the input folder.",
                                    notSupportedEx);
                            }

                            // Hand off to the shared async helper so the process observes cancellation.
                            if (!await TryExtractWithSevenZipCliAsync(archivePath, extractionPath, token))
                            {
                                throw new IOException(
                                    $"Unsupported archive: 7-Zip CLI extraction failed for '{archiveFileName}'.",
                                    notSupportedEx);
                            }

                            _logger.Information("Successfully extracted using 7-Zip CLI fallback: {ArchiveFileName}",
                                archiveFileName);
                        }
                    }, token);
                }, $"Extraction of {archiveFileName}", token);

            _logger.Information("Successfully extracted: {ArchiveFileName}", archiveFileName);
            return new ArchiveExtractionResult(true, skippedEntries.ToList());
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Extraction of {ArchiveFileName} was canceled.", archiveFileName);
            throw;
        }
        catch (Exception ex) when (Path.GetExtension(archivePath).Equals(".7z", StringComparison.OrdinalIgnoreCase) &&
                                   (ex is ArchiveException or ArchiveOperationException ||
                                    (ex is InvalidOperationException ioe &&
                                     ioe.Message.Contains("Archive", StringComparison.OrdinalIgnoreCase))))
        {
            _logger.Warning(ex, "SharpCompress unable to extract 7z ({ExceptionType}), falling back to 7-Zip CLI...",
                ex.GetType().Name);

            if (!File.Exists(_sevenZipExePath))
            {
                var userMessage = "7z archives require the 7-Zip command-line tool.\n\n" +
                                  "To extract .7z files automatically, you can:\n" +
                                  SevenZipInstallHint;
                _logger.Information("{Message:l}", userMessage);
                throw new IOException(userMessage, ex);
            }

            var cliResult = await TryExtractWithSevenZipCliAsync(archivePath, extractionPath, token);
            if (cliResult)
            {
                _logger.Information("Successfully extracted: {ArchiveFileName}", archiveFileName);
                return new ArchiveExtractionResult(true, skippedEntries.ToList());
            }

            _logger.Information("Extraction failed for {ArchiveFileName}.", archiveFileName);
            return ArchiveExtractionResult.Failed;
        }
        catch (NotSupportedException notSupportedEx) when (
            notSupportedEx.Message.Contains("Unsupported compression method", StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // ZIP archives using newer compression methods (e.g., method 21 = ZSTD) are not supported by SharpCompress.
            // Fall back to 7-Zip CLI which supports these methods.
            _logger.Warning(notSupportedEx,
                "ZIP archive uses unsupported compression method ({Message}). Falling back to 7-Zip CLI...",
                notSupportedEx.Message);

            if (!File.Exists(_sevenZipExePath))
            {
                var userMessage =
                    "This ZIP archive uses a compression method (e.g., ZSTD) not supported by the built-in extractor.\n\n" +
                    "To extract this file automatically, you can:\n" +
                    SevenZipInstallHint + "\n\n" +
                    "Alternatively, you can manually extract the ZIP and place the ISO file directly in the input folder.";
                _logger.Information("{Message:l}", userMessage);
                throw new IOException(userMessage, notSupportedEx);
            }

            var cliResult = await TryExtractWithSevenZipCliAsync(archivePath, extractionPath, token);
            if (cliResult)
            {
                _logger.Information("Successfully extracted: {ArchiveFileName}", archiveFileName);
                return new ArchiveExtractionResult(true, skippedEntries.ToList());
            }

            _logger.Information("Extraction failed for {ArchiveFileName}.", archiveFileName);
            return ArchiveExtractionResult.Failed;
        }
        catch (InvalidFormatException ex)
        {
            var userMessage =
                $"Error extracting {archiveFileName}: The archive uses a compression format not supported by SharpCompress.\n" +
                "Please ensure the file is a valid archive or use an alternative extraction tool.\n" +
                $"Exception: {ex.Message}";
            _logger.Information("{Message:l}", userMessage);
            throw new IOException(userMessage, ex);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Archive", StringComparison.OrdinalIgnoreCase))
        {
            var errorMessage = $"Error extracting {archiveFileName}: Could not open the archive.\n" +
                               "The archive may be corrupted or in an unsupported format.\n" +
                               $"Exception: {ex.Message}";
            _logger.Information("{Message:l}", errorMessage);
            throw;
        }
        catch (Exception ex) when (ex is ArchiveException or ArchiveOperationException)
        {
            var errorMessage =
                $"Error extracting {archiveFileName}: The archive appears to be invalid, corrupted, or in an unsupported format.\n" +
                "Please ensure the file is a valid archive (Zip, Rar, 7Zip, etc.).\n" +
                $"Exception: {ex.Message}";
            _logger.Information("{Message:l}", errorMessage);
            throw new IOException(errorMessage, ex);
        }
        catch (IOException ex) when (PathHelper.IsDiskSpaceError(ex))
        {
            var userMessage = $"Not enough disk space to extract {archiveFileName}.\n\n" +
                              "Please free up some space on your drive and try again.";
            _logger.Information("{Message:l}", userMessage);
            throw new IOException(userMessage, ex);
        }
        catch (IOException ex) when (PathHelper.IsDeviceIoError(ex))
        {
            var userMessage = $"Error extracting {archiveFileName}: The drive reported a hardware I/O error.\n\n" +
                              "This usually means the drive is failing, was disconnected, or has a hardware problem.\n" +
                              "Please check the drive connection and health (e.g. run chkdsk), or copy the archive to a local drive and try again.";
            _logger.Information(ex, "{Message:l}", userMessage);
            throw new IOException(userMessage, ex);
        }
        catch (IOException ex) when (IsCloudFileProviderError(ex))
        {
            // Provide user-friendly message for cloud file provider errors
            var userMessage =
                $"Cannot access {archiveFileName} because it is stored in cloud storage (OneDrive, Dropbox, etc.) " +
                "and the cloud sync provider is not running or the file is not fully synchronized.\n\n" +
                "Please try:\n" +
                "1. Ensure your cloud storage application (OneDrive, Dropbox, etc.) is running\n" +
                "2. Make sure the file is fully downloaded/synced to your local machine\n" +
                "3. Right-click the file in File Explorer and select 'Always keep on this device'\n" +
                "4. Try again once the file shows a solid checkmark (not a cloud icon)";

            _logger.Information("{Message:l}", userMessage);

            // Cloud file errors are environmental, do not report as bugs
            throw new IOException(userMessage, ex);
        }
        catch (CryptographicException)
        {
            _logger.Information(
                "{ArchiveFileName} is encrypted/password-protected. This application cannot extract password-protected archives. Please extract the archive manually using a tool that supports passwords (e.g., WinRAR, 7-Zip) and re-package it without encryption.",
                archiveFileName);

            return ArchiveExtractionResult.Failed;
        }
        catch (Exception ex)
        {
            // Provide user-friendly message for corrupt archives
            if (ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Information(
                    "The file '{ArchiveFileName}' is currently in use by another process. Close any programs that may have the file open (file explorer preview, zip tools, antivirus, download manager, etc.) and try again.",
                    archiveFileName);
            }
            else if (ex is EndOfStreamException ||
                     ex.Message.Contains("End of stream reached", StringComparison.OrdinalIgnoreCase) ||
                     ex.Message.Contains("Unable to read beyond the end of the stream",
                         StringComparison.OrdinalIgnoreCase))
            {
                _logger.Information(
                    "{ArchiveFileName} appears to be corrupt or incomplete. The file may have been damaged during download or transfer. Please re-download the archive and try again.",
                    archiveFileName);
            }
            else if (ex.Message.Contains("Bad state", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Information(
                    "{ArchiveFileName} appears to be corrupt (invalid compression data). The file may have been damaged during download or transfer. Please re-download the archive and try again.",
                    archiveFileName);
            }
            else if (PathHelper.IsDiskSpaceError(ex))
            {
                _logger.Information(
                    "Not enough disk space to extract {ArchiveFileName}. Please free up some space on your drive and try again.",
                    archiveFileName);
            }
            else if (PathHelper.IsNetworkError(ex))
            {
                _logger.Information(
                    "Network error while extracting {ArchiveFileName}. The file may be on a network drive that is no longer available or experiencing connectivity issues.\n\n" +
                    "Please try:\n" +
                    "1. Check that the network drive is still connected and accessible\n" +
                    "2. Copy the file to a local drive before processing\n" +
                    "3. Check your network connection stability\n" +
                    "4. If using WiFi, try a wired connection for better reliability",
                    archiveFileName);
            }
            else if (ex is UnauthorizedAccessException)
            {
                // Permission problems are environmental, not an application defect.
                _logger.Information(
                    "Access denied while extracting {ArchiveFileName}. Check the file/folder permissions and try again.",
                    archiveFileName);
            }
            else
            {
                _logger.Error(ex, "Error extracting {ArchiveFileName}: {Message}", archiveFileName, ex.Message);
            }

            throw;
        }
    }
}