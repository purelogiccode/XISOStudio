using System.Security.Cryptography;
using XISOSharp.Models;
using XISOStudio.Models;

namespace XISOStudio.Interfaces;

/// <summary>
/// Computes whole-image checksums (CRC-32, MD5, SHA-1, SHA-256, SHA3-256), the
/// xdvdfs-compatible file-tree hash and per-file hash sidecars for supported images.
/// </summary>
public interface IImageHashService
{
    /// <summary>
    /// Computes the checksums of <paramref name="imagePath"/>. ISO, split ISO, CISO, CCI and
    /// GOD inputs (header or <c>.data</c> folder) are hashed through their decompressed view;
    /// ZAR hashes cover the stored archive bytes plus the extracted tree hash; CHD hashes cover
    /// the decompressed image.
    /// </summary>
    /// <param name="imagePath">Path of the image or archive.</param>
    /// <param name="token">Token that cancels the operation.</param>
    /// <returns>The available checksums and a note describing their scope.</returns>
    Task<ImageHashes> ComputeAsync(string imagePath, CancellationToken token = default);

    /// <summary>
    /// Computes a per-file hash sidecar (the data written to a <c>.hash</c> file).
    /// </summary>
    /// <param name="imagePath">Path of the image or archive.</param>
    /// <param name="algorithm">Hash algorithm to use; defaults to SHA-256.</param>
    /// <param name="token">Token that cancels the operation.</param>
    /// <returns>One entry per file in the image.</returns>
    IReadOnlyList<FileHashEntry> ComputeFileHashes(string imagePath,
        HashAlgorithmName? algorithm = null, CancellationToken token = default);
}
