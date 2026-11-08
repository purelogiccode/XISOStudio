using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using CHDSharp;
using CHDSharp.Models;
using Serilog;
using XISOSharp;
using XISOSharp.BlockDevice;
using XISOSharp.Models;
using XISOStudio.Interfaces;
using XISOStudio.Models;
using ZArchiveSharp;

namespace XISOStudio.Services;

/// <summary>
/// Computes image checksums with the XISOSharp and CHDSharp libraries. Compression-aware
/// inputs hash through their decompressed view, so an ISO, CISO, CCI and GOD image holding
/// the same data produce identical values.
/// </summary>
public sealed class ImageHashService : IImageHashService
{
    private const int CopyBufferSize = 1 << 20;

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageHashService"/> class.
    /// </summary>
    /// <param name="logger">Logger used for diagnostics.</param>
    public ImageHashService(ILogger logger)
    {
        _logger = logger.ForContext<ImageHashService>();
    }

    /// <inheritdoc/>
    public Task<ImageHashes> ComputeAsync(string imagePath, CancellationToken token = default)
    {
        return Task.Run(() => Compute(imagePath, token), token);
    }

    /// <inheritdoc/>
    public IReadOnlyList<FileHashEntry> ComputeFileHashes(string imagePath,
        HashAlgorithmName? algorithm = null, CancellationToken token = default)
    {
        return XisoReader.ComputeFileHashSidecar(imagePath, algorithm ?? HashAlgorithmName.SHA256, token);
    }

    /// <summary>
    /// Dispatches on the container format and computes the available hashes.
    /// </summary>
    /// <param name="imagePath">Path of the image or archive.</param>
    /// <param name="token">Token that cancels the operation.</param>
    /// <returns>The available checksums.</returns>
    private ImageHashes Compute(string imagePath, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        if (!File.Exists(imagePath) && !Directory.Exists(imagePath))
        {
            throw new FileNotFoundException("Image not found.", imagePath);
        }

        token.ThrowIfCancellationRequested();

        // Single-file STFS packages (XBLA/XBLIG) are extensionless and are hashed through
        // StfsSharp rather than the XISO image source.
        if (StfsSharp.StfsPackage.IsStfsPackage(imagePath))
        {
            return ComputeStfs(imagePath, token);
        }

        var extension = Path.GetExtension(imagePath);
        if (extension.Equals(".chd", StringComparison.OrdinalIgnoreCase))
        {
            return ComputeChd(imagePath, token);
        }

        if (extension.Equals(".zar", StringComparison.OrdinalIgnoreCase))
        {
            return ComputeZar(imagePath, token);
        }

        return ComputeXiso(imagePath, token);
    }

