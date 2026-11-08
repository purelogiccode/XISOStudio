namespace XISOStudio.Models;

/// <summary>
///     Output format produced by the conversion pipeline.
/// </summary>
public enum OutputFormat
{
    /// <summary>Optimized XISO image (default).</summary>
    Xiso = 0,

    /// <summary>ZArchive (<c>.zar</c>) compressed image — zstd blocks, loadable in Xenia canary.</summary>
    Zar = 1,

    /// <summary>Compressed ISO (<c>.cso</c>) — CISO v2 (LZ4), compatible with the xdvdfs ecosystem.</summary>
    Cso = 2,

    /// <summary>Compressed Hunks of Data (<c>.chd</c>) — CHD v5, readable by MAME-compatible tools.</summary>
    Chd = 3,

    /// <summary>CCI/CCIM compressed image (<c>.cci</c>) — LZ4 sectors, single file.</summary>
    Cci = 4
}