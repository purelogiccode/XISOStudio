using System.Buffers;
using CHDSharp;
using CHDSharp.Models;
using Serilog;
using XISOSharp;
using XISOSharp.Models;
using XISOStudio.Interfaces;
using XISOStudio.Models;

namespace XISOStudio.Services;

/// <summary>
/// Decompresses CISO/CSO, CCI, ZAR, Xbox DVD CHD and GOD/SVOD images back into plain ISO
/// sector images with the XISOSharp and CHDSharp libraries.
/// </summary>
public sealed class ImageConversionService : IImageConversionService
{
    /// <summary>Buffer size used when copying a decompressed stream to the output ISO.</summary>
    private const int CopyBufferSize = 1 << 20;

    /// <summary>Decompressed hunks kept by the CHD reader while copying.</summary>
    private const int HunkCacheSize = 32;

    /// <summary>Hunks pre-decompressed by the CHD reader while copying.</summary>
    private const int ReadAheadHunks = 8;

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageConversionService"/> class.
    /// </summary>
    /// <param name="logger">Logger used for diagnostics.</param>
    public ImageConversionService(ILogger logger)
    {
        _logger = logger.ForContext<ImageConversionService>();
    }

    /// <inheritdoc/>
    public async Task<FileProcessingStatus> DecompressToIsoAsync(string inputFile, string outputFolder,
        string outputFileName, bool checkIntegrity, IProgress<BatchOperationProgress> progress,
        CancellationToken token)
    {
        var fileName = Path.GetFileName(inputFile);
        _logger.Information("Decompressing '{FileName}' to ISO...", fileName);

        if (!File.Exists(inputFile))
        {
            _logger.Information("Input file not found: {InputFile}", inputFile);
            return FileProcessingStatus.Failed;
        }

        // Plain images are already the target format.
        if (SupportedFiles.IsIso(inputFile))
        {
            _logger.Information("'{FileName}' is already a plain ISO image; skipping decompression.", fileName);
            return FileProcessingStatus.Skipped;
        }

        outputFileName = Path.GetFileName(outputFileName);
        string outputPath;
        try
        {
            outputPath = Path.Combine(outputFolder, outputFileName);
            if (XisoPaths.AreSamePath(inputFile, outputPath))
            {
                _logger.Information("The output file would overwrite the source file for '{FileName}'. " +
                                    "Please choose a different output folder.", fileName);
                return FileProcessingStatus.Failed;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                       or UnauthorizedAccessException)
        {
            _logger.Information(ex, "Could not prepare the decompression of '{FileName}': {Reason}", fileName,
                ex.Message);
            return FileProcessingStatus.Failed;
        }

        try
        {
            Directory.CreateDirectory(outputFolder);
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "Could not create the output folder '{OutputFolder}'", outputFolder);
            return FileProcessingStatus.Failed;
        }

        if (File.Exists(outputPath) &&
            !await PathHelper.TryDeleteExistingFileWithRetryAsync(outputPath, _logger, token))
        {
            return FileProcessingStatus.Failed;
        }

        return await Task.Run(
            () => DecompressCore(inputFile, outputPath, checkIntegrity, progress, token), token);
    }

