using System.Globalization;
using XISOStudio.Interfaces;
using XISOStudio.Models;
using Serilog;
using XISOSharp;

namespace XISOStudio.Services;

/// <summary>
/// Runs batch conversion and integrity testing of Xbox images: enumerates the requested
/// inputs, extracts archives, delegates the per-file work to the conversion services and
/// reports progress to the caller.
/// </summary>
public class OrchestratorService : IOrchestratorService
{
    /// <summary>Extracts archive contents to a temporary folder.</summary>
    private readonly IFileExtractor _fileExtractor;

    /// <summary>Moves tested images into the success or failed folder.</summary>
    private readonly IFileMover _fileMover;

    /// <summary>Logger used for diagnostics.</summary>
    private readonly ILogger _logger;

    /// <summary>Validates image structure and readability.</summary>
    private readonly IXisoIntegrityService _integrityService;

    /// <summary>Converts images to XISO, ZAR or CSO.</summary>
    private readonly IXisoSharpService _xisoSharpService;

    /// <summary>Converts images to CHD.</summary>
    private readonly IChdService _chdService;

    /// <summary>Resolves temporary directories based on free disk space.</summary>
    private readonly IDiskMonitorService _diskMonitorService;

    /// <summary>
    /// Per-batch shared state: tracks the next global file index and the output paths that
    /// processed files have already reserved.
    /// </summary>
    private class ProcessingContext
    {
        /// <summary>
        /// Gets or sets the one-based index of the next file in the batch, used to generate
        /// unique temporary file names.
        /// </summary>
        internal int GlobalFileIndex { get; set; } = 1;

        /// <summary>Output paths already reserved by processed files in this batch.</summary>
        private readonly HashSet<string> _reservedOutputPaths = new(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        /// <summary>
        ///     Returns an output path for the given base name that no other file in this batch
        ///     has used yet. Two inputs with the same file name (for example
        ///     <c>Disc1/game.iso</c> and <c>Disc2/game.iso</c>) would otherwise overwrite each
        ///     other; with "delete originals" enabled that loses one result and both sources.
        /// </summary>
        /// <param name="outputFolder">Folder where the output file is written.</param>
        /// <param name="baseName">Base file name of the source file, without extension.</param>
        /// <param name="extension">Extension (including the leading dot) of the requested output format.</param>
        /// <returns>A unique output path that no earlier file in the batch has reserved.</returns>
        internal string ReserveOutputPath(string outputFolder, string baseName, string extension)
        {
            var candidate = Path.Combine(outputFolder, baseName + extension);
            for (var counter = 2; !_reservedOutputPaths.Add(candidate); counter++)
            {
                candidate = Path.Combine(outputFolder, $"{baseName} ({counter}){extension}");
            }

            return candidate;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OrchestratorService"/> class.
    /// </summary>
    /// <param name="fileExtractor">Extracts archive contents to a temporary folder.</param>
    /// <param name="fileMover">Moves tested images into the success or failed folder.</param>
    /// <param name="logger">Logger used for diagnostics.</param>
    /// <param name="integrityService">Validates image structure and readability.</param>
    /// <param name="xisoSharpService">Converts images to XISO, ZAR or CSO.</param>
    /// <param name="chdService">Converts images to CHD.</param>
    /// <param name="diskMonitorService">Resolves temporary directories based on free disk space.</param>
    public OrchestratorService(
        IFileExtractor fileExtractor,
        IFileMover fileMover,
        ILogger logger,
        IXisoIntegrityService integrityService,
        IXisoSharpService xisoSharpService,
        IChdService chdService,
        IDiskMonitorService diskMonitorService)
    {
        _fileExtractor = fileExtractor;
        _fileMover = fileMover;
        _logger = logger.ForContext<OrchestratorService>();
        _integrityService = integrityService;
        _xisoSharpService = xisoSharpService;
        _chdService = chdService;
        _diskMonitorService = diskMonitorService;
    }

    #region Conversion Logic

    /// <summary>
    /// Converts every convertible file in <paramref name="inputFolder"/> to the requested
    /// output format and writes the results to <paramref name="outputFolder"/>.
    /// </summary>
    /// <param name="inputFolder">Folder to scan for convertible files.</param>
    /// <param name="outputFolder">Folder where the converted files are written.</param>
    /// <param name="deleteOriginals">Whether to delete each source file after a successful conversion.</param>
    /// <param name="skipSystemUpdate">Whether to remove the $SystemUpdate folder from the output image.</param>
    /// <param name="checkIntegrity">Whether to validate the image structure during conversion.</param>
    /// <param name="outputFormat">The format to convert to.</param>
    /// <param name="searchSubfolders">Whether to include files in subfolders of the input folder.</param>
    /// <param name="progress">Receives progress updates for the batch.</param>
    /// <param name="onCloudRetryRequired">Callback invoked when a cloud file cannot be read and a retry decision is needed.</param>
    /// <param name="token">Token used to cancel the batch.</param>
    /// <returns>A task that completes when the batch conversion has finished.</returns>
    public Task ConvertAsync(
        string inputFolder,
        string outputFolder,
        bool deleteOriginals,
        bool skipSystemUpdate,
        bool checkIntegrity,
        OutputFormat outputFormat,
        bool searchSubfolders,
        IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired,
        CancellationToken token)
    {
        return RunWithErrorHandlingAsync("ConvertAsync", async () =>
        {
            if (!Directory.Exists(inputFolder))
            {
                throw new IOException(
                    $"The input folder does not exist or is not accessible: '{inputFolder}'\n\n" +
                    "Possible causes:\n" +
                    "• The folder was deleted, moved, or renamed\n" +
                    "• The folder is on a network drive that is disconnected\n" +
                    "• The folder is a cloud placeholder (OneDrive, Dropbox) that hasn't been synced\n" +
                    "• The path contains characters that are not supported by the file system\n\n" +
                    "Please verify the folder exists and try again.");
            }

            _logger.Information("Starting conversion. Input: {InputFolder}, Output: {OutputFolder}", inputFolder,
                outputFolder);

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = searchSubfolders
            };

            List<string> topLevelEntries = [];
            const int maxRetries = 3;
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    topLevelEntries = await Task.Run(
                        () => Directory.GetFiles(inputFolder, "*.*", enumOptions)
                            .Where(SupportedFiles.IsConvertible).ToList(), token);
                    break;
                }
                catch (DirectoryNotFoundException ex)
                {
                    _logger.Information(ex, "Input folder not found: {InputFolder}", inputFolder);
                    throw new IOException(
                        $"The input folder was not found: '{inputFolder}'\n\n" +
                        "The folder may have been deleted, moved, or is a cloud placeholder that hasn't been synced.\n" +
                        $"Original error: {ex.Message}",
                        ex);
                }
                catch (IOException ex) when (attempt < maxRetries)
                {
                    _logger.Information(ex, "Folder scan attempt {Attempt}/{MaxRetries} failed; retrying", attempt,
                        maxRetries);
                    await Task.Delay(attempt * 2000, token);
                }
            }

            if (topLevelEntries.Count == 0)
            {
                _logger.Information("No convertible files found in {InputFolder}", inputFolder);
                return;
            }

            _logger.Information("Found {FileCount} file(s) to process in {InputFolder}", topLevelEntries.Count,
                inputFolder);

            await ConvertEntriesCoreAsync(topLevelEntries, outputFolder, deleteOriginals, skipSystemUpdate,
                checkIntegrity, outputFormat, progress, onCloudRetryRequired, token);
        });
    }

