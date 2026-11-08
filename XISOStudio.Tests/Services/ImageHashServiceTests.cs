using System.Text;
using XISOSharp;
using XISOStudio.Services;
using XISOStudio.Tests.Fixtures;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests image checksum computation across ISO, CISO, ZAR and CHD containers.</summary>
public sealed class ImageHashServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(),
        $"ImageHashServiceTests_{Guid.NewGuid():N}");

    private readonly ImageHashService _service = new(new TestLogger().Logger);

    public ImageHashServiceTests()
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

    [Fact]
    public async Task IsoHashesMatchTheLibrary()
    {
        var isoPath = CreateIso();
        var expected = XisoImageHash.Compute(isoPath);

        var hashes = await _service.ComputeAsync(isoPath);

        Assert.Equal(expected.Crc32, hashes.Crc32);
        Assert.Equal(expected.Md5, hashes.Md5);
        Assert.Equal(expected.Sha1, hashes.Sha1);
        Assert.Equal(expected.Sha256, hashes.Sha256);
        Assert.Equal(expected.Sha3256, hashes.ImageSha3256);
        Assert.False(string.IsNullOrEmpty(hashes.TreeSha3256));
        Assert.Null(hashes.Note);
    }

    [Fact]
    public async Task CisoHashesEqualTheIsoHashes()
    {
        var isoPath = CreateIso();
        var csoPath = Path.Combine(_tempRoot, "game.cso");
        Assert.Equal(0, CisoWriter.CompressToCso(isoPath, csoPath));

        var isoHashes = await _service.ComputeAsync(isoPath);
        var csoHashes = await _service.ComputeAsync(csoPath);

        Assert.Equal(isoHashes.Sha256, csoHashes.Sha256);
        Assert.Equal(isoHashes.ImageSha3256, csoHashes.ImageSha3256);
        Assert.Equal(isoHashes.TreeSha3256, csoHashes.TreeSha3256);
    }

    [Fact]
    public async Task ZarHashesCoverStoredArchiveAndExtractedTree()
    {
        var isoPath = CreateIso();
        var zarPath = Path.Combine(_tempRoot, "game.zar");
        Assert.True(XisoZarchive.CreateZar(isoPath, zarPath, quiet: true));

        var isoHashes = await _service.ComputeAsync(isoPath);
        var zarHashes = await _service.ComputeAsync(zarPath);

        Assert.False(string.IsNullOrEmpty(zarHashes.Crc32));
        Assert.False(string.IsNullOrEmpty(zarHashes.Md5));
        Assert.False(string.IsNullOrEmpty(zarHashes.Sha1));
        Assert.False(string.IsNullOrEmpty(zarHashes.Sha256));
        Assert.Null(zarHashes.ImageSha3256);
        Assert.Equal(isoHashes.TreeSha3256, zarHashes.TreeSha3256);
        Assert.NotNull(zarHashes.Note);
    }

    [Fact]
    public async Task ChdHashesEqualTheIsoHashes()
    {
        var isoPath = CreateIso();
        var chdPath = ChdTestHelper.CreateDvdChd(isoPath, Path.Combine(_tempRoot, "game.chd"));

        var isoHashes = await _service.ComputeAsync(isoPath);
        var chdHashes = await _service.ComputeAsync(chdPath);

        Assert.Equal(isoHashes.Sha256, chdHashes.Sha256);
        Assert.Equal(isoHashes.ImageSha3256, chdHashes.ImageSha3256);
        Assert.Equal(isoHashes.TreeSha3256, chdHashes.TreeSha3256);
        Assert.NotNull(chdHashes.Note);
    }

    [Fact]
    public async Task StfsHashesMatchTheEquivalentIsoTree()
    {
        var isoPath = CreateIso();
        var packagePath = Path.Combine(_tempRoot, "584109B7");
        SyntheticStfsPackage.Create(packagePath,
        [
            new SyntheticStfsFile("default.xbe", Encoding.UTF8.GetBytes("fake xbe content")),
            new SyntheticStfsFile("media/data.bin", [1, 2, 3, 4, 5])
        ]);

        var isoHashes = await _service.ComputeAsync(isoPath);
        var stfsHashes = await _service.ComputeAsync(packagePath);

        Assert.Equal(isoHashes.TreeSha3256, stfsHashes.TreeSha3256);
        Assert.False(string.IsNullOrEmpty(stfsHashes.Crc32));
        Assert.False(string.IsNullOrEmpty(stfsHashes.Md5));
        Assert.False(string.IsNullOrEmpty(stfsHashes.Sha1));
        Assert.False(string.IsNullOrEmpty(stfsHashes.Sha256));
        Assert.Null(stfsHashes.ImageSha3256);
        Assert.NotNull(stfsHashes.Note);
    }

    [Fact]
    public void FileHashesProduceOneEntryPerFile()
    {
        var isoPath = CreateIso();

        var entries = _service.ComputeFileHashes(isoPath);

        Assert.Equal(2, entries.Count);
        Assert.All(entries, static entry => Assert.Equal(64, entry.HashHex.Length));
        Assert.Contains(entries, static entry => entry.Path.EndsWith("default.xbe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingFileThrows()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.ComputeAsync(Path.Combine(_tempRoot, "missing.iso")));
    }
}
