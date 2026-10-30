using System.Globalization;

namespace XISOStudio.Services;

/// <summary>
///     Extension filters shared by the UI file lists and the orchestrator folder scans so
///     both always offer exactly the same files.
/// </summary>
public static class SupportedFiles
{
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
    ///     plain ISO, CISO (<c>.cso</c>, including the first part of a split set),
    ///     ZAR archive, or CHD image.
    /// </summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns><c>true</c> when the path is an image the integrity test can verify; otherwise <c>false</c>.</returns>
    public static bool IsTestable(string path)
    {
        return IsImage(path) && !IsSplitContinuationPart(path);
    }

    /// <summary>
    ///     Returns true when the path has an image extension (<c>.iso</c>, <c>.cso</c>,
    ///     <c>.zar</c>, or <c>.chd</c>) regardless of whether it is a standalone image or a
    ///     continuation part of a split CISO set.
    /// </summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns><c>true</c> when the path is an image file; otherwise <c>false</c>.</returns>
    public static bool IsImage(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".iso" or ".cso" or ".zar" or ".chd";
    }

    /// <summary>
    ///     Split CISO parts are named <c>game.1.cso</c>, <c>game.2.cso</c>, …; only part 1
    ///     carries the header and opens the whole image, so part 2+ must not be listed as
    ///     standalone images.
    /// </summary>
    /// <param name="path">Path to inspect.</param>
    /// <returns><c>true</c> when the file name ends in a split part number of 2 or greater; otherwise <c>false</c>.</returns>
    private static bool IsSplitContinuationPart(string path)
    {
        // Only CISO part sets use the ".N.cso" naming convention. A plain ISO, ZAR, or
        // CHD whose name happens to end in a number (for example "Game.2.iso") is a
        // standalone image and must remain testable.
        if (!Path.GetExtension(path).Equals(".cso", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(path);
        var lastDot = stem.LastIndexOf('.');
        if (lastDot < 0) return false;

        return int.TryParse(stem.AsSpan(lastDot + 1), NumberStyles.None, CultureInfo.InvariantCulture,
            out var part) && part >= 2;
    }
}