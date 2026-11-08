using System.Globalization;

namespace XISOStudio.Services;

/// <summary>
///     Extension filters shared by the UI file lists and the orchestrator folder scans so
///     both always offer exactly the same files.
/// </summary>
public static class SupportedFiles
{
    /// <summary>Cerbios Compressed Image extension.</summary>
    private const string CciExtension = ".cci";

    /// <summary>
    ///     Returns true when the path has an extension the conversion engine can process.
    ///     Compressed containers are deliberately excluded: converting a CISO/CCI/ZAR/CHD
    ///     directly to another container requires decompressing it first, which is the job
    ///     of the Decompress view.
    /// </summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns>
    ///     <c>true</c> for <c>.iso</c>, <c>.xiso</c>, <c>.img</c>, <c>.zip</c>, <c>.7z</c>
    ///     or <c>.rar</c>; otherwise <c>false</c>.
    /// </returns>
    public static bool IsConvertible(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".iso" or ".xiso" or ".img" or ".zip" or ".7z" or ".rar";
    }

    /// <summary>
    ///     Returns true when the path is an image the Decompress view can turn back into a
    ///     plain ISO. Plain ISO inputs are listed so the user can see them, but the
    ///     decompressor skips them as already uncompressed.
    /// </summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns>
    ///     <c>true</c> for every supported image extension and for extensionless GOD/SVOD
    ///     headers; otherwise <c>false</c>.
    /// </returns>
    public static bool IsDecompressible(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length == 0)
        {
            // GOD/SVOD headers are extensionless containers with a matching ".data" folder,
            // so they must be listed even though the user never types an extension. STFS
            // packages are also extensionless but are not sector images, so they stay out.
            return File.Exists(path) && StfsSharp.GodHeader.IsGodContainer(path);
        }

        return IsImage(path) && !IsSplitContinuationPart(path);
    }

    /// <summary>
    ///     Returns true when the path is a plain ISO image (<c>.iso</c>, <c>.xiso</c> or
    ///     <c>.img</c>). Used for ISO files extracted from archives, which are the only
    ///     convertible entries.
    /// </summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns><c>true</c> when the path has a plain-image extension; otherwise <c>false</c>.</returns>
    public static bool IsIso(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xiso", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".img", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns true when the path is an image the integrity test can verify:
    ///     plain ISO/XISO/IMG, CISO (<c>.cso</c>, including the first part of a split set), CCI
    ///     (<c>.cci</c>, including the first part of a split set), ZAR archive, or CHD image.
    /// </summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns><c>true</c> when the path is an image the integrity test can verify; otherwise <c>false</c>.</returns>
    public static bool IsTestable(string path)
    {
        return IsImage(path) && !IsSplitContinuationPart(path);
    }

    /// <summary>
    ///     Returns true when the path is a supported image or container: a file with an image
    ///     extension (<c>.iso</c>, <c>.xiso</c>, <c>.img</c>, <c>.cso</c>, <c>.cci</c>,
    ///     <c>.zar</c> or <c>.chd</c>) or an extensionless XContent container (a GOD/SVOD
    ///     header or a single-file XBLA/XBLIG STFS package).
    /// </summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns><c>true</c> when the path is an image file; otherwise <c>false</c>.</returns>
    public static bool IsImage(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".iso" or ".xiso" or ".img" or ".cso" or ".cci" or ".zar" or ".chd")
        {
            return true;
        }

        // Extensionless XContent containers: a GOD/SVOD header or a single-file
        // XBLA/XBLIG STFS package. The probe reads a few header bytes only.
        return extension.Length == 0 && File.Exists(path) && StfsSharp.StfsFile.IsContainer(path);
    }

    /// <summary>
    ///     Split image parts are named <c>game.1.cso</c>/<c>game.1.cci</c>/<c>game.1.iso</c>,
    ///     <c>game.2.*</c>, …. Only CISO always has a header in part 1. CCI accepts any
    ///     numbered part as an entry point, so a numeric CCI name is a continuation only when
    ///     the contiguous set starts at <c>.1</c>; a standalone <c>game.2.cci</c> remains
    ///     testable. Split ISO sets use the same rule.
    /// </summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns><c>true</c> when the file is a continuation part of a split image; otherwise <c>false</c>.</returns>
    private static bool IsSplitContinuationPart(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".cso", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetPartNumber(path, out var csoPart) && csoPart >= 2;
        }

        if (extension.Equals(CciExtension, StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetPartNumber(path, out var part) && part >= 2 &&
                   File.Exists(BuildFirstPartPath(path));
        }

        // A plain XISO/IMG, ZAR, or CHD whose name happens to end in a number (for example
        // "Game.2.img") is a standalone image and must remain testable.
        return false;
    }

    /// <summary>Parses the trailing numeric part of a split image name.</summary>
    /// <param name="path">Path to inspect.</param>
    /// <param name="part">Parsed part number, or zero when the name has no numeric suffix.</param>
    /// <returns><c>true</c> when the name ends with a positive part number.</returns>
    private static bool TryGetPartNumber(string path, out int part)
    {
        part = 0;
        var stem = Path.GetFileNameWithoutExtension(path);
        var lastDot = stem.LastIndexOf('.');
        return lastDot >= 0 && lastDot < stem.Length - 1 &&
               int.TryParse(stem.AsSpan(lastDot + 1), NumberStyles.None, CultureInfo.InvariantCulture,
                   out part) && part >= 1;
    }

    /// <summary>Builds the <c>.1</c> sibling path used to detect a split image set.</summary>
    /// <param name="path">Path of a numeric image part.</param>
    /// <returns>The matching first-part path.</returns>
    private static string BuildFirstPartPath(string path)
    {
        var extension = Path.GetExtension(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        var lastDot = stem.LastIndexOf('.');
        var firstPart = stem[..lastDot] + ".1" + extension;
        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? firstPart : Path.Combine(directory, firstPart);
    }
}
