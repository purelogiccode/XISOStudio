using CHDSharp;
using CHDSharp.Encoder;
using CHDSharp.Encoder.Models;
using CHDSharp.Models;
using XISOStudio.Interfaces;
using XISOStudio.Models;
using Serilog;
using XISOSharp;

namespace XISOStudio.Services;

/// <summary>
/// Converts Xbox ISO images to CHD v5 (Compressed Hunks of Data) using the CHDSharp
/// library. The source is first rewritten to an optimized XISO (game partition only,
/// honoring the $SystemUpdate filter) and then encoded as a DVD CHD with the chdman
/// <c>createdvd</c> preset: 4096-byte hunks, 2048-byte units and the
/// <c>lzma,zlib,huff,flac</c> codec list.
/// </summary>
public class ChdService : IChdService
{
    /// <summary>chdman <c>createdvd</c> defaults: 4096-byte hunks, 2048-byte units.</summary>
    private const uint DvdHunkBytes = 4096;

    /// <summary>chdman <c>createdvd</c> default unit size in bytes.</summary>
    private const uint DvdUnitBytes = 2048;

    /// <summary>chdman <c>createdvd</c> default codec list (best ratio).</summary>
    private const string DvdCodecList = "lzma,zlib,huff,flac";

    /// <summary>Subfolder under the system temp directory used for temporary CHD working files.</summary>
    private const string TempSubfolder = "XISOStudio_Chd";

    /// <summary>Logger used to report conversion progress and failures.</summary>
    private readonly ILogger _logger;

    /// <summary>XISO service used to prepare optimized source images.</summary>
    private readonly IXisoSharpService _xisoSharpService;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChdService"/> class.
    /// </summary>
    /// <param name="logger">Logger used to report conversion progress and failures.</param>
    /// <param name="xisoSharpService">XISO service used to prepare optimized source images.</param>
    public ChdService(ILogger logger, IXisoSharpService xisoSharpService)
    {
        _logger = logger.ForContext<ChdService>();
        _xisoSharpService = xisoSharpService;
    }