    /// <summary>
    /// Dispatches on the detected container and decompresses it to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="inputFile">Path of the compressed or container image.</param>
    /// <param name="outputPath">Full path of the ISO to produce.</param>
    /// <param name="checkIntegrity">Whether to audit the produced ISO.</param>
    /// <param name="progress">Receives progress updates.</param>
    /// <param name="token">Token that cancels the operation.</param>
    /// <returns>The outcome of the decompression.</returns>
    private FileProcessingStatus DecompressCore(string inputFile, string outputPath, bool checkIntegrity,
        IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        var fileName = Path.GetFileName(inputFile);

        try
        {
            var extension = Path.GetExtension(inputFile);
            if (extension.Equals(".chd", StringComparison.OrdinalIgnoreCase))
            {
                progress.Report(new BatchOperationProgress { StatusText = "Decompressing CHD to ISO..." });
                CopyChdToIso(inputFile, outputPath, progress, token);
            }
            else if (extension.Equals(".zar", StringComparison.OrdinalIgnoreCase))
            {
                progress.Report(new BatchOperationProgress { StatusText = "Unpacking ZAR to ISO..." });
                var zarResult = XisoZarchive.ConvertZarToIso(inputFile, outputPath, overwrite: true,
                    CreateProgressAdapter(progress), token);
                if (zarResult != 0)
                {
                    _logger.Information("XISOSharp could not unpack '{FileName}' to ISO (result code {ResultCode}).",
                        fileName, zarResult);
                    DeletePartialOutput(outputPath);
                    return FileProcessingStatus.Failed;
                }
            }
            else
            {
                var source = XisoImageSource.TryDetect(inputFile, out var info) ? info : null;
                if (source is null)
                {
                    throw new InvalidDataException($"Not a supported Xbox image: {inputFile}");
                }

                switch (source.Format)
                {
                    case XisoImageFormat.Ciso:
                    case XisoImageFormat.SplitCiso:
                        progress.Report(new BatchOperationProgress { StatusText = "Decompressing CSO to ISO..." });
                        var csoResult = CisoReader.DecompressToIso(inputFile, outputPath,
                            CreateProgressAdapter(progress), token);
                        if (csoResult != 0)
                        {
                            _logger.Information(
                                "XISOSharp could not decompress '{FileName}' to ISO (result code {ResultCode}).",
                                fileName, csoResult);
                            DeletePartialOutput(outputPath);
                            return FileProcessingStatus.Failed;
                        }

                        break;

                    case XisoImageFormat.Cci:
                    case XisoImageFormat.SplitCci:
                        progress.Report(new BatchOperationProgress { StatusText = "Decompressing CCI to ISO..." });
                        var cciResult = XisoCci.DecompressToIso(inputFile, outputPath, overwrite: true,
                            CreateProgressAdapter(progress), token);
                        if (cciResult != 0)
                        {
                            _logger.Information(
                                "XISOSharp could not decompress '{FileName}' to ISO (result code {ResultCode}).",
                                fileName, cciResult);
                            DeletePartialOutput(outputPath);
                            return FileProcessingStatus.Failed;
                        }

                        break;

                    default:
                        // Plain/split ISO and GOD/SVOD: copy the decompressed logical image.
                        progress.Report(new BatchOperationProgress { StatusText = "Copying image to ISO..." });
                        CopyStreamToIso(inputFile, outputPath, progress, token);
                        break;
                }
            }

            if (!File.Exists(outputPath))
            {
                _logger.Information("No ISO output was produced for '{FileName}'.", fileName);
                return FileProcessingStatus.Failed;
            }

            if (checkIntegrity)
            {
                _logger.Information("Verifying output ISO integrity...");
                var audit = XisoReader.AuditXiso(outputPath, requireOptimizedTag: false);
                if (!audit.IsValid)
                {
                    _logger.Information("Output ISO failed structural validation: {Issues}",
                        string.Join("; ", audit.Issues));
                    DeletePartialOutput(outputPath);
                    return FileProcessingStatus.Failed;
                }

                _logger.Information("Output ISO passed validation ({FilesChecked} files, {DirsChecked} directories).",
                    audit.FilesChecked, audit.DirsChecked);
            }

            _logger.Information("Successfully converted '{FileName}' to ISO format.", fileName);
            return FileProcessingStatus.Converted;
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Decompression of '{FileName}' was canceled. Cleaning up partial output...", fileName);
            DeletePartialOutput(outputPath);
            throw;
        }
        catch (Exception ex) when (PathHelper.IsDiskSpaceError(ex))
        {
            DeletePartialOutput(outputPath);
            _logger.Information(ex, "Not enough disk space to decompress '{FileName}'", fileName);
            throw;
        }
        catch (Exception ex) when (PathHelper.IsDeviceIoError(ex))
        {
            DeletePartialOutput(outputPath);
            _logger.Information(ex,
                "The drive reported a hardware I/O error while decompressing '{FileName}'.\n\n" +
                "This usually means the source or output drive is failing, was disconnected, or has a hardware problem.\n" +
                "Please check the drive connection and health (e.g. run chkdsk), then try again.", fileName);
            throw;
        }
        catch (Exception ex) when (PathHelper.IsNetworkError(ex))
        {
            DeletePartialOutput(outputPath);
            _logger.Information(ex, "Network error while decompressing '{FileName}'.\n\n" +
                                    "Please try:\n" +
                                    "1. Check that the network drive is still connected and accessible\n" +
                                    "2. Copy the file to a local drive before processing\n" +
                                    "3. Check your network connection stability", fileName);
            throw;
        }
        catch (DirectoryNotFoundException ex)
        {
            DeletePartialOutput(outputPath);
            _logger.Information(ex, "Drive or path not found for '{FileName}'.\n\n" +
                                    "Please check that the drive is connected and the path exists.", fileName);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            DeletePartialOutput(outputPath);
            _logger.Information(ex, "Access denied while decompressing '{FileName}'.\n\n" +
                                    "The output folder may be write-protected or require administrator rights.\n" +
                                    "Please choose a different output folder or run the application as administrator.",
                fileName);
            return FileProcessingStatus.Failed;
        }
        catch (Exception ex) when (ImageErrorClassifier.IsInvalidImageError(ex))
        {
            DeletePartialOutput(outputPath);
            _logger.Information(ex,
                "Failed to decompress '{FileName}'. The file may not be a valid Xbox/Xbox 360 image or may be corrupt.",
                fileName);
            return FileProcessingStatus.InvalidInput;
        }
        catch (Exception ex)
        {
            DeletePartialOutput(outputPath);
            _logger.Error(ex, "Failed to decompress '{FileName}'", fileName);
            return FileProcessingStatus.Failed;
        }
    }

