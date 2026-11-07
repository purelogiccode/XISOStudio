namespace XISOStudio.Models;

/// <summary>
/// Image-wide metadata shown by the explorer details panel. Original-Xbox values
/// come from the XBE certificate; Xbox 360 values come from the XEX XDBF/XSRC
/// resources or, for GOD/SVOD containers, from the XContent header.
/// </summary>
public sealed record ImageMetadata
{
    /// <summary>Detected container format (for example <c>Iso</c>, <c>Ciso</c>, <c>God</c>, <c>Chd</c> or <c>Zar</c>).</summary>
    public string Format { get; init; } = "";

    /// <summary>Image or archive path as opened.</summary>
    public string Path { get; init; } = "";

    /// <summary>Logical (decompressed) image length in bytes.</summary>
    public long Length { get; init; }

    /// <summary>Game title name, when the image carries metadata.</summary>
    public string TitleName { get; init; } = "";

    /// <summary>Title ID formatted as <c>0xXXXXXXXX</c>, when available.</summary>
    public string TitleIdHex { get; init; } = "";

    /// <summary>Human-readable game region summary, when available.</summary>
    public string Region { get; init; } = "";

    /// <summary>Disc number of a multi-disc title, when available.</summary>
    public uint DiscNumber { get; init; }

    /// <summary>Number of discs in the set, when available.</summary>
    public uint DiscCount { get; init; }

    /// <summary>Game version, when available.</summary>
    public uint Version { get; init; }

    /// <summary>Publisher string from XDBF/XSRC, when available.</summary>
    public string Publisher { get; init; } = "";

    /// <summary>Developer string from XDBF/XSRC, when available.</summary>
    public string Developer { get; init; } = "";

    /// <summary>Genre string from XDBF/XSRC, when available.</summary>
    public string Genre { get; init; } = "";

    /// <summary>Description/sell text from XDBF/XSRC, when available.</summary>
    public string Description { get; init; } = "";

    /// <summary>Image-internal path of the root executable, when one was found.</summary>
    public string ExecutablePath { get; init; } = "";

    /// <summary>Executable/metadata kind (XBE, XEX, GOD/SVOD, archive).</summary>
    public string Kind { get; init; } = "";

    /// <summary>Whether an XBE logo bitmap is present.</summary>
    public bool HasLogo { get; init; }

    /// <summary>Whether an XBE <c>$$XTIMAGE</c> title image is present.</summary>
    public bool HasTitleImage { get; init; }

    /// <summary>Whether an XBE <c>$$XSIMAGE</c> save image is present.</summary>
    public bool HasSaveImage { get; init; }

    /// <summary>Reason title metadata or preview images are unavailable, or <c>null</c>.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>Creates metadata for a container that does not expose game-title fields.</summary>
    /// <param name="format">Container format label.</param>
    /// <param name="path">Image path.</param>
    /// <param name="length">Logical length in bytes.</param>
    /// <param name="reason">Reason title metadata is unavailable.</param>
    public static ImageMetadata Unavailable(string format, string path, long length, string reason) =>
        new()
        {
            Format = format,
            Path = path,
            Length = length,
            UnavailableReason = reason
        };
}
