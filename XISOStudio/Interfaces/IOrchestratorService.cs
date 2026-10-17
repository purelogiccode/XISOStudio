using XISOStudio.Models;

namespace XISOStudio.Interfaces;

/// <summary>
/// Runs batch conversion and testing operations over folders or selected files.
/// </summary>
public interface IOrchestratorService
{
    /// <summary>
    /// Converts the specified files.
    /// </summary>
    /// <param name="files">Full paths of the files to convert; unsupported files are ignored.</param>
    /// <param name="outputFolder">Folder the converted files are written to.</param>
    /// <param name="deleteOriginals">When <c>true</c>, deletes each source file after it was converted successfully.</param>
    /// <param name="skipSystemUpdate">When <c>true</c>, removes the $SystemUpdate folder from the converted image.</param>
    /// <param name="checkIntegrity">When <c>true</c>, validates each converted output image.</param>
    /// <param name="outputFormat">Format the images are converted to.</param>
    /// <param name="progress">Receives progress updates for the batch.</param>
    /// <param name="onCloudRetryRequired">
    /// Callback invoked when a cloud file operation fails, letting the user choose to retry,
    /// skip the file or cancel the batch.
    /// </param>
    /// <param name="token">Token used to cancel the batch.</param>
    Task ConvertFilesAsync(
        IReadOnlyList<string> files,
        string outputFolder,
        bool deleteOriginals,
        bool skipSystemUpdate,
        bool checkIntegrity,
        OutputFormat outputFormat,
        IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired,
        CancellationToken token);

    /// <summary>
    /// Tests the specified images.
    /// </summary>
    /// <param name="inputFolder">Folder used to resolve the "_success" and "_failed" subfolders.</param>
    /// <param name="files">Full paths of the images to test; unsupported files are ignored.</param>
    /// <param name="moveSuccessful">When <c>true</c>, moves images that pass the test into a "_success" subfolder.</param>
    /// <param name="moveFailed">When <c>true</c>, moves images that fail the test into a "_failed" subfolder.</param>
    /// <param name="performDeepScan">
    /// When <c>true</c>, reads all image data to detect media or decompression errors.
    /// </param>
    /// <param name="progress">Receives progress updates for the batch.</param>
    /// <param name="onCloudRetryRequired">
    /// Callback invoked when a cloud file operation fails, letting the user choose to retry,
    /// skip the file or cancel the batch.
    /// </param>
    /// <param name="token">Token used to cancel the batch.</param>
    Task TestFilesAsync(
        string inputFolder,
        IReadOnlyList<string> files,
        bool moveSuccessful,
        bool moveFailed,
        bool performDeepScan,
        IProgress<BatchOperationProgress> progress,
        Func<string, Task<CloudRetryResult>> onCloudRetryRequired,
        CancellationToken token);
}