    /// <summary>
    /// Converts the given files to the requested output format and writes the results to
    /// <paramref name="outputFolder"/>.
    /// </summary>
    /// <param name="files">The files to convert; files that are not convertible are ignored.</param>
    /// <param name="outputFolder">Folder where the converted files are written.</param>
    /// <param name="deleteOriginals">Whether to delete each source file after a successful conversion.</param>
    /// <param name="skipSystemUpdate">Whether to remove the $SystemUpdate folder from the output image.</param>
    /// <param name="checkIntegrity">Whether to validate the image structure during conversion.</param>
    /// <param name="outputFormat">The format to convert to.</param>
    /// <param name="progress">Receives progress updates for the batch.</param>
    /// <param name="onCloudRetryRequired">Callback invoked when a cloud file cannot be read and a retry decision is needed.</param>
    /// <param name="token">Token used to cancel the batch.</param>
    /// <returns>A task that completes when the batch conversion has finished.</returns>
    public Task ConvertFilesAsync(
        IReadOnlyList<string> files,
        string outputFolder,
        bool deleteOriginals,
        bool skipSystemUpdate,
        bool checkIntegrity,
        OutputFormat outputFormat,
        IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired,
        CancellationToken token)
    {
        return RunWithErrorHandlingAsync("ConvertFilesAsync", async () =>
        {
            var convertibleFiles = files.Where(SupportedFiles.IsConvertible).ToList();
            if (convertibleFiles.Count == 0)
            {
                _logger.Information("No convertible files selected for conversion.");
                return;
            }

            _logger.Information("Starting conversion of {FileCount} selected file(s). Output: {OutputFolder}",
                convertibleFiles.Count, outputFolder);

            await ConvertEntriesCoreAsync(convertibleFiles, outputFolder, deleteOriginals, skipSystemUpdate,
                checkIntegrity, outputFormat, progress, onCloudRetryRequired, token);
        });
    }

    /// <summary>
    /// Converts the given batch entries, unpacking archives and delegating every image to the
    /// conversion services while reporting progress and cleaning up temporary folders.
    /// </summary>
    /// <param name="entries">Paths of the files to process.</param>
    /// <param name="outputFolder">Folder where the converted files are written.</param>
    /// <param name="deleteOriginals">Whether to delete each source file after a successful conversion.</param>
    /// <param name="skipSystemUpdate">Whether to remove the $SystemUpdate folder from the output image.</param>
    /// <param name="checkIntegrity">Whether to validate the image structure during conversion.</param>
    /// <param name="outputFormat">The format to convert to.</param>
    /// <param name="progress">Receives progress updates for the batch.</param>
    /// <param name="onCloudRetryRequired">Callback invoked when a cloud file cannot be read and a retry decision is needed.</param>
    /// <param name="token">Token used to cancel the batch.</param>
    private async Task ConvertEntriesCoreAsync(
        IReadOnlyList<string> entries,
        string outputFolder,
        bool deleteOriginals,
        bool skipSystemUpdate,
        bool checkIntegrity,
        OutputFormat outputFormat,
        IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired,
        CancellationToken token)
    {
        progress.Report(new BatchOperationProgress { TotalFiles = entries.Count });

        var tempFoldersToCleanUp = new List<string>();
        var context = new ProcessingContext();
        var topLevelProcessed = 0;

        try
        {
            foreach (var entryPath in entries)
            {
                token.ThrowIfCancellationRequested();

                // Check file existence on a background thread to avoid blocking UI for cloud/slow files
                var fileExists = await Task.Run(() => File.Exists(entryPath), token);
                if (!fileExists)
                {
                    progress.Report(new BatchOperationProgress
                    {
                        LogMessage = $"Error: Source file not found: {entryPath}. Skipping.", FailedCount = 1,
                        FailedPathToAdd = entryPath
                    });
                    _logger.Information("Source file not found, skipping: {FilePath}", entryPath);
                    topLevelProcessed++;
                    progress.Report(new BatchOperationProgress { ProcessedCount = topLevelProcessed });
                    continue;
                }

                var fileName = Path.GetFileName(entryPath);
                var extension = Path.GetExtension(entryPath).ToLowerInvariant();
                progress.Report(new BatchOperationProgress
                {
                    StatusText = $"Processing: {fileName}", CurrentDrive = PathHelper.GetDriveLetter(entryPath)
                });

                try
                {
                    switch (extension)
                    {
                        case ".iso":
                            var isoStatus = await ConvertFileInternalAsync(entryPath, outputFolder, deleteOriginals,
                                context.GlobalFileIndex++, context, skipSystemUpdate, checkIntegrity, outputFormat,
                                progress, onCloudRetryRequired, token);
                            ReportStatus(isoStatus, entryPath, progress);
                            break;

                        case ".zip" or ".7z" or ".rar":
                            await ProcessArchiveAsync(entryPath, outputFolder, deleteOriginals, skipSystemUpdate,
                                checkIntegrity, outputFormat, context, tempFoldersToCleanUp, progress,
                                onCloudRetryRequired, token);
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.Debug("Conversion canceled while processing {FileName}", fileName);
                    throw;
                }
                catch (Exception ex) when (PathHelper.IsDiskSpaceError(ex))
                {
                    // Stop the batch — no point continuing without disk space
                    progress.Report(new BatchOperationProgress
                    {
                        LogMessage =
                            "ERROR: Not enough disk space on the output drive. Batch operation stopped. Please free up disk space and try again.",
                        FailedCount = 1,
                        FailedPathToAdd = entryPath
                    });
                    _logger.Information(ex, "Not enough disk space; batch conversion stopped on {FileName}",
                        fileName);
                    throw;
                }
                catch (Exception ex) when (IsFatalEnvironmentalError(ex))
                {
                    // Stop the batch — no point continuing if the output drive or network is disconnected
                    progress.Report(new BatchOperationProgress
                    {
                        LogMessage =
                            $"FATAL ERROR: The output device or path is not available: {ex.Message}. Batch operation stopped.",
                        FailedCount = 1,
                        FailedPathToAdd = entryPath
                    });
                    _logger.Information(ex,
                        "Output device or path unavailable; batch conversion stopped on {FileName}",
                        fileName);
                    throw;
                }
                catch (Exception ex)
                {
                    // Provide user-friendly message for corrupt archives
                    string logMessage;
                    if (ex.Message.Contains("End of stream reached", StringComparison.OrdinalIgnoreCase))
                    {
                        logMessage =
                            $"ERROR: {fileName} appears to be corrupt or incomplete. The file may have been damaged during download or transfer. Please re-download the archive and try again.";
                    }
                    else
                    {
                        logMessage = $"Critical error processing {fileName}: {ex.Message}";
                    }

                    progress.Report(new BatchOperationProgress
                        { LogMessage = logMessage, FailedCount = 1, FailedPathToAdd = entryPath });

                    // Filter environmental errors (disconnected drives, network issues, etc.)
                    var isEnvironmentalError = IsFatalEnvironmentalError(ex) || PathHelper.IsNetworkError(ex);

                    // Filter common archive errors (corruption, incomplete downloads, etc.)
                    var isArchiveError = ex.Message.Contains("Data error", StringComparison.OrdinalIgnoreCase) ||
                                         ex.Message.Contains("Invalid archive",
                                             StringComparison.OrdinalIgnoreCase) ||
                                         ex.Message.Contains("Unsupported archive",
                                             StringComparison.OrdinalIgnoreCase) ||
                                         ex.Message.Contains("End of stream reached",
                                             StringComparison.OrdinalIgnoreCase);

                    if (!isEnvironmentalError && !isArchiveError)
                    {
                        _logger.Error(ex, "Orchestrator error on {FileName}", fileName);
                    }
                    else
                    {
                        _logger.Information(ex, "Handled processing error on {FileName}", fileName);
                    }
                }

                topLevelProcessed++;
                progress.Report(new BatchOperationProgress { ProcessedCount = topLevelProcessed });
            }
        }
        finally
        {
            // Cleanup must finish even when the batch token is canceled: passing the
            // canceled token would abort the retry loop and leak the temp folders.
            await CleanupTempFoldersAsync(tempFoldersToCleanUp, progress);
        }

        _logger.Information("Batch conversion completed. Processed {ProcessedCount} of {TotalFiles} file(s)",
            topLevelProcessed, entries.Count);
    }

    /// <summary>
    /// Runs a batch operation, logging failures with the operation name and rethrowing them so
    /// the caller can report them.
    /// </summary>
    /// <param name="operationName">Name of the operation, used in log messages.</param>
    /// <param name="operation">Delegate that performs the operation.</param>
    private async Task RunWithErrorHandlingAsync(string operationName, Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("OrchestratorService.{OperationName} canceled", operationName);
            throw;
        }
        catch (Exception ex)
        {
            // The caller (MainWindow) logs the rethrown failure; unexpected defects must be
            // visible to the bug-report sink, so they are logged at Error here.
            if (PathHelper.IsDiskSpaceError(ex) || PathHelper.IsNetworkError(ex) || IsFatalEnvironmentalError(ex) ||
                ex is IOException)
            {
                _logger.Information(ex, "OrchestratorService.{OperationName} stopped due to an environmental error",
                    operationName);
            }
            else
            {
                _logger.Error(ex, "OrchestratorService.{OperationName} failed", operationName);
            }

            throw;
        }
    }

