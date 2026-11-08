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
            logger.Information("This application provides three main functions, available in the tabs above:");
            logger.Information(
                "1. Convert: Converts standard Xbox ISO files to optimized XISO, ZAR, CSO, or CHD format. Supports archives (.zip, .7z, .rar).");
            logger.Information(
                "2. Test Integrity: Verifies ISO/CSO/CCI (XDVDFS), ZAR, and Xbox CHD structure and readability.");
            logger.Information(
                "   NOTE: This test checks if the image is structurally valid and readable. It does NOT perform data checksum (MD5/SHA) verification.");
            logger.Information(
                "3. Explorer: Explore the content of .iso, .cso, .cci, .zar, .chd and GOD/SVOD containers, with title metadata and XBE image previews.");
            logger.Information("");
            logger.Information("IMPORTANT: This tool ONLY works with Xbox and Xbox 360 ISO files.");
            logger.Information("It cannot convert or test ISOs from PlayStation, PlayStation 2, or other consoles.");
            logger.Information("");

            // XISO/ZAR/CSO conversion is performed in-process by XISOSharp; CHD by CHDSharp
            logger.Information(
                "INFO: XISOSharp and CHDSharp conversion engines loaded. XISO/ZAR/CSO and CHD conversion are enabled.");

            logger.Information("");

            logger.Information("--- Ready ---");
        }
    }
}