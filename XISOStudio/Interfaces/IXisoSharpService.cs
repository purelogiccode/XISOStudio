using XISOStudio.Models;

namespace XISOStudio.Interfaces;

/// <summary>
/// Converts Xbox ISO images to the optimized XISO, ZAR, CSO, or CCI format using the
/// XISOSharp library.
/// </summary>
public interface IXisoSharpService
{
    /// <summary>
    /// Converts <paramref name="inputFile"/> and writes the result into <paramref name="outputFolder"/>
    /// using <paramref name="outputFileName"/> in the requested <paramref name="outputFormat"/>.
    /// The caller supplies the name explicitly so the result always matches the user-visible original
    /// file name, even when the input is a temporary working copy.
    /// </summary>
    /// <param name="inputFile">Path of the ISO image to convert.</param>
    /// <param name="outputFolder">Folder that receives the converted file.</param>
    /// <param name="outputFileName">File name to use for the result.</param>
    /// <param name="outputFormat">Target format (XISO, ZAR, CSO, or CCI).</param>
    /// <param name="skipSystemUpdate">Whether to exclude the $SystemUpdate folder from the optimized image.</param>
    /// <param name="checkIntegrity">Whether to validate image structure: the converted XISO output, or the source image when producing ZAR/CSO (those formats are streamed and are not re-read afterwards).</param>
    /// <param name="progress">Receives batch progress and status updates.</param>
    /// <param name="token">Token that cancels the conversion.</param>
    /// <returns>The status of the completed conversion.</returns>
    Task<FileProcessingStatus> ConvertIsoAsync(string inputFile, string outputFolder, string outputFileName,
        OutputFormat outputFormat, bool skipSystemUpdate, bool checkIntegrity,
        IProgress<BatchOperationProgress> progress, CancellationToken token);
}