    /// <summary>
    /// Resolves a temporary directory that can hold the required number of bytes.
    /// </summary>
    /// <param name="requiredSize">Number of bytes the temporary directory must be able to hold.</param>
    /// <param name="tempSubfolder">Name of the subfolder to create under the temporary root.</param>
    /// <returns>Full path of the temporary directory to use.</returns>
    private string ResolveTempDirectory(long requiredSize, string tempSubfolder)
    {
        return PathHelper.ResolveTempDirectory(requiredSize, tempSubfolder, _diskMonitorService);
    }

    /// <summary>
    /// Extracts an archive to a temporary folder, converts the ISOs it contains, and deletes
    /// the archive when deletion is enabled and every entry was converted.
    /// </summary>
    /// <param name="archivePath">Path of the archive to process.</param>
    /// <param name="outputFolder">Folder where the converted files are written.</param>
    /// <param name="deleteOriginal">Whether to delete the archive after a fully successful batch.</param>
    /// <param name="skipUpdate">Whether to remove the $SystemUpdate folder from the output images.</param>
    /// <param name="checkIntegrity">Whether to validate the image structure during conversion.</param>
    /// <param name="outputFormat">The format to convert to.</param>
    /// <param name="context">Per-batch shared state used for file indexing and output path reservation.</param>
    /// <param name="tempFolders">Temporary folders registered for cleanup after the batch.</param>
    /// <param name="progress">Receives progress updates for the archive.</param>
    /// <param name="cloudRetry">Callback invoked when a cloud file cannot be read and a retry decision is needed.</param>
    /// <param name="token">Token used to cancel the operation.</param>
    private async Task ProcessArchiveAsync(string archivePath, string outputFolder, bool deleteOriginal,
        bool skipUpdate, bool checkIntegrity, OutputFormat outputFormat, ProcessingContext context,
        List<string> tempFolders, IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> cloudRetry, CancellationToken token)
    {
        string tempDir;
        try
        {
            progress.Report(new BatchOperationProgress { LogMessage = "Analyzing archive for required space..." });
            var (totalSize, fileCount) = await _fileExtractor.GetArchiveInfoAsync(archivePath, token);
            tempDir = ResolveTempDirectory(totalSize, "XISOStudio_Extract");
            progress.Report(new BatchOperationProgress
            {
                LogMessage =
                    $"Archive contains {fileCount} files ({Formatter.FormatBytes(totalSize)} uncompressed). Extracting to: {Path.GetDirectoryName(tempDir)}"
            });
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Archive processing canceled: {ArchivePath}", archivePath);
            throw;
        }
        catch (IOException ex) when (ex.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase) ||
                                     ex.Message.Contains("Not enough disk space", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Information(ex, "Not enough disk space to extract archive: {ArchivePath}", archivePath);
            throw;
        }
        catch (Exception ex)
        {
            // If we can't analyze the archive, fall back to default temp and let ExtractArchiveAsync handle it.
            // Environmental failures (offline drive, device error, network drop) are not application
            // defects and must not generate automatic bug reports.
            progress.Report(new BatchOperationProgress
                { LogMessage = $"Could not analyze archive: {ex.Message}. Using default temp path." });

            if (IsFatalEnvironmentalError(ex) || PathHelper.IsNetworkError(ex) || PathHelper.IsDiskSpaceError(ex))
            {
                _logger.Information(ex,
                    "Could not analyze archive {ArchivePath} due to an environmental error; using default temp path",
                    archivePath);
            }
            else
            {
                _logger.Warning(ex, "Could not analyze archive {ArchivePath}; using default temp path", archivePath);
            }

            tempDir = Path.Combine(Path.GetTempPath(), "XISOStudio_Extract", Guid.NewGuid().ToString());
        }

        tempFolders.Add(tempDir);

        bool extracted;
        var internalFail = false;
        var internalSuccess = false;
        var convertedCount = 0;
        var skippedCount = 0;
        var isoFileCount = 0;
        List<string> unprocessedImages = [];
        IReadOnlyList<string> skippedEntries = [];

        try
        {
            Directory.CreateDirectory(tempDir);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var extractionResult = await _fileExtractor.ExtractArchiveAsync(archivePath, tempDir, linkedCts.Token);
            extracted = extractionResult.Success;
            skippedEntries = extractionResult.SkippedEntries;

            if (extracted)
            {
                var extractedFiles = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories);
                var isoFiles = extractedFiles.Where(SupportedFiles.IsIso).ToList();
                isoFileCount = isoFiles.Count;

                // Images that are not plain ISOs (CSO/ZAR/CHD, including the continuation
                // parts of a split CISO set) are extracted but never converted, so they
                // keep the archive alive when deletion is enabled.
                unprocessedImages =
                [
                    .. extractedFiles.Where(file => !SupportedFiles.IsIso(file) && SupportedFiles.IsImage(file))
                ];

                foreach (var file in isoFiles)
                {
                    token.ThrowIfCancellationRequested();
                    var status = await ConvertFileInternalAsync(file, outputFolder, false, context.GlobalFileIndex++,
                        context, skipUpdate, checkIntegrity, outputFormat, progress, cloudRetry, token);

                    switch (status)
                    {
                        case FileProcessingStatus.Converted:
                            internalSuccess = true;
                            convertedCount++;
                            break;
                        case FileProcessingStatus.InvalidInput:
                            // An invalid image keeps the archive alive (internalFail) and is
                            // reported to the UI so the "invalid ISOs" warning stays accurate.
                            internalFail = true;
                            progress.Report(new BatchOperationProgress { InvalidIsoCount = 1 });
                            break;
                        case FileProcessingStatus.Failed:
                            internalFail = true;
                            break;
                        case FileProcessingStatus.AlreadyOptimized:
                        case FileProcessingStatus.Skipped:
                            // The image was not rewritten.
                            skippedCount++;
                            break;
                    }
                }
            }
            else
            {
                internalFail = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Re-throw cancellation exceptions to stop the batch
            _logger.Debug("Archive extraction canceled: {ArchivePath}", archivePath);
            throw;
        }
        catch (Exception ex) when (PathHelper.IsDiskSpaceError(ex))
        {
            // Stop the batch — no point continuing without disk space
            _logger.Information(ex, "Not enough disk space while extracting archive: {ArchivePath}", archivePath);
            throw;
        }
        catch (Exception ex) when (IsFatalEnvironmentalError(ex))
        {
            // Stop the batch — no point continuing if the output drive or network is disconnected
            _logger.Information(ex, "Output device or path unavailable while extracting archive: {ArchivePath}",
                archivePath);
            throw;
        }
        catch (Exception ex)
        {
            // Mark as failed, but don't stop the batch processing. Environmental and archive
            // errors (locked/corrupt archive, permissions) are expected input conditions;
            // anything else is unexpected and must stay visible as an application error.
            var isEnvironmentalError = PathHelper.IsNetworkError(ex) || IsFatalEnvironmentalError(ex) ||
                                       ex is UnauthorizedAccessException or IOException or InvalidDataException;
            var isArchiveError = ex.Message.Contains("Data error", StringComparison.OrdinalIgnoreCase) ||
                                 ex.Message.Contains("Invalid archive", StringComparison.OrdinalIgnoreCase) ||
                                 ex.Message.Contains("Unsupported archive", StringComparison.OrdinalIgnoreCase) ||
                                 ex.Message.Contains("End of stream reached", StringComparison.OrdinalIgnoreCase);

            if (isEnvironmentalError || isArchiveError)
            {
                _logger.Information(ex, "Archive processing failed for {ArchivePath}; continuing batch", archivePath);
            }
            else
            {
                _logger.Error(ex, "Archive processing failed for {ArchivePath}; continuing batch", archivePath);
            }

            internalFail = true;
            extracted = false;
        }
        finally
        {
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(tempDir, 3, 1000, _logger,
                CancellationToken.None);
            tempFolders.Remove(tempDir);
        }

        if (internalFail || !extracted)
            progress.Report(new BatchOperationProgress { FailedCount = 1, FailedPathToAdd = archivePath });
        else if (internalSuccess) progress.Report(new BatchOperationProgress { SuccessCount = 1 });
        else progress.Report(new BatchOperationProgress { SkippedCount = 1 });

        // Only remove the archive when every entry was extracted and every extracted image
        // was converted. Skipped entries (additional ISOs, unsafe paths), skipped images
        // (already optimized) and unconverted images (CSO/CHD/ZAR) all mean the archive
        // still holds the only copy of that content.
        var everyEntryExtracted = skippedEntries.Count == 0;
        var everyImageConverted = convertedCount == isoFileCount && skippedCount == 0 &&
                                  unprocessedImages.Count == 0;

        if (deleteOriginal && extracted && internalSuccess && !internalFail && everyEntryExtracted &&
            everyImageConverted)
        {
            try
            {
                File.Delete(archivePath);
            }
            catch (Exception ex)
            {
                // The user must know the original is still there; environmental, not a defect.
                progress.Report(new BatchOperationProgress
                {
                    LogMessage =
                        $"Warning: Could not delete original archive {Path.GetFileName(archivePath)}: {ex.Message}"
                });
                _logger.Information(ex, "Could not delete original archive {ArchivePath}", archivePath);
            }
        }
        else if (deleteOriginal && extracted && !internalFail)
        {
            ReportKeptArchive(progress, skippedEntries, unprocessedImages, skippedCount);
        }
        else if (deleteOriginal)
        {
            progress.Report(new BatchOperationProgress
            {
                LogMessage = "Keeping the original archive because processing was not fully successful."
            });
        }
    }

