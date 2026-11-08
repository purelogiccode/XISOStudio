using CCISharp;
using XISOSharp;
using XISOStudio.Models;
using XISOStudio.Services;
using XISOStudio.Tests.Fixtures;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests decompression of CISO, CCI, ZAR, CHD and GOD containers back to ISO.</summary>
public sealed class ImageConversionServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(),
        $"ImageConversionServiceTests_{Guid.NewGuid():N}");

    private readonly ImageConversionService _service = new(new TestLogger().Logger);

    public ImageConversionServiceTests()
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

    private string CreateIso()
    {
        var sourceDirectory = Path.Combine(_tempRoot, "source");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "default.xbe"), "fake xbe content");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "media"));
        File.WriteAllBytes(Path.Combine(sourceDirectory, "media", "data.bin"), [1, 2, 3, 4, 5]);

        var isoPath = Path.Combine(_tempRoot, "game.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(sourceDirectory, isoPath));
        return isoPath;
    }

    private async Task AssertDecompressedAsync(string sourcePath, string expectedIsoPath)
    {
        var outputFolder = Path.Combine(_tempRoot, "out");
        var status = await _service.DecompressToIsoAsync(sourcePath, outputFolder, "result.iso", true,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        var outputPath = Path.Combine(outputFolder, "result.iso");
        Assert.True(File.Exists(outputPath));
        Assert.True(XisoReader.AuditXiso(outputPath, requireOptimizedTag: false).IsValid);

        // ZAR is a file tree, so it is repacked: compare the xdvdfs tree hash (paths plus
        // content), not the byte-identical sector layout.
        Assert.Equal(XisoChecksum.ComputeImageChecksumHex(expectedIsoPath),
            XisoChecksum.ComputeImageChecksumHex(outputPath));
    }

    [Fact]
    public async Task CisoIsDecompressedToIso()
    {
        var isoPath = CreateIso();
        var csoPath = Path.Combine(_tempRoot, "game.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(isoPath, csoPath));

        await AssertDecompressedAsync(csoPath, isoPath);
    }

    [Fact]
    public async Task CciIsDecompressedToIso()
    {
        var isoPath = CreateIso();
        var cciPath = Path.Combine(_tempRoot, "game.cci");
        Assert.Equal(CciError.None, CciWriter.Compress(isoPath, cciPath, new CciWriteOptions { SplitBytes = 0 }));

        await AssertDecompressedAsync(cciPath, isoPath);
    }

    [Fact]
    public async Task ZarIsDecompressedToIso()
    {
        var isoPath = CreateIso();
        var zarPath = Path.Combine(_tempRoot, "game.zar");
        Assert.True(XisoZarchive.CreateZar(isoPath, zarPath, quiet: true));

        await AssertDecompressedAsync(zarPath, isoPath);
    }

    [Fact]
    public async Task ChdIsDecompressedToIso()
    {
        var isoPath = CreateIso();
        var chdPath = ChdTestHelper.CreateDvdChd(isoPath, Path.Combine(_tempRoot, "game.chd"));

        await AssertDecompressedAsync(chdPath, isoPath);
    }

    [Fact]
    public async Task GodIsDecompressedToIso()
    {
        var isoPath = CreateIso();
        var godDirectory = Directory.CreateDirectory(Path.Combine(_tempRoot, "god")).FullName;
        var header = SyntheticGodContainer.CreateFromIso(
            isoPath,
            Path.Combine(godDirectory, "58480813ABCDEF0123456789"),
            "GOD Decompress Test",
            0x58480813,
            discNumber: 1,
            discCount: 1);

        await AssertDecompressedAsync(header, isoPath);
    }

    [Fact]
    public async Task PlainIsoIsSkipped()
    {
        var isoPath = CreateIso();

        var status = await _service.DecompressToIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), "result.iso",
            false, new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Skipped, status);
        Assert.False(File.Exists(Path.Combine(_tempRoot, "out", "result.iso")));
    }

    [Fact]
    public async Task InvalidInputIsRejected()
    {
        var badPath = Path.Combine(_tempRoot, "bad.cso");
        await File.WriteAllTextAsync(badPath, "not a ciso file");

        var status = await _service.DecompressToIsoAsync(badPath, Path.Combine(_tempRoot, "out"), "result.iso",
            false, new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.True(status is FileProcessingStatus.InvalidInput or FileProcessingStatus.Failed);
    }
}
