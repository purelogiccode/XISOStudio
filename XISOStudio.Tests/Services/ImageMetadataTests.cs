using XISOSharp;
using XISOSharp.Models;
using XISOStudio.Services;
using XISOStudio.Tests.Fixtures;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>
/// Tests the explorer metadata and XBE preview surface across XISO, CISO, GOD/SVOD,
/// CHD and ZAR containers, using deterministic synthetic fixtures.
/// </summary>
public sealed class ImageMetadataTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"ImageMetadataTests_{Guid.NewGuid():N}");

    public ImageMetadataTests()
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

    private string CreateXbeXiso(string name = "game.iso")
    {
        var sourceDir = Path.Combine(_tempRoot, "xbe-source");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllBytes(Path.Combine(sourceDir, "default.xbe"),
            SyntheticXbe.BuildWithImages("Metadata Test", 0x4D530123));

        var isoPath = Path.Combine(_tempRoot, name);
        Assert.Equal(0, XisoWriter.PackFromDirectory(sourceDir, isoPath));
        return isoPath;
    }

    private string CreateGod(out string dataDirectory)
    {
        var isoPath = CreateXbeXiso("god-source.iso");
        var godDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "god")).FullName;
        var header = Path.Combine(godDirectory, "58480813ABCDEF0123456789");
        SyntheticGodContainer.CreateFromIso(isoPath, header, "GOD Metadata Test", 0x58480813, discNumber: 1,
            discCount: 2);
        dataDirectory = header + ".data";
        return header;
    }

    [Fact]
    public void XisoExplorer_ReportsTitleMetadataAndXbeAssets()
    {
        using var explorer = ImageExplorerFactory.Open(CreateXbeXiso());

        var metadata = explorer.GetMetadata();
        Assert.Equal("Iso", metadata.Format);
        Assert.Equal("Metadata Test", metadata.TitleName);
        Assert.Equal("0x4D530123", metadata.TitleIdHex);
        Assert.Equal("/default.xbe", metadata.ExecutablePath);
        Assert.Equal("Xbe", metadata.Kind);
        Assert.True(metadata.HasLogo);
        Assert.True(metadata.HasTitleImage);
        Assert.True(metadata.HasSaveImage);
        Assert.Null(metadata.UnavailableReason);

        foreach (var kind in new[] { XbeImageKind.Logo, XbeImageKind.Title, XbeImageKind.Save })
        {
            var image = explorer.GetXbeImage("/default.xbe", kind);
            Assert.NotNull(image);
            Assert.NotNull(image.Png);
            Assert.Equal([0x89, 0x50, 0x4E, 0x47], image.Png![..4]);
            Assert.True(image.Width > 0);
            Assert.True(image.Height > 0);
        }
    }

    [Fact]
    public void GodExplorer_OpensHeaderDataFolderAndParentFolder()
    {
        var header = CreateGod(out var dataDirectory);

        using (var explorer = ImageExplorerFactory.Open(header))
        {
            Assert.Contains(explorer.ListChildren("/"), static e => e is { Name: "default.xbe", IsDirectory: false });
            var metadata = explorer.GetMetadata();
            Assert.Equal("God", metadata.Format);
            Assert.Equal("GOD Metadata Test", metadata.TitleName);
            Assert.Equal("0x58480813", metadata.TitleIdHex);
            Assert.Equal(1u, metadata.DiscNumber);
            Assert.Equal(2u, metadata.DiscCount);
            Assert.Equal("GOD/SVOD", metadata.Kind);
        }

        using (var explorer = ImageExplorerFactory.Open(dataDirectory))
        {
            Assert.Contains(explorer.ListChildren("/"), static e => e is { Name: "default.xbe", IsDirectory: false });
            Assert.Equal("GOD Metadata Test", explorer.GetMetadata().TitleName);
        }

        using (var explorer = ImageExplorerFactory.Open(Path.GetDirectoryName(header)!))
        {
            Assert.Contains(explorer.ListChildren("/"), static e => e is { Name: "default.xbe", IsDirectory: false });
            Assert.Equal("GOD Metadata Test", explorer.GetMetadata().TitleName);
        }
    }

    [Fact]
    public void GodExplorer_ReadsXbeAssetsFromTheMappedPartition()
    {
        var header = CreateGod(out _);

        using var explorer = ImageExplorerFactory.Open(header);

        var image = explorer.GetXbeImage("/default.xbe", XbeImageKind.Logo);
        Assert.NotNull(image);
        Assert.NotNull(image.Png);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], image.Png![..4]);
    }

    [Fact]
    public void ChdExplorer_ReportsMetadataAndReadsXbeAssets()
    {
        var isoPath = CreateXbeXiso("chd-source.iso");
        var chdPath = ChdTestHelper.CreateDvdChd(isoPath, Path.Combine(_tempRoot, "game.chd"));

        using var explorer = ImageExplorerFactory.Open(chdPath);

        var metadata = explorer.GetMetadata();
        Assert.Equal("Chd", metadata.Format);
        Assert.Equal("Metadata Test", metadata.TitleName);
        Assert.Equal("/default.xbe", metadata.ExecutablePath);

        var image = explorer.GetXbeImage("/default.xbe", XbeImageKind.Title);
        Assert.NotNull(image);
        Assert.NotNull(image.Png);
    }

    [Fact]
    public void ZarExplorer_ReportsMetadataAsUnavailable()
    {
        var isoPath = CreateXbeXiso("zar-source.iso");
        var zarPath = Path.Combine(_tempRoot, "game.zar");
        Assert.True(XisoZarchive.CreateZar(isoPath, zarPath, quiet: true));

        using var explorer = ImageExplorerFactory.Open(zarPath);

        var metadata = explorer.GetMetadata();
        Assert.Equal("Zar", metadata.Format);
        Assert.NotNull(metadata.UnavailableReason);
        Assert.Null(explorer.GetXbeImage("/default.xbe", XbeImageKind.Logo));
    }

    [Fact]
    public void ImageExplorerFactory_MissingFileStillThrowsFileNotFoundException()
    {
        var missing = Path.Combine(_tempRoot, "missing.iso");

        Assert.Throws<FileNotFoundException>(() => ImageExplorerFactory.Open(missing));
    }
}
