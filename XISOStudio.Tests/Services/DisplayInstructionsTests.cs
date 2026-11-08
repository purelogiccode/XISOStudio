using System.Globalization;
using Serilog.Events;
using XISOStudio.Services;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests the startup instruction output produced by <c>DisplayInstructions</c>.</summary>
public class DisplayInstructionsTests
{
    [Fact]
    public void DisplayInitialInstructionsWhenNotInitializedDoesNotThrow()
    {
        var exception = Record.Exception(DisplayInstructions.DisplayInitialInstructions);
        Assert.Null(exception);
    }

    [Fact]
    public void DisplayInitialInstructionsWhenInitializedLogsWelcomeMessage()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.True(logger.HasMessage("Welcome to 'XISO Studio'."));
    }

    [Fact]
    public void DisplayInitialInstructionsLogsApplicationFunctions()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.True(logger.HasMessage("This application provides"));
        Assert.True(logger.HasMessage("Convert"));
        Assert.True(logger.HasMessage("Test Integrity"));
        Assert.True(logger.HasMessage("Decompress"));
        Assert.True(logger.HasMessage("Explorer"));
        Assert.True(logger.HasMessage("CHD"));
    }

    [Fact]
    public void DisplayInitialInstructionsLogsPlatformWarning()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.True(logger.HasMessage("IMPORTANT: This tool ONLY works with Xbox"));
    }

    [Fact]
    public void DisplayInitialInstructionsLogsReadyMessage()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.True(logger.HasMessage("--- Ready ---"));
    }

    [Fact]
    public void DisplayInitialInstructionsLogsEngineStatus()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.True(logger.HasMessage("XISOSharp"));
        Assert.True(logger.HasMessage("CHDSharp"));
    }

    [Fact]
    public void DisplayInitialInstructionsLogsExactlyFifteenInformationEvents()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.Equal(15, logger.Events.Count);
    }

    [Fact]
    public void DisplayInitialInstructionsLogsOnlyInformationLevel()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.All(logger.Events, static logEvent => Assert.Equal(LogEventLevel.Information, logEvent.Level));
    }

    [Fact]
    public void DisplayInitialInstructionsLogsFourBlankSeparatorLines()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        var blankCount = logger.Events.Count(static logEvent =>
            string.IsNullOrEmpty(logEvent.RenderMessage(CultureInfo.InvariantCulture)));
        Assert.Equal(4, blankCount);
    }

    [Fact]
    public void DisplayInitialInstructionsLogsConvertTabInstructions()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.True(
            logger.HasMessage(
                "1. Convert: Converts standard Xbox ISO files to optimized XISO, ZAR, CSO, CCI, or CHD format."));
        Assert.True(logger.HasMessage("Supports archives (.zip, .7z, .rar)."));
    }

    [Fact]
    public void DisplayInitialInstructionsLogsTestIntegrityInstructions()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.True(logger.HasMessage(
            "2. Test Integrity: Verifies ISO/XISO/IMG/CSO/CCI (XDVDFS), ZAR, Xbox CHD and STFS/XBLA package structure and readability, with repair and salvage."));
        Assert.True(logger.HasMessage("does NOT perform data checksum (MD5/SHA) verification."));
        Assert.True(logger.HasMessage(
            "3. Decompress: Converts CSO, CCI, ZAR, CHD and GOD/SVOD images back into plain ISO files."));
    }

    [Fact]
    public void DisplayInitialInstructionsLogsExplorerInstructions()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.True(logger.HasMessage(
            "4. Explorer: Explore the content of .iso, .xiso, .img, .cso, .cci, .zar, .chd and GOD/SVOD containers, with metadata, checksums, extraction and split/join tools."));
    }

    [Fact]
    public void DisplayInitialInstructionsLogsTabsPhrase()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.True(logger.HasMessage("available in the tabs above"));
    }

    [Fact]
    public void DisplayInitialInstructionsLogsXbox360SupportLine()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.True(logger.HasMessage("This tool ONLY works with Xbox and Xbox 360 ISO files."));
    }

    [Fact]
    public void DisplayInitialInstructionsLogsPlayStationLimitationLine()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.True(
            logger.HasMessage("It cannot convert or test ISOs from PlayStation, PlayStation 2, or other consoles."));
    }

    [Fact]
    public void DisplayInitialInstructionsLogsEngineEnabledInfo()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        Assert.True(logger.HasMessage(
            "XISOSharp and CHDSharp conversion engines loaded. XISO/ZAR/CSO/CCI and CHD conversion, and ISO decompression, are enabled."));
    }

    [Fact]
    public void DisplayInitialInstructionsLogsWelcomeBeforeReady()
    {
        var logger = new TestLogger();
        DisplayInstructions.Initialize(logger.Logger);

        DisplayInstructions.DisplayInitialInstructions();

        var messages = logger.Events.Select(static logEvent =>
            logEvent.RenderMessage(CultureInfo.InvariantCulture)).ToList();
        var welcomeIndex = messages.FindIndex(static message =>
            message.Contains("Welcome to 'XISO Studio'.", StringComparison.Ordinal));
        var readyIndex = messages.FindIndex(static message =>
            message.Contains("--- Ready ---", StringComparison.Ordinal));

        Assert.True(welcomeIndex >= 0);
        Assert.True(readyIndex > welcomeIndex);
    }

    [Fact]
    public void InitializeReplacesPreviousLogger()
    {
        var firstLogger = new TestLogger();
        var secondLogger = new TestLogger();

        DisplayInstructions.Initialize(firstLogger.Logger);
        DisplayInstructions.DisplayInitialInstructions();
        var firstCount = firstLogger.Events.Count;

        DisplayInstructions.Initialize(secondLogger.Logger);
        DisplayInstructions.DisplayInitialInstructions();

        Assert.True(firstCount > 0);
        Assert.Equal(firstCount, firstLogger.Events.Count);
        Assert.Equal(firstCount, secondLogger.Events.Count);
    }

    [Fact]
    public void DisplayInitialInstructionsAfterInitializeWithNullDoesNotThrow()
    {
        DisplayInstructions.Initialize(null!);

        var exception = Record.Exception(DisplayInstructions.DisplayInitialInstructions);

        Assert.Null(exception);
    }
}