    /// <summary>
    ///     Explains why an archive was kept when "delete originals" is enabled but not every
    ///     entry could be converted (deleting it would lose content).
    /// </summary>
    /// <param name="progress">Receives the explanation messages.</param>
    /// <param name="skippedEntries">Archive entries that could not be extracted.</param>
    /// <param name="unprocessedImages">Extracted images that are not plain ISOs and were not converted.</param>
    /// <param name="skippedImages">Number of images that were already optimized and not rewritten.</param>
    private static void ReportKeptArchive(IProgress<BatchOperationProgress> progress,
        IReadOnlyList<string> skippedEntries, List<string> unprocessedImages, int skippedImages)
    {
        if (skippedEntries.Count > 0)
        {
            progress.Report(new BatchOperationProgress
            {
                LogMessage =
                    $"Keeping the original archive: {skippedEntries.Count} entry(ies) were not extracted " +
                    $"(for example '{Path.GetFileName(skippedEntries[0])}')."
            });
        }

        if (unprocessedImages.Count > 0)
        {
            progress.Report(new BatchOperationProgress
            {
                LogMessage =
                    $"Keeping the original archive: {unprocessedImages.Count} extracted image(s) are not plain " +
                    "ISOs and were not converted."
            });
        }

        if (skippedImages > 0)
        {
            progress.Report(new BatchOperationProgress
            {
                LogMessage =
                    $"Keeping the original archive: {skippedImages} image(s) were already optimized and were " +
                    "not rewritten."
            });
        }
    }

