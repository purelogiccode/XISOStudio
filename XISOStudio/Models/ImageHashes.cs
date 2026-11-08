namespace XISOStudio.Models;

/// <summary>
/// Checksums for one image. Whole-image hashes cover the decompressed sector image; the tree
/// hash is the xdvdfs-compatible sorted-path SHA3-256 over the file tree, so an ISO, a ZAR and
/// an STFS package holding the same files produce the same tree hash.
/// </summary>
/// <param name="TotalBytes">Logical (decompressed) image or archive length in bytes.</param>
/// <param name="Crc32">Lowercase CRC-32 of the whole image, or <c>null</c> when unavailable.</param>
/// <param name="Md5">Lowercase MD5 of the whole image, or <c>null</c> when unavailable.</param>
/// <param name="Sha1">Lowercase SHA-1 of the whole image, or <c>null</c> when unavailable.</param>
/// <param name="Sha256">Lowercase SHA-256 of the whole image, or <c>null</c> when unavailable.</param>
/// <param name="ImageSha3256">Lowercase SHA3-256 of the whole image, or <c>null</c> when unavailable.</param>
/// <param name="TreeSha3256">Lowercase sorted-path SHA3-256 of the file tree, or <c>null</c> when unavailable.</param>
/// <param name="Note">Explanation shown to the user when a value is unavailable or covers a different scope.</param>
public sealed record ImageHashes(
    long TotalBytes,
    string? Crc32,
    string? Md5,
    string? Sha1,
    string? Sha256,
    string? ImageSha3256,
    string? TreeSha3256,
    string? Note);