    /// <summary>
    /// Hashes an ISO/CISO/CCI/GOD/split image through the decompressed view and adds the
    /// xdvdfs tree hash.
    /// </summary>
    /// <param name="imagePath">Path of the image.</param>
    /// <param name="token">Token that cancels the operation.</param>
    /// <returns>The whole-image and tree hashes.</returns>
    private ImageHashes ComputeXiso(string imagePath, CancellationToken token)
    {
        var hashes = XisoImageHash.Compute(imagePath, token);

        string? treeHash = null;
        try
        {
            treeHash = XisoChecksum.ComputeImageChecksumHex(imagePath, ct: token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            _logger.Information(ex, "Could not compute the file-tree hash of '{ImagePath}': {Reason}",
                imagePath, ex.Message);
        }

        return new ImageHashes(hashes.TotalBytes, hashes.Crc32, hashes.Md5, hashes.Sha1, hashes.Sha256,
            hashes.Sha3256, treeHash, null);
    }

    /// <summary>
    /// Hashes a CHD's decompressed image and its file tree.
    /// </summary>
    /// <param name="imagePath">Path of the CHD image.</param>
    /// <param name="token">Token that cancels the operation.</param>
    /// <returns>The whole-image and tree hashes.</returns>
    private ImageHashes ComputeChd(string imagePath, CancellationToken token)
    {
        var openError = ChdFile.Open(imagePath, out var chd, token);
        if (openError != ChdError.Chderrnone || chd is null)
        {
            throw new InvalidDataException($"Not a valid CHD image ({openError.GetMessage()}): {imagePath}");
        }

        chd.ConfigureCache(32);
        chd.ConfigureReadAhead(8);

        var streamError = ChdFile.OpenAsStream(chd, ownsChd: true, out var stream);
        if (streamError != ChdError.Chderrnone)
        {
            chd.Dispose();
            throw new InvalidDataException($"Not a valid CHD image ({streamError.GetMessage()}): {imagePath}");
        }

        using (stream)
        {
            var hashes = XisoImageHash.Compute(stream, token);

            string? treeHash = null;
            try
            {
                using var device = new StreamBlockDevice(stream, leaveOpen: true);
                var digest = XisoChecksum.ComputeImageChecksum(device, Path.GetFileName(imagePath), ct: token);
                treeHash = Convert.ToHexStringLower(digest);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                _logger.Information(ex, "Could not compute the file-tree hash of '{ImagePath}': {Reason}",
                    imagePath, ex.Message);
            }

            return new ImageHashes(hashes.TotalBytes, hashes.Crc32, hashes.Md5, hashes.Sha1, hashes.Sha256,
                hashes.Sha3256, treeHash, "CHD hashes cover the decompressed image.");
        }
    }

    /// <summary>
    /// Hashes a ZAR archive: the stored <c>.zar</c> bytes for the classic algorithms and the
    /// extracted file tree for the xdvdfs tree hash.
    /// </summary>
    /// <param name="imagePath">Path of the ZAR archive.</param>
    /// <param name="token">Token that cancels the operation.</param>
    /// <returns>The archive hashes and the tree hash.</returns>
    private ImageHashes ComputeZar(string imagePath, CancellationToken token)
    {
        using var reader = ZArchiveReader.TryOpen(imagePath, ZarImageExplorer.CreateReaderOptions(),
            out var failure);
        if (reader is null)
        {
            throw new InvalidDataException($"Not a valid ZAR archive ({failure}): {imagePath}");
        }

        var totalBytes = reader.TotalUncompressedSize > long.MaxValue
            ? long.MaxValue
            : (long)reader.TotalUncompressedSize;

        var stored = ComputeStoredFileHashes(imagePath, token);
        var treeHash = XisoChecksum.ComputeZarChecksumHex(imagePath, token);

        return new ImageHashes(totalBytes, stored.Crc32, stored.Md5, stored.Sha1, stored.Sha256, null,
            treeHash,
            "Whole-file hashes cover the stored .zar archive; the tree hash covers its extracted contents.");
    }

    /// <summary>
    /// Hashes an STFS package: the stored package bytes for the classic algorithms and the
    /// extracted file tree for the xdvdfs tree hash.
    /// </summary>
    /// <param name="packagePath">Path of the STFS package.</param>
    /// <param name="token">Token that cancels the operation.</param>
    /// <returns>The package hashes and the tree hash.</returns>
    private ImageHashes ComputeStfs(string packagePath, CancellationToken token)
    {
        var packageLength = new FileInfo(packagePath).Length;
        var stored = ComputeStoredFileHashes(packagePath, token);
        var treeHash = XisoChecksum.ComputeStfsChecksumHex(packagePath, token);

        return new ImageHashes(packageLength, stored.Crc32, stored.Md5, stored.Sha1, stored.Sha256, null,
            treeHash,
            "Whole-file hashes cover the stored STFS package; the tree hash covers its extracted contents.");
    }

    /// <summary>
    /// Streams a file once and returns its CRC-32, MD5, SHA-1 and SHA-256 digests.
    /// </summary>
    /// <param name="path">File to hash.</param>
    /// <param name="token">Token that cancels the operation.</param>
    /// <returns>The four digests in lowercase hexadecimal.</returns>
    private static StoredFileHashes ComputeStoredFileHashes(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            CopyBufferSize, FileOptions.SequentialScan);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            var crc = 0xFFFFFFFFu;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();

                md5.AppendData(buffer, 0, read);
                sha1.AppendData(buffer, 0, read);
                sha256.AppendData(buffer, 0, read);

                for (var i = 0; i < read; i++)
                {
                    crc ^= buffer[i];
                    for (var bit = 0; bit < 8; bit++)
                    {
                        crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
                    }
                }
            }

            return new StoredFileHashes(
                (crc ^ 0xFFFFFFFFu).ToString("x8", CultureInfo.InvariantCulture),
                Convert.ToHexStringLower(md5.GetHashAndReset()),
                Convert.ToHexStringLower(sha1.GetHashAndReset()),
                Convert.ToHexStringLower(sha256.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Digests of a stored archive file.</summary>
    /// <param name="Crc32">CRC-32 in lowercase hexadecimal.</param>
    /// <param name="Md5">MD5 in lowercase hexadecimal.</param>
    /// <param name="Sha1">SHA-1 in lowercase hexadecimal.</param>
    /// <param name="Sha256">SHA-256 in lowercase hexadecimal.</param>
    private readonly record struct StoredFileHashes(string Crc32, string Md5, string Sha1, string Sha256);
}