    /// <summary>
    /// Converts a single image, copying unreadable cloud files to a local temporary file
    /// first, and optionally deletes the original after a successful conversion.
    /// </summary>
    /// <param name="inputFile">Path of the source image to convert.</param>
    /// <param name="outputFolder">Folder where the converted file is written.</param>
    /// <param name="deleteOriginal">Whether to delete the source file after a successful conversion.</param>
    /// <param name="fileIndex">One-based index of the file, used to generate temporary file names.</param>
    /// <param name="context">Per-batch shared state used for output path reservation.</param>
    /// <param name="skipSystemUpdate">Whether to remove the $SystemUpdate folder from the output image.</param>
    /// <param name="checkIntegrity">Whether to validate the image structure during conversion.</param>
    /// <param name="outputFormat">The format to convert to.</param>
    /// <param name="progress">Receives progress updates for the file.</param>
    /// <param name="onCloudRetryRequired">Callback invoked when a cloud file cannot be read and a retry decision is needed.</param>
    /// <param name="token">Token used to cancel the conversion.</param>
    /// <returns>The outcome of the conversion.</returns>
    private async Task<FileProcessingStatus> ConvertFileInternalAsync(string inputFile, string outputFolder,
        bool deleteOriginal, int fileIndex, ProcessingContext context, bool skipSystemUpdate, bool checkIntegrity,
        OutputFormat outputFormat, IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired, CancellationToken token)
    {
        var originalFileName = Path.GetFileName(inputFile);
        string? localTempWorkingDir = null;

        try
        {
            // Handle cloud files first: copy to local temp when the source is not directly readable
            var sourcePath = inputFile;

            try
            {
                // Simple check if file is accessible (triggers cloud download if hydration is automatic, or fails)
                // Run on background thread to avoid blocking UI for cloud/slow files
                await Task.Run(() =>
                {
                    using var stream = File.OpenRead(inputFile);
                }, token);
            }
            catch (IOException)
            {
                _logger.Debug("File {FileName} is not directly readable; copying to a local temp file",
                    originalFileName);
                try
                {
                    var fileSize = new FileInfo(inputFile).Length;
                    localTempWorkingDir = ResolveTempDirectory(fileSize, "XISOStudio_Convert");
                }
                catch (IOException ex) when (PathHelper.IsDiskSpaceError(ex))
                {
                    _logger.Information(ex, "Not enough disk space while preparing conversion of {FileName}",
                        originalFileName);
                    throw;
                }
                catch (Exception ex)
                {
                    progress.Report(new BatchOperationProgress
                    {
                        LogMessage =
                            $"Could not resolve temp directory for copy: {ex.Message}. Falling back to default temp path."
                    });
                    _logger.Warning(ex,
                        "Could not resolve temp directory for copying {FileName}; using default temp path",
                        originalFileName);
                    localTempWorkingDir = Path.Combine(Path.GetTempPath(), "XISOStudio_Convert",
                        Guid.NewGuid().ToString());
                }

                Directory.CreateDirectory(localTempWorkingDir);
                var simpleFilename = GenerateFilename.GenerateSimpleFilename(fileIndex);
                var localTempIsoPath = Path.Combine(localTempWorkingDir, simpleFilename);

                progress.Report(new BatchOperationProgress
                {
                    LogMessage = $"File '{originalFileName}': Copying to local temp...",
                    CurrentDrive = PathHelper.GetDriveLetter(Path.GetTempPath())
                });

                if (!await CopyFileWithCloudRetryAsync(inputFile, localTempIsoPath, onCloudRetryRequired, progress,
                        token))
                {
                    return FileProcessingStatus.Failed;
                }

                sourcePath = localTempIsoPath;
            }

            Directory.CreateDirectory(outputFolder);

            // Generate the output filename for the requested format
            var outputExtension = outputFormat switch
            {
                OutputFormat.Zar => ".zar",
                OutputFormat.Cso => ".cso",
                OutputFormat.Chd => ".chd",
                _ => ".iso"
            };
            // Two inputs can share a base name (e.g. Disc1/game.iso and Disc2/game.iso); reserve
            // a unique output path so the second conversion cannot destroy the first result.
            var destinationPath = context.ReserveOutputPath(outputFolder,
                Path.GetFileNameWithoutExtension(originalFileName), outputExtension);
            var outputFileName = Path.GetFileName(destinationPath);

            // Never delete the source file: converting a file onto itself would destroy it
            if (XisoPaths.AreSamePath(sourcePath, destinationPath))
            {
                progress.Report(new BatchOperationProgress
                {
                    LogMessage =
                        $"Error: The output file would overwrite the source file '{originalFileName}'. Please choose a different output folder."
                });
                return FileProcessingStatus.Failed;
            }

            var formatLabel = outputFormat switch
            {
                OutputFormat.Zar => "ZAR",
                OutputFormat.Cso => "CSO",
                OutputFormat.Chd => "CHD",
                _ => "optimized XISO"
            };

            var engineName = outputFormat == OutputFormat.Chd ? "CHDSharp" : "XISOSharp";

            progress.Report(new BatchOperationProgress
            {
                LogMessage = $"File '{originalFileName}': Converting to {formatLabel} with {engineName}...",
                CurrentDrive = PathHelper.GetDriveLetter(outputFolder)
            });

            // Pass the user-visible output name explicitly: the working copy may be a
            // temporary file, but the converted result must keep the original name.
            var status = outputFormat == OutputFormat.Chd
                ? await _chdService.ConvertIsoToChdAsync(sourcePath, outputFolder, outputFileName, skipSystemUpdate,
                    checkIntegrity, progress, token)
                : await _xisoSharpService.ConvertIsoAsync(sourcePath, outputFolder, outputFileName,
                    outputFormat, skipSystemUpdate, checkIntegrity, progress, token);

            // Map engine results: already-optimized and skipped both count as skipped (the
            // image was not rewritten); invalid inputs pass through unchanged so the UI can
            // count genuinely invalid images instead of every failure.
            if (status is FileProcessingStatus.AlreadyOptimized or FileProcessingStatus.Skipped)
                return FileProcessingStatus.Skipped;
            if (status != FileProcessingStatus.Converted) return status;

            if (deleteOriginal)
            {
                try
                {
                    // Even when the conversion ran from a temporary working copy (cloud files),
                    // the user-visible original is the file that "Replace Originals" must remove.
                    File.Delete(inputFile);
                    progress.Report(new BatchOperationProgress
                        { LogMessage = $"Deleted original: {originalFileName}" });
                }
                catch (Exception ex)
                {
                    progress.Report(new BatchOperationProgress
                        { LogMessage = $"Warning: Could not delete original {originalFileName}: {ex.Message}" });

                    // A source file held open by another process (emulator, antivirus, Explorer
                    // preview) is environmental and must not generate an automatic bug report.
                    if (PathHelper.IsFileInUseError(ex) || PathHelper.IsDeviceIoError(ex) ||
                        PathHelper.IsNetworkError(ex) || ex is UnauthorizedAccessException)
                    {
                        _logger.Information(ex, "Could not delete original file {FileName}", originalFileName);
                    }
                    else
                    {
                        _logger.Warning(ex, "Could not delete original file {FileName}", originalFileName);
                    }
                }
            }

            return FileProcessingStatus.Converted;
        }
        finally
        {
            if (localTempWorkingDir != null)
            {
                await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(localTempWorkingDir, 5, 1000, _logger,
                    CancellationToken.None);
            }
        }
    }

    #endregion

    #region Testing Logic