    /// <summary>
    /// Converts an Xbox ISO image to a DVD CHD v5 file, rewriting non-optimized inputs through
    /// the XISO pipeline first and optionally verifying the encoded output.
    /// </summary>
    /// <param name="inputFile">Path of the ISO image to convert.</param>
    /// <param name="outputFolder">Folder that receives the generated CHD file.</param>
    /// <param name="outputFileName">File name for the generated CHD file.</param>
    /// <param name="skipSystemUpdate">Whether to strip the $SystemUpdate folder while preparing the source.</param>
    /// <param name="checkIntegrity">Whether to perform a full deep verification of the encoded CHD.</param>
    /// <param name="progress">Progress sink that receives status updates.</param>
    /// <param name="token">Cancellation token for the conversion.</param>
    /// <returns>The conversion status, or a failure status when the input is invalid or conversion fails.</returns>
    public async Task<FileProcessingStatus> ConvertIsoToChdAsync(string inputFile, string outputFolder,
        string outputFileName, bool skipSystemUpdate, bool checkIntegrity,
        IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        var fileName = Path.GetFileName(inputFile);
        _logger.Information("Converting '{FileName}' to CHD using CHDSharp...", fileName);

        if (!File.Exists(inputFile))
        {
            _logger.Information("Input file not found: {InputFile}", inputFile);
            return FileProcessingStatus.Failed;
        }

        outputFileName = Path.GetFileName(outputFileName);

        string outputPath;
        try
        {
            outputPath = Path.Combine(outputFolder, outputFileName);

            // CHDSharp writes the output directly; converting a file onto itself would destroy the source.
            if (XisoPaths.AreSamePath(inputFile, outputPath))
            {
                _logger.Information("The output file would overwrite the source file for '{FileName}'. " +
                                    "Please choose a different output folder.", fileName);
                return FileProcessingStatus.Failed;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to prepare the conversion of '{FileName}'", fileName);
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

        // Remove any pre-existing output so the conversion starts from a clean file. A file
        // locked by another process (an emulator playing the previous result, antivirus,
        // Explorer preview) is environmental and must not generate an automatic bug report.
        if (File.Exists(outputPath) &&
            !await PathHelper.TryDeleteExistingFileWithRetryAsync(outputPath, _logger, token))
        {
            return FileProcessingStatus.Failed;
        }

        string? tempDir = null;
        try
        {
            var sourceForChd = inputFile;

            // CHD holds the optimized game partition, so non-optimized (Redump) inputs are
            // first rewritten through the existing XISO pipeline into a temporary file.
            // Already-optimized inputs are rewritten too when $SystemUpdate must be stripped
            // (CHD encoding has no filter of its own).
            if (!XisoReader.IsOptimizedImage(inputFile) || skipSystemUpdate)
            {
                tempDir = Path.Combine(Path.GetTempPath(), TempSubfolder, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                progress.Report(new BatchOperationProgress { StatusText = "Preparing XISO for CHD compression..." });
                _logger.Information("Preparing an optimized XISO for CHD compression...");

                var rewriteStatus = await _xisoSharpService.ConvertIsoAsync(inputFile, tempDir, "source.iso",
                    OutputFormat.Xiso, skipSystemUpdate, checkIntegrity: false, progress, token);
                if (rewriteStatus != FileProcessingStatus.Converted)
                {
                    _logger.Information("Could not prepare '{FileName}' for CHD compression.", fileName);
                    // Keep invalid inputs distinguishable for the caller's reporting.
                    return rewriteStatus == FileProcessingStatus.InvalidInput
                        ? FileProcessingStatus.InvalidInput
                        : FileProcessingStatus.Failed;
                }

                sourceForChd = Path.Combine(tempDir, "source.iso");
            }
            else
            {
                _logger.Information("'{FileName}' is already an optimized XISO; packing it directly.", fileName);
            }

            return await Task.Run(
                () => EncodeAndVerify(sourceForChd, outputPath, checkIntegrity, progress, token), token);
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Conversion of '{FileName}' to CHD was canceled. Cleaning up partial output...",
                fileName);
            DeletePartialOutput(outputPath);
            throw;
        }
        catch (Exception ex) when (PathHelper.IsDiskSpaceError(ex))
        {
            DeletePartialOutput(outputPath);
            _logger.Information(ex, "Not enough disk space to convert '{FileName}' to CHD", fileName);
            throw;
        }
        catch (Exception ex) when (PathHelper.IsDeviceIoError(ex))
        {
            DeletePartialOutput(outputPath);
            _logger.Information(ex,
                "The drive reported a hardware I/O error while converting '{FileName}' to CHD.\n\n" +
                "This usually means the source or output drive is failing, was disconnected, or has a hardware problem.\n" +
                "Please check the drive connection and health (e.g. run chkdsk), then try again.", fileName);
            throw;
        }
        catch (Exception ex) when (PathHelper.IsNetworkError(ex))
        {
            DeletePartialOutput(outputPath);
            _logger.Information(ex, "Network error while converting '{FileName}' to CHD.\n\n" +
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
            _logger.Warning(ex, "Access denied while converting '{FileName}' to CHD.\n\n" +
                                "The output folder may be write-protected or require administrator rights.\n" +
                                "Please choose a different output folder or run the application as administrator.",
                fileName);
            return FileProcessingStatus.Failed;
        }
        catch (Exception ex) when (IsInvalidImageError(ex))
        {
            DeletePartialOutput(outputPath);
            _logger.Information(ex,
                "Failed to convert '{FileName}' to CHD. The file may not be a valid Xbox/Xbox 360 ISO image " +
                "or may be corrupt.", fileName);
            return FileProcessingStatus.InvalidInput;
        }
        catch (Exception ex)
        {
            DeletePartialOutput(outputPath);
            _logger.Error(ex, "Failed to convert '{FileName}' to CHD", fileName);
            return FileProcessingStatus.Failed;
        }
        finally
        {
            if (tempDir != null)
            {
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Could not delete temporary CHD working folder '{TempDir}'", tempDir);
                }
            }
        }
    }

    /// <summary>
    /// Encodes the prepared XISO into a DVD CHD and verifies the result: a header check
    /// always, and a full deep verification (all hunks and hashes) when requested.
    /// </summary>
    /// <param name="sourcePath">Path of the prepared XISO to encode.</param>
    /// <param name="outputPath">Full path of the CHD file to produce.</param>
    /// <param name="checkIntegrity">Whether to perform a full deep verification of the encoded CHD.</param>
    /// <param name="progress">Receives progress updates during encoding and verification.</param>
    /// <param name="token">Token used to cancel the operation.</param>
    /// <returns>The outcome of the conversion.</returns>
    private FileProcessingStatus EncodeAndVerify(string sourcePath, string outputPath, bool checkIntegrity,
        IProgress<BatchOperationProgress> progress, CancellationToken token)
    {
        var fileName = Path.GetFileName(sourcePath);

        _logger.Information("Encoding '{FileName}' to CHD v5 (createdvd preset)...", fileName);
        progress.Report(new BatchOperationProgress { StatusText = "Compressing to CHD..." });

        var codecTags = ChdCodecs.ParseCodecTags(DvdCodecList);
        var options = new ChdEncodeOptions
        {
            // The image is an Xbox DVD filesystem; the tag makes CHDSharp report it as
            // a DVD image (unit size 2048) for the test and explorer paths.
            Metadata = [MetadataWriter.BuildDvdMetadata()],
            HunkCompleted = CreateEncodeProgressCallback(progress)
        };

        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024))
        {
            ChdEncoder.EncodeRaw(source, outputPath, DvdHunkBytes, DvdUnitBytes, codecTags, options, token);
        }

        if (!File.Exists(outputPath))
        {
            _logger.Information("CHDSharp did not produce an output file for '{FileName}'.", fileName);
            return FileProcessingStatus.Failed;
        }

        _logger.Information("CHD encoding completed ({OutputSize}).",
            Formatter.FormatBytes(new FileInfo(outputPath).Length));

        progress.Report(new BatchOperationProgress
            { StatusText = checkIntegrity ? "Verifying CHD output..." : "Checking CHD output..." });
        _logger.Information(checkIntegrity
            ? "Verifying CHD output (every hunk and checksum)..."
            : "Checking CHD output...");

        using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        var result = Chd.CheckFile(stream, Path.GetFileName(outputPath), checkIntegrity,
            checkIntegrity ? CreateVerifyProgressAdapter(progress) : null, token);

        if (!result.IsSuccess)
        {
            _logger.Information("CHD verification failed for '{FileName}': {Error}", fileName,
                result.Error.GetMessage());
            DeletePartialOutput(outputPath);
            return FileProcessingStatus.Failed;
        }

        _logger.Information("Successfully converted '{FileName}' to CHD (V{Version}, SHA1: {Sha1}).",
            fileName, result.Version, result.Sha1Hex);
        return FileProcessingStatus.Converted;
    }

