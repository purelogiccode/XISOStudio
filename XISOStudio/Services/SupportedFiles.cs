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
    /// </summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns><c>true</c> when the extension is <c>.iso</c>, <c>.zip</c>, <c>.7z</c>, or <c>.rar</c>; otherwise <c>false</c>.</returns>
    public static bool IsConvertible(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".iso" or ".zip" or ".7z" or ".rar";
    }

    /// <summary>
    ///     Returns true when the path is a plain ISO image (<c>.iso</c>). Used for
    ///     ISO files extracted from archives, which are the only convertible entries.
    /// </summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns><c>true</c> when the path has the <c>.iso</c> extension; otherwise <c>false</c>.</returns>
    public static bool IsIso(string path)
    {
        return Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns true when the path is an image the integrity test can verify:
    ///     plain ISO, CISO (<c>.cso</c>, including the first part of a split set), CCI
    ///     (<c>.cci</c>, including the first part of a split set), ZAR archive, or CHD image.
    /// </summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns><c>true</c> when the path is an image the integrity test can verify; otherwise <c>false</c>.</returns>
    public static bool IsTestable(string path)
    {
        return IsImage(path) && !IsSplitContinuationPart(path);
    }

    /// <summary>
    ///     Returns true when the path has an image extension (<c>.iso</c>, <c>.cso</c>,
    ///     <c>.cci</c>, <c>.zar</c>, or <c>.chd</c>) regardless of whether it is a standalone
    ///     image or a continuation part of a split set.
    /// </summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns><c>true</c> when the path is an image file; otherwise <c>false</c>.</returns>
    public static bool IsImage(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".iso" or ".cso" or ".cci" or ".zar" or ".chd";
    }

    /// <summary>
    ///     Split image parts are named <c>game.1.cso</c>/<c>game.1.cci</c>, <c>game.2.*</c>, ….
    ///     Only CISO always has a header in part 1. CCI accepts any numbered part as an entry
    ///     point, so a numeric CCI name is a continuation only when the contiguous set starts
    ///     at <c>.1</c>; a standalone <c>game.2.cci</c> remains testable.
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

        if (extension.Equals(CciExtension, StringComparison.OrdinalIgnoreCase))
        {
            return TryGetPartNumber(path, out var cciPart) && cciPart >= 2 &&
                   File.Exists(BuildFirstPartPath(path));
        }

        // A plain ISO, ZAR, or CHD whose name happens to end in a number (for example
        // "Game.2.iso") is a standalone image and must remain testable.
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

    /// <summary>Builds the <c>.1</c> sibling path used to detect a split CCI set.</summary>
    /// <param name="path">Path of a numeric CCI part.</param>
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