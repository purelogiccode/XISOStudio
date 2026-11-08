using XISOStudio.Models;

namespace XISOStudio.Interfaces;

/// <summary>
/// Turns a compressed or container image back into a plain ISO: CISO/CSO, CCI, ZAR,
/// Xbox DVD CHD and GOD/SVOD inputs are decompressed to a sector image.
/// </summary>
public interface IImageConversionService
{
    /// <summary>
    /// Decompresses <paramref name="inputFile"/> into <paramref name="outputFolder"/> using
    /// <paramref name="outputFileName"/> and optionally audits the result.
    /// </summary>
    /// <param name="inputFile">Path of the compressed or container image.</param>
    /// <param name="outputFolder">Folder that receives the ISO.</param>
    /// <param name="outputFileName">File name to use for the ISO.</param>
    /// <param name="checkIntegrity">Whether to audit the decompressed ISO structure.</param>
    /// <param name="progress">Receives batch progress and status updates.</param>
    /// <param name="token">Token that cancels the conversion.</param>
    /// <returns>
    /// <see cref="FileProcessingStatus.Converted"/> on success,
    /// <see cref="FileProcessingStatus.Skipped"/> when the input is already a plain ISO,
    /// otherwise a failure status.
    /// </returns>
    Task<FileProcessingStatus> DecompressToIsoAsync(string inputFile, string outputFolder,
        string outputFileName, bool checkIntegrity, IProgress<BatchOperationProgress> progress,
        CancellationToken token);
}