    /// <summary>
    /// Copies a decompressed logical image stream to the destination ISO, reporting 1% steps.
    /// </summary>
    /// <param name="inputFile">Path of the source image or container.</param>
    /// <param name="outputPath">Full path of the ISO to produce.</param>
    /// <param name="progress">Receives progress updates.</param>
    /// <param name="token">Token that cancels the copy.</param>
    private static void CopyStreamToIso(string inputFile, string outputPath,
        IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        using var source = XisoImageSource.OpenRead(inputFile);
        using var destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            CopyBufferSize, FileOptions.SequentialScan);

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            var total = source.Length;
            long copied = 0;
            var lastPercent = -1;

            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
                copied += read;

                if (total <= 0) continue;

                var percent = (int)Math.Clamp(copied * 100 / total, 0, 100);
                if (percent <= lastPercent) continue;

                lastPercent = percent;
                progress.Report(new BatchOperationProgress { StatusText = $"Decompressing: {percent}%" });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Copies a CHD's decompressed image to the destination ISO with read-ahead enabled.
    /// </summary>
    /// <param name="inputFile">Path of the CHD image.</param>
    /// <param name="outputPath">Full path of the ISO to produce.</param>
    /// <param name="progress">Receives progress updates.</param>
    /// <param name="token">Token that cancels the copy.</param>
    private static void CopyChdToIso(string inputFile, string outputPath,
        IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        // Only Xbox DVD images are supported, matching the explorer and integrity test.
        var classifyError = Chd.Classify(inputFile, out var classification);
        if (classifyError != ChdError.Chderrnone ||
            !string.Equals(classification, "dvd", StringComparison.Ordinal))
        {
            var reason = classifyError != ChdError.Chderrnone
                ? classifyError.GetMessage()
                : classification ?? "unknown media type";
            throw new InvalidDataException($"CHD image is not an Xbox DVD image ({reason}): {inputFile}");
        }

        var openError = ChdFile.Open(inputFile, out var chd, token);
        if (openError != ChdError.Chderrnone || chd is null)
        {
            throw new InvalidDataException($"Not a valid CHD image ({openError.GetMessage()}): {inputFile}");
        }

        chd.ConfigureCache(HunkCacheSize);
        chd.ConfigureReadAhead(ReadAheadHunks);

        var streamError = ChdFile.OpenAsStream(chd, ownsChd: true, out var stream);
        if (streamError != ChdError.Chderrnone)
        {
            chd.Dispose();
            throw new InvalidDataException($"Not a valid CHD image ({streamError.GetMessage()}): {inputFile}");
        }

        using (stream)
        using (var destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                   CopyBufferSize, FileOptions.SequentialScan))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                var total = stream.Length;
                long copied = 0;
                var lastPercent = -1;

                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, read);
                    copied += read;

                    if (total <= 0) continue;

                    var percent = (int)Math.Clamp(copied * 100 / total, 0, 100);
                    if (percent <= lastPercent) continue;

                    lastPercent = percent;
                    progress.Report(new BatchOperationProgress { StatusText = $"Decompressing CHD: {percent}%" });
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Adapts XISOSharp decompression progress to batch status text, reporting at 5% steps.
    /// </summary>
    /// <param name="progress">Receives the converted progress updates.</param>
    /// <returns>An adapter that reports decompression progress to the batch.</returns>
    private static IProgress<ProgressInfo> CreateProgressAdapter(IProgress<BatchOperationProgress> progress)
    {
        var lastPercent = -1;

        return new SynchronousProgress<ProgressInfo>(info =>
        {
            switch (info.Type)
            {
                case ProgressInfoType.FileProgress:
                    if (info.Count <= 0) break;
                    var percent = (int)Math.Clamp(info.Size * 100 / info.Count, 0, 100);
                    if (percent < lastPercent + 5 && percent < 100) break;
                    lastPercent = percent;
                    progress.Report(new BatchOperationProgress { StatusText = $"Decompressing: {percent}%" });
                    break;
                case ProgressInfoType.FileCount:
                    progress.Report(new BatchOperationProgress
                        { StatusText = $"Decompressing {info.Count:N0} file(s)..." });
                    break;
                case ProgressInfoType.FinishedPacking:
                    progress.Report(new BatchOperationProgress { StatusText = "Finalizing output..." });
                    break;
            }
        });
    }

    /// <summary>
    /// Deletes a partially written output file, ignoring failures because the error has
    /// already been reported to the user.
    /// </summary>
    /// <param name="path">Path of the partial output file; ignored when null or empty.</param>
    private void DeletePartialOutput(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            // Cleanup failure is non-fatal; the error is already reported to the user
            _logger.Debug(ex, "Could not delete partial output file '{Path}'", path);
        }
    }
}