    /// <summary>
    /// Reports encoding progress at 5% steps and writes each step to the log as well, so the
    /// on-screen log shows activity during the (CPU-heavy, minutes-long) CHD encoding.
    /// CHDSharp invokes the callback once per compressed hunk (in hunk order).
    /// </summary>
    /// <param name="progress">Receives the converted progress updates.</param>
    /// <returns>A callback that reports encoding progress to the batch and the log.</returns>
    private Action<HunkProgress> CreateEncodeProgressCallback(IProgress<BatchOperationProgress> progress)
    {
        var lastPercent = -1;
        return hunk =>
        {
            if (hunk.HunkCount == 0) return;

            var percent = (int)((hunk.HunkIndex + 1) * 100 / hunk.HunkCount);
            if (percent < lastPercent + 5 && percent < 100) return;

            lastPercent = percent;
            _logger.Information("Compressing to CHD: {Percent}%", percent);
            progress.Report(new BatchOperationProgress { StatusText = $"Compressing to CHD: {percent}%" });
        };
    }

    /// <summary>Reports deep-verification progress at 5% steps and writes each step to the log.</summary>
    /// <param name="progress">Receives the converted progress updates.</param>
    /// <returns>An adapter that reports deep-verification progress to the batch and the log.</returns>
    private IProgress<ChdProgress> CreateVerifyProgressAdapter(IProgress<BatchOperationProgress> progress)
    {
        var lastPercent = -1;
        return new SynchronousProgress<ChdProgress>(chdProgress =>
        {
            var percent = (int)chdProgress.Percent;
            if (percent < lastPercent + 5 && percent < 100) return;

            lastPercent = percent;
            _logger.Information("Verifying CHD: {Percent}%", percent);
            progress.Report(new BatchOperationProgress { StatusText = $"Verifying CHD: {percent}%" });
        });
    }

    /// <summary>
    /// Errors that indicate the input image itself is unsupported or corrupt rather
    /// than an application defect. XISOSharp's low-level reader reports truncated
    /// images as a plain IOException with a "Read error" message.
    /// </summary>
    /// <param name="ex">Exception to inspect.</param>
    /// <returns><c>true</c> when the error indicates an invalid input image; otherwise <c>false</c>.</returns>
    private static bool IsInvalidImageError(Exception ex)
    {
        return ex is XisoFormatException or XisoEmptyException or XisoFileTooLargeException or InvalidDataException
                   or ExtractErrorException or EndOfStreamException ||
               (ex is IOException ioException &&
                ioException.Message.StartsWith("Read error", StringComparison.OrdinalIgnoreCase));
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