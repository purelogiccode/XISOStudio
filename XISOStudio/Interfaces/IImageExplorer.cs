using XISOSharp.Models;
using XISOStudio.Models;

namespace XISOStudio.Interfaces;

/// <summary>
/// A UI-agnostic explorer over one open image or archive: plain ISO and CISO
/// (<c>.cso</c>) images, GOD/SVOD containers and ZAR archives via XISOSharp, and
/// CHD images via CHDSharp (decompressed on demand). Internal paths use forward
/// slashes and are case-insensitive; <c>"/"</c> is the root.
/// </summary>
public interface IImageExplorer : IDisposable
{
    /// <summary>
    /// Lists the direct children of a directory within the container.
    /// </summary>
    /// <param name="internalPath">Directory path (<c>"/"</c> for the root).</param>
    /// <exception cref="InvalidDataException">
    /// Thrown when the path does not exist or names a file instead of a directory.
    /// </exception>
    /// <returns>The entries directly inside the directory.</returns>
    IReadOnlyList<ImageEntry> ListChildren(string internalPath);

    /// <summary>
    /// Copies a single file or directory out of the container (directories recurse).
    /// An existing destination is overwritten.
    /// </summary>
    /// <param name="internalPath">Source path within the container.</param>
    /// <param name="destPath">Destination path on the local filesystem.</param>
    /// <exception cref="InvalidDataException">Thrown when the internal path does not exist.</exception>
    void CopyOut(string internalPath, string destPath);

    /// <summary>
    /// Reads image-wide title metadata. Never throws for an image that opened
    /// successfully: unsupported or absent metadata is reported through
    /// <see cref="ImageMetadata.UnavailableReason"/>.
    /// </summary>
    /// <returns>The metadata shown by the explorer details panel.</returns>
    ImageMetadata GetMetadata();

    /// <summary>
    /// Reads one XBE image asset (logo, title image or save image) from a file in
    /// the container.
    /// </summary>
    /// <param name="internalPath">Image-internal path of the <c>.xbe</c> file.</param>
    /// <param name="kind">Asset kind to read.</param>
    /// <returns>The asset, or <c>null</c> when it is absent or the container has no XBE image support.</returns>
    XbeImage? GetXbeImage(string internalPath, XbeImageKind kind);
}