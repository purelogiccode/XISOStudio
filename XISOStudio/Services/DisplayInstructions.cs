using Serilog;

namespace XISOStudio.Services;

/// <summary>
/// Logs the welcome banner and usage instructions shown when the application starts.
/// </summary>
public static class DisplayInstructions
{
    private static volatile ILogger? _logger;

    /// <summary>
    /// Supplies the logger used by <see cref="DisplayInitialInstructions"/>.
    /// </summary>
    /// <param name="logger">Logger that receives the instructions.</param>
    public static void Initialize(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Writes the welcome message and the description of the application's main tabs to the
    /// configured logger.
    /// </summary>
    public static void DisplayInitialInstructions()
    {
        var logger = _logger;
        if (logger != null)
        {
            logger.Information("Welcome to 'XISO Studio'.");
            logger.Information("");
            logger.Information("This application provides four main functions, available in the tabs above:");
            logger.Information(
                "1. Convert: Converts standard Xbox ISO files to optimized XISO, ZAR, CSO, CCI, or CHD format. Supports archives (.zip, .7z, .rar).");
            logger.Information(
                "2. Test Integrity: Verifies ISO/XISO/IMG/CSO/CCI (XDVDFS), ZAR, Xbox CHD and STFS/XBLA package structure and readability, with repair and salvage.");
            logger.Information(
                "   NOTE: The structural test does NOT perform data checksum (MD5/SHA) verification.");
            logger.Information(
                "3. Decompress: Converts CSO, CCI, ZAR, CHD and GOD/SVOD images back into plain ISO files.");
            logger.Information(
                "4. Explorer: Explore the content of .iso, .xiso, .img, .cso, .cci, .zar, .chd and GOD/SVOD containers, with metadata, checksums, extraction and split/join tools.");
            logger.Information("");
            logger.Information("IMPORTANT: This tool ONLY works with Xbox and Xbox 360 ISO files.");
            logger.Information("It cannot convert or test ISOs from PlayStation, PlayStation 2, or other consoles.");
            logger.Information("");

            // XISO/ZAR/CSO/CCI conversion is performed in-process by XISOSharp; CHD by CHDSharp
            logger.Information(
                "INFO: XISOSharp and CHDSharp conversion engines loaded. XISO/ZAR/CSO/CCI and CHD conversion, and ISO decompression, are enabled.");

            logger.Information("");

            logger.Information("--- Ready ---");
        }
    }
}