    /// <summary>
    /// Tests every supported image in <paramref name="inputFolder"/> and optionally moves each
    /// image to a success or failed subfolder based on the result.
    /// </summary>
    /// <param name="inputFolder">Folder to scan for testable images.</param>
    /// <param name="moveSuccessful">Whether images that pass the test are moved to the "_success" subfolder.</param>
    /// <param name="moveFailed">Whether images that fail the test are moved to the "_failed" subfolder.</param>
    /// <param name="searchSubfolders">Whether to include images in subfolders of the input folder.</param>
    /// <param name="performDeepScan">Whether to read all image data to detect media or decompression errors.</param>
    /// <param name="progress">Receives progress updates for the batch.</param>
    /// <param name="onCloudRetryRequired">Callback invoked when a cloud file cannot be read and a retry decision is needed.</param>
    /// <param name="token">Token used to cancel the batch.</param>
    /// <returns>A task that completes when the batch test has finished.</returns>
    public Task TestAsync(string inputFolder, bool moveSuccessful, bool moveFailed, bool searchSubfolders,
        bool performDeepScan, IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired, CancellationToken token)
    {
        return RunWithErrorHandlingAsync("TestAsync", async () =>
        {
            if (!Directory.Exists(inputFolder))
            {
                throw new IOException(
                    $"The input folder does not exist or is not accessible: '{inputFolder}'\n\n" +
                    "Possible causes:\n" +
                    "• The folder was deleted, moved, or renamed\n" +
                    "• The folder is on a network drive that is disconnected\n" +
                    "• The folder is a cloud placeholder (OneDrive, Dropbox) that hasn't been synced\n" +
                    "• The path contains characters that are not supported by the file system\n\n" +
                    "Please verify the folder exists and try again.");
            }

            _logger.Information("Starting image test. Input: {InputFolder}", inputFolder);

            var enumOptions = new EnumerationOptions
                { IgnoreInaccessible = true, RecurseSubdirectories = searchSubfolders };

            List<string> imageFiles;
            try
            {
                imageFiles = await Task.Run(() => Directory.GetFiles(inputFolder, "*.*", enumOptions)
                    .Where(SupportedFiles.IsTestable).ToList(), token);
            }
            catch (DirectoryNotFoundException ex)
            {
                _logger.Information(ex, "Input folder not found: {InputFolder}", inputFolder);
                throw new IOException(
                    $"The input folder was not found: '{inputFolder}'\n\n" +
                    "The folder may have been deleted, moved, or is a cloud placeholder that hasn't been synced.\n" +
                    $"Original error: {ex.Message}",
                    ex);
            }

            if (imageFiles.Count == 0)
            {
                _logger.Information("No supported image files found in {InputFolder}", inputFolder);
                return;
            }

            _logger.Information("Found {FileCount} image file(s) to test in {InputFolder}", imageFiles.Count,
                inputFolder);

            await TestEntriesCoreAsync(inputFolder, imageFiles, moveSuccessful, moveFailed, performDeepScan, progress,
                onCloudRetryRequired, token);
        });
    }

    /// <summary>
    /// Tests the given images and optionally moves each image to a success or failed subfolder
    /// of <paramref name="inputFolder"/> based on the result.
    /// </summary>
    /// <param name="inputFolder">Folder that receives the "_success" and "_failed" subfolders when images are moved.</param>
    /// <param name="files">The files to test; files that are not testable are ignored.</param>
    /// <param name="moveSuccessful">Whether images that pass the test are moved to the "_success" subfolder.</param>
    /// <param name="moveFailed">Whether images that fail the test are moved to the "_failed" subfolder.</param>
    /// <param name="performDeepScan">Whether to read all image data to detect media or decompression errors.</param>
    /// <param name="progress">Receives progress updates for the batch.</param>
    /// <param name="onCloudRetryRequired">Callback invoked when a cloud file cannot be read and a retry decision is needed.</param>
    /// <param name="token">Token used to cancel the batch.</param>
    /// <returns>A task that completes when the batch test has finished.</returns>
    public Task TestFilesAsync(string inputFolder, IReadOnlyList<string> files, bool moveSuccessful, bool moveFailed,
        bool performDeepScan, IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired, CancellationToken token)
    {
        return RunWithErrorHandlingAsync("TestFilesAsync", async () =>
        {
            var imageFiles = files.Where(SupportedFiles.IsTestable).ToList();
            if (imageFiles.Count == 0)
            {
                _logger.Information("No supported image files selected for testing.");
                return;
            }

            _logger.Information("Starting image test of {FileCount} selected file(s). Input: {InputFolder}",
                imageFiles.Count, inputFolder);

            await TestEntriesCoreAsync(inputFolder, imageFiles, moveSuccessful, moveFailed, performDeepScan, progress,
                onCloudRetryRequired, token);
        });
    }

