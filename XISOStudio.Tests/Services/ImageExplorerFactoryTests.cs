using Serilog.Events;
using XISOStudio.Services;
using XISOSharp;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests format routing, success paths, and failure logging in <c>ImageExplorerFactory</c>.</summary>
public sealed class ImageExplorerFactoryTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"ImageExplorerFactoryTests_{Guid.NewGuid():N}");

    public ImageExplorerFactoryTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
        }
        catch
        {
            // ignored
        }
    }

    private string TempPath(string fileName)
    {
        return Path.Combine(_tempRoot, fileName);
    }

    private string CreateXiso(string name = "created.iso")
    {
        var sourceDir = Path.Combine(_tempRoot, "source");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "default.xbe"), "fake xbe content");

        var isoPath = Path.Combine(_tempRoot, name);
        Assert.Equal(0, XisoWriter.PackFromDirectory(sourceDir, isoPath));
        return isoPath;
    }

    [Fact]
    public void OpenMissingIsoThrowsFileNotFound()
    {
        var path = TempPath("missing.iso");
        Assert.Throws<FileNotFoundException>(() => ImageExplorerFactory.Open(path));
    }

    [Fact]
    public void OpenMissingIsoLogsFactoryInformation()
    {
        var path = TempPath("missing-log.iso");
        var logger = new TestLogger();

        Assert.Throws<FileNotFoundException>(() => ImageExplorerFactory.Open(path, logger.Logger));

        Assert.True(logger.HasMessage(LogEventLevel.Information, "Failed to open an image explorer"));
        Assert.True(logger.HasMessage(path));
    }

    [Fact]
    public void OpenMissingCsoThrowsFileNotFound()
    {
        var path = TempPath("missing.cso");
        Assert.Throws<FileNotFoundException>(() => ImageExplorerFactory.Open(path));
    }

    [Fact]
    public void OpenMissingZarThrowsInvalidDataWithZarMessage()
    {
        var path = TempPath("missing.zar");
        var exception = Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(path));
        Assert.Contains("ZAR archive", exception.Message, StringComparison.Ordinal);
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenMissingZarLogsFactoryInformation()
    {
        var path = TempPath("missing-log.zar");
        var logger = new TestLogger();

        var exception = Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(path, logger.Logger));

        Assert.Contains("ZAR archive", exception.Message, StringComparison.Ordinal);
        Assert.True(logger.HasMessage(LogEventLevel.Information, "Failed to open an image explorer"));
        Assert.True(logger.HasMessage(LogEventLevel.Information, "ZAR archive"));
    }

    [Fact]
    public void OpenMissingChdThrowsInvalidDataWithChdMessage()
    {
        var path = TempPath("missing.chd");
        var exception = Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(path));
        Assert.Contains("Xbox DVD image", exception.Message, StringComparison.Ordinal);
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenMissingChdLogsFactoryInformation()
    {
        var path = TempPath("missing-log.chd");
        var logger = new TestLogger();

        var exception = Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(path, logger.Logger));

        Assert.Contains("Xbox DVD image", exception.Message, StringComparison.Ordinal);
        Assert.True(logger.HasMessage(LogEventLevel.Information, "Failed to open an image explorer"));
        Assert.True(logger.HasMessage(LogEventLevel.Information, "Xbox DVD image"));
    }

    [Theory]
    [InlineData("missing.ZAR")]
    [InlineData("missing.Zar")]
    [InlineData("missing.zAr")]
    public void OpenUppercaseZarRoutesToZarExplorer(string fileName)
    {
        var path = TempPath(fileName);
        var exception = Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(path));
        Assert.Contains("ZAR archive", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing.CHD")]
    [InlineData("missing.Chd")]
    [InlineData("missing.cHd")]
    public void OpenMixedCaseChdRoutesToChdExplorer(string fileName)
    {
        var path = TempPath(fileName);
        var exception = Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(path));
        Assert.Contains("Xbox DVD image", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing.ISO")]
    [InlineData("missing.Iso")]
    [InlineData("missing.CSO")]
    public void OpenUppercasePlainImageRoutesToXisoExplorer(string fileName)
    {
        var path = TempPath(fileName);
        Assert.Throws<FileNotFoundException>(() => ImageExplorerFactory.Open(path));
    }

    [Theory]
    [InlineData("missing.bin")]
    [InlineData("missing.dat")]
    [InlineData("myimage")]
    [InlineData("myimage.")]
    public void OpenUnknownExtensionUsesXisoExplorer(string fileName)
    {
        var path = TempPath(fileName);
        var exception = Assert.Throws<FileNotFoundException>(() => ImageExplorerFactory.Open(path));
        Assert.DoesNotContain("ZAR archive", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Xbox DVD image", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("empty.iso")]
    [InlineData("empty.cso")]
    [InlineData("empty.bin")]
    public void OpenExistingEmptyPlainImageThrowsXisoFormatException(string fileName)
    {
        var path = TempPath(fileName);
        File.WriteAllBytes(path, []);

        Assert.Throws<XisoFormatException>(() => ImageExplorerFactory.Open(path));
    }

    [Fact]
    public void OpenExistingEmptyZarThrowsInvalidData()
    {
        var path = TempPath("empty.zar");
        File.WriteAllBytes(path, []);

        var exception = Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(path));
        Assert.Contains("ZAR archive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenExistingEmptyChdThrowsInvalidData()
    {
        var path = TempPath("empty.chd");
        File.WriteAllBytes(path, []);

        var exception = Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(path));
        Assert.Contains("CHD image", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenMissingFileWithoutLoggerStillThrows()
    {
        var path = TempPath("no-logger.iso");
        Assert.Throws<FileNotFoundException>(() => ImageExplorerFactory.Open(path, null));
    }

    [Fact]
    public void OpenMissingFileLogsExactlyOneInformationEvent()
    {
        var path = TempPath("single-error.chd");
        var logger = new TestLogger();

        Assert.Throws<InvalidDataException>(() => ImageExplorerFactory.Open(path, logger.Logger));

        Assert.Single(logger.Events);
        Assert.Equal(LogEventLevel.Information, logger.Events[0].Level);
        Assert.True(logger.HasMessage(LogEventLevel.Information, "Failed to open an image explorer"));
    }

    [Fact]
    public void OpenValidIsoWithUppercaseExtensionReturnsExplorer()
    {
        var isoPath = CreateXiso("valid.ISO");
        var logger = new TestLogger();

        using var explorer = ImageExplorerFactory.Open(isoPath, logger.Logger);

        Assert.NotNull(explorer);
        Assert.Empty(logger.Events);
    }

    [Fact]
    public void OpenValidZarWithUppercaseExtensionReturnsExplorer()
    {
        var isoPath = CreateXiso("valid-zar-source.iso");
        var zarPath = Path.Combine(_tempRoot, "valid.ZAR");
        Assert.True(XisoZarchive.CreateZar(isoPath, zarPath, quiet: true));

        using var explorer = ImageExplorerFactory.Open(zarPath);

        Assert.NotNull(explorer);
    }
}