    /// <summary>
    /// Tests the given images and moves each image to the success or failed subfolder when
    /// the corresponding option is enabled.
    /// </summary>
    /// <param name="inputFolder">Folder that contains the "_success" and "_failed" subfolders.</param>
    /// <param name="imageFiles">Paths of the images to test.</param>
    /// <param name="moveSuccessful">Whether images that pass the test are moved to the "_success" subfolder.</param>
    /// <param name="moveFailed">Whether images that fail the test are moved to the "_failed" subfolder.</param>
    /// <param name="performDeepScan">Whether to read all image data to detect media or decompression errors.</param>
    /// <param name="progress">Receives progress updates for the batch.</param>
    /// <param name="onCloudRetryRequired">Callback invoked when a cloud file cannot be read and a retry decision is needed.</param>
    /// <param name="token">Token used to cancel the batch.</param>
    private async Task TestEntriesCoreAsync(string inputFolder, IReadOnlyList<string> imageFiles, bool moveSuccessful,
        bool moveFailed, bool performDeepScan, IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired, CancellationToken token)
    {
        progress.Report(new BatchOperationProgress { TotalFiles = imageFiles.Count });
        var successFolder = Path.Combine(inputFolder, "_success");
        var failedFolder = Path.Combine(inputFolder, "_failed");

        var processed = 0;
        var fileIndex = 1;

        foreach (var imagePath in imageFiles)
        {
            token.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(imagePath);
            progress.Report(new BatchOperationProgress
            {
                StatusText = $"Testing: {fileName}", CurrentDrive = PathHelper.GetDriveLetter(imagePath)
            });

            try
            {
                var result = await TestSingleIsoInternalAsync(imagePath, fileIndex++, performDeepScan,
                    onCloudRetryRequired,
                    progress, token);

                if (result == IsoTestResultStatus.Passed)
                {
                    // Move first: a file whose move fails must not be reported as a success
                    // while it is still sitting in the input folder.
                    if (moveSuccessful)
                        await MoveTestedImageAsync(imagePath, successFolder, "successfully tested", token);

                    progress.Report(new BatchOperationProgress
                        { SuccessCount = 1, LogMessage = $"  SUCCESS: '{fileName}' passed test." });
                }
                else
                {
                    // Move first, mirroring the success path: when the move throws, the
                    // catch below reports the file once — reporting here as well would
                    // count the same file twice.
                    if (moveFailed) await MoveTestedImageAsync(imagePath, failedFolder, "failed test", token);

                    progress.Report(new BatchOperationProgress
                    {
                        FailedCount = 1, FailedPathToAdd = imagePath,
                        LogMessage = $"  FAILURE: '{fileName}' failed test."
                    });
                    _logger.Information("Test failed for {FileName}", fileName);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("Image test canceled while processing {FileName}", fileName);
                throw;
            }
            catch (Exception ex) when (PathHelper.IsDiskSpaceError(ex))
            {
                // Stop the batch — no point continuing without disk space
                progress.Report(new BatchOperationProgress
                {
                    LogMessage =
                        "ERROR: Not enough disk space on the drive. Batch operation stopped. Please free up disk space and try again.",
                    FailedCount = 1,
                    FailedPathToAdd = imagePath
                });
                _logger.Information(ex, "Not enough disk space; batch test stopped on {FileName}", fileName);
                throw;
            }
            catch (Exception ex) when (IsFatalEnvironmentalError(ex))
            {
                // Stop the batch — no point continuing if the source device is unavailable
                progress.Report(new BatchOperationProgress
                {
                    LogMessage =
                        $"FATAL ERROR: The source device or path is not available: {ex.Message}. Batch operation stopped.",
                    FailedCount = 1,
                    FailedPathToAdd = imagePath
                });
                _logger.Information(ex, "Source device or path unavailable; batch test stopped on {FileName}",
                    fileName);
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable file must not abort the remaining tests.
                progress.Report(new BatchOperationProgress
                {
                    LogMessage = $"ERROR: {fileName} could not be tested: {ex.Message}",
                    FailedCount = 1,
                    FailedPathToAdd = imagePath
                });

                // A file locked by another process (antivirus, download manager), an
                // inaccessible path, or a network glitch is environmental, not an
                // application defect, and must not be auto-uploaded as a bug report.
                if (IsFatalEnvironmentalError(ex) || PathHelper.IsNetworkError(ex) ||
                    ex is UnauthorizedAccessException or IOException)
                {
                    _logger.Information(ex, "Handled test error on {FileName}", fileName);
                }
                else
                {
                    _logger.Error(ex, "Orchestrator test error on {FileName}", fileName);
                }
            }

            processed++;
            progress.Report(new BatchOperationProgress { ProcessedCount = processed });
        }

        _logger.Information("Image test completed. Processed {ProcessedCount} file(s)", processed);
    }

    /// <summary>
    /// Moves a tested image to the success/failed folder. A split CISO set is opened
    /// through its first part (<c>game.1.cso</c>), so the continuation parts
    /// (<c>game.2.cso</c>, …) travel with it — moving part 1 alone would break the set.
    /// </summary>
    /// <param name="imagePath">Path of the image that was tested.</param>
    /// <param name="destinationFolder">Folder that receives the image and, for a split set, its continuation parts.</param>
    /// <param name="moveReason">Reason logged by the file mover.</param>
    /// <param name="token">Token used to cancel the move.</param>
    private async Task MoveTestedImageAsync(string imagePath, string destinationFolder, string moveReason,
        CancellationToken token)
    {
        await _fileMover.MoveTestedFileAsync(imagePath, destinationFolder, moveReason, token);

        if (!imagePath.EndsWith(".1.cso", StringComparison.OrdinalIgnoreCase)) return;

        // Part 1 was not moved (destination already exists or the move failed): leave the
        // set together instead of stranding the continuation parts.
        if (File.Exists(imagePath)) return;

        // Preserve the original extension casing: on case-sensitive file systems a
        // hard-coded ".cso" would never find parts named "game.2.CSO".
        var basePath = imagePath[..^".1.cso".Length];
        var extension = imagePath[^4..];
        for (var part = 2;; part++)
        {
            var partPath = $"{basePath}.{part.ToString(CultureInfo.InvariantCulture)}{extension}";
            if (!File.Exists(partPath)) break;

            await _fileMover.MoveTestedFileAsync(partPath, destinationFolder, moveReason, token);
        }
    }

    /// <summary>
    ///     Enumerates the existing parts of a split CISO set starting at its first part
    ///     (<c>game.1.cso</c>, <c>game.2.cso</c>, …), preserving the original extension casing.
    /// </summary>
    /// <param name="firstPartPath">Path of the first part of the split CISO set (for example <c>game.1.cso</c>).</param>
    /// <returns>The existing parts of the set, starting with the first part.</returns>
    private static IEnumerable<string> EnumerateSplitCisoParts(string firstPartPath)
    {
        var basePath = firstPartPath[..^".1.cso".Length];
        var extension = firstPartPath[^4..];

        for (var part = 1;; part++)
        {
            var partPath = $"{basePath}.{part.ToString(CultureInfo.InvariantCulture)}{extension}";
            if (!File.Exists(partPath)) yield break;

            yield return partPath;
        }
    }

    /// <summary>
    /// Tests a single image, copying unreadable cloud files (including every part of a split
    /// CISO set) to a temporary folder first.
    /// </summary>
    /// <param name="isoPath">Path of the image to test.</param>
    /// <param name="index">One-based index of the image, used to generate temporary file names.</param>
    /// <param name="performDeepScan">Whether to read all image data to detect media or decompression errors.</param>
    /// <param name="cloudRetry">Callback invoked when a cloud file cannot be read and a retry decision is needed.</param>
    /// <param name="progress">Receives progress updates during the test.</param>
    /// <param name="token">Token used to cancel the test.</param>
    /// <returns>The outcome of the test.</returns>
    private async Task<IsoTestResultStatus> TestSingleIsoInternalAsync(string isoPath, int index, bool performDeepScan,
        Func<string, Task<CloudRetryResult>> cloudRetry, IProgress<BatchOperationProgress> progress,
        CancellationToken token)
    {
        // 1. Handle Cloud/OneDrive files (download to temp if necessary)
        var pathToCheck = isoPath;
        string? tempCloudDir = null;

        try
        {
            try
            {
                // Simple check if file is accessible (triggers cloud download if hydration is automatic, or fails)
                // Run on background thread to avoid blocking UI for cloud/slow files
                await Task.Run(() =>
                {
                    using var stream = File.OpenRead(isoPath);
                }, token);
            }
            catch (IOException)
            {
                // Likely cloud file issue, use existing copy logic
                _logger.Debug("Image {ImagePath} is not directly readable; copying to local temp", isoPath);

                // A split CISO set is opened through its first part, so every part must be
                // copied and the names must keep the ".1.cso"/".2.cso" markers XISOSharp
                // uses to recognize the set.
                var extension = Path.GetExtension(isoPath);
                var simpleStem = Path.GetFileNameWithoutExtension(GenerateFilename.GenerateSimpleFilename(index));
                var isSplitCiso = isoPath.EndsWith(".1.cso", StringComparison.OrdinalIgnoreCase);
                var parts = isSplitCiso ? EnumerateSplitCisoParts(isoPath).ToList() : [isoPath];

                long estimatedSize = 0;
                try
                {
                    estimatedSize = parts.Sum(part => new FileInfo(part).Length);
                }
                catch (Exception ex)
                {
                    // ignored
                    _logger.Debug(ex, "Could not determine size of image {ImagePath}", isoPath);
                }

                var tempDir = ResolveTempDirectory(estimatedSize, "XISOStudio_Test");
                Directory.CreateDirectory(tempDir);
                tempCloudDir = tempDir;

                for (var partIndex = 0; partIndex < parts.Count; partIndex++)
                {
                    var partName = isSplitCiso
                        ? $"{simpleStem}.{(partIndex + 1).ToString(CultureInfo.InvariantCulture)}{extension}"
                        : simpleStem + extension;
                    var tempPartPath = Path.Combine(tempDir, partName);

                    if (!await CopyFileWithCloudRetryAsync(parts[partIndex], tempPartPath, cloudRetry, progress, token))
                    {
                        return IsoTestResultStatus.Failed;
                    }

                    if (partIndex == 0) pathToCheck = tempPartPath;
                }
            }

            try
            {
                progress.Report(
                    new BatchOperationProgress { LogMessage = "  Verifying image structure and readability..." });

                var passed =
                    await _integrityService.TestIsoIntegrityAsync(pathToCheck, performDeepScan, progress, token);

                return passed ? IsoTestResultStatus.Passed : IsoTestResultStatus.Failed;
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("Image test canceled: {ImagePath}", isoPath);
                throw;
            }
            catch (Exception ex)
            {
                progress.Report(new BatchOperationProgress { LogMessage = $"  Test Error: {ex.Message}" });
                _logger.Information(ex, "Test failed for {ImagePath}", isoPath);
                return IsoTestResultStatus.Failed;
            }
        }
        finally
        {
            // The temp copy — including partial files from a failed or canceled copy of a
            // split set — must never survive this method.
            if (tempCloudDir != null)
            {
                try
                {
                    if (Directory.Exists(tempCloudDir)) Directory.Delete(tempCloudDir, true);
                }
                catch (Exception ex)
                {
                    /* ignore cleanup errors */
                    _logger.Debug(ex, "Could not delete test temp copy {TempPath}", tempCloudDir);
                }
            }
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Determines whether an exception indicates that the source or output device is
    /// unavailable, so the batch cannot continue.
    /// </summary>
    /// <param name="ex">Exception to inspect.</param>
    /// <returns><c>true</c> when the error is fatal for the batch; otherwise <c>false</c>.</returns>
    internal static bool IsFatalEnvironmentalError(Exception ex)
    {
        if (PathHelper.IsDeviceIoError(ex)) return true;

        if (ex is IOException ioEx)
        {
            var hResult = ioEx.HResult & 0xFFFF;
            // 0x15: ERROR_NOT_READY, 0x03: ERROR_PATH_NOT_FOUND, 0x0F: ERROR_INVALID_DRIVE,
            // 0x37: ERROR_DEV_NOT_EXIST, 0x40: ERROR_NETNAME_DELETED
            if (hResult is 0x15 or 0x03 or 0x0F or 0x37 or 0x40) return true;
        }

        if (ex is DirectoryNotFoundException) return true;

        return ex is IOException ioEx2 &&
               (ioEx2.Message.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                ioEx2.Message.Contains("network name is no longer available", StringComparison.OrdinalIgnoreCase) ||
                ioEx2.Message.Contains("Zařízení není připraveno",
                    StringComparison.OrdinalIgnoreCase)); // Czech translation from bug reports
    }

    /// <summary>
    /// Reports the outcome of a single conversion to the progress sink.
    /// </summary>
    /// <param name="status">Outcome returned by the conversion service.</param>
    /// <param name="path">Path reported for failed entries.</param>
    /// <param name="progress">Receives the progress update.</param>
    private static void ReportStatus(FileProcessingStatus status, string path,
        IProgress<BatchOperationProgress> progress)
    {
        switch (status)
        {
            case FileProcessingStatus.Converted:
                progress.Report(new BatchOperationProgress { SuccessCount = 1 }); break;
            case FileProcessingStatus.AlreadyOptimized:
            case FileProcessingStatus.Skipped: progress.Report(new BatchOperationProgress { SkippedCount = 1 }); break;
            case FileProcessingStatus.InvalidInput:
                progress.Report(new BatchOperationProgress
                    { FailedCount = 1, InvalidIsoCount = 1, FailedPathToAdd = path }); break;
            case FileProcessingStatus.Failed:
                progress.Report(new BatchOperationProgress { FailedCount = 1, FailedPathToAdd = path }); break;
        }
    }

    /// <summary>
    /// Copies a file, retrying network failures and asking the caller to hydrate cloud
    /// placeholders when needed.
    /// </summary>
    /// <param name="source">Path of the file to copy.</param>
    /// <param name="dest">Destination path of the copy.</param>
    /// <param name="cloudRetry">Callback invoked when a cloud file cannot be read and a retry decision is needed.</param>
    /// <param name="progress">Receives progress updates for the copy.</param>
    /// <param name="token">Token used to cancel the copy.</param>
    /// <returns><c>true</c> when the file was copied; otherwise <c>false</c>.</returns>
    private async Task<bool> CopyFileWithCloudRetryAsync(string source, string dest,
        Func<string, Task<CloudRetryResult>> cloudRetry, IProgress<BatchOperationProgress> progress,
        CancellationToken token)
    {
        const int maxNetworkRetries = 5;
        const int initialRetryDelayMs = 500;
        var networkRetryCount = 0;

        while (true)
        {
            try
            {
                await Task.Run(() => File.Copy(source, dest, true), token);
                return true;
            }
            catch (IOException ex) when (PathHelper.IsNetworkError(ex) && networkRetryCount < maxNetworkRetries)
            {
                networkRetryCount++;
                var delayMs = initialRetryDelayMs * (int)Math.Pow(2, networkRetryCount - 1);
                progress.Report(new BatchOperationProgress
                {
                    LogMessage =
                        $"Network error detected, retrying in {delayMs}ms... (attempt {networkRetryCount}/{maxNetworkRetries})"
                });
                _logger.Information(ex,
                    "Network error copying {Source}; retrying in {DelayMs}ms (attempt {Attempt}/{MaxRetries})", source,
                    delayMs, networkRetryCount, maxNetworkRetries);
                await Task.Delay(delayMs, token);
            }
            catch (IOException ex) when (ex.Message.Contains("cloud operation", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug(ex, "Cloud operation required while copying {Source}", source);
                var result = await cloudRetry(Path.GetFileName(source));
                switch (result)
                {
                    case CloudRetryResult.Retry:
                        continue;
                    case CloudRetryResult.Cancel:
                        _logger.Debug("Cloud retry canceled by the user for {Source}", source);
                        throw new OperationCanceledException();
                    default:
                        return false;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("File copy canceled: {Source}", source);
                throw;
            }
            catch (Exception ex)
            {
                progress.Report(new BatchOperationProgress { LogMessage = $"Copy failed: {ex.Message}" });
                // A source locked by another process (antivirus, download manager) or held
                // without share access is environmental; Warning-or-higher events are
                // auto-uploaded as bug reports, so those must stay at Information.
                var isEnvironmentalError =
                    PathHelper.IsDiskSpaceError(ex) || PathHelper.IsNetworkError(ex) ||
                    IsFatalEnvironmentalError(ex) || ex is UnauthorizedAccessException ||
                    (ex is IOException ioEx && FileExtractorService.IsTransientIoError(ioEx));
                if (isEnvironmentalError)
                {
                    _logger.Information(ex, "Copy failed for {Source} due to an environmental error", source);
                }
                else
                {
                    _logger.Warning(ex, "Copy failed for {Source}", source);
                }

                return false;
            }
        }
    }

    /// <summary>
    /// Deletes the temporary folders registered during the batch, retrying failures.
    /// </summary>
    /// <param name="folders">Temporary folders to delete.</param>
    /// <param name="progress">Receives progress updates for the cleanup.</param>
    private async Task CleanupTempFoldersAsync(List<string> folders, IProgress<BatchOperationProgress> progress)
    {
        if (folders.Count == 0) return;

        progress.Report(new BatchOperationProgress { LogMessage = "Cleaning up temporary folders..." });
        foreach (var folder in folders.ToList())
        {
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(folder, 5, 1000, _logger,
                CancellationToken.None);
        }
    }

    #endregion
}