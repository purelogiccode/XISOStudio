using XISOSharp;
using XISOStudio.Services;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests plain-ISO splitting and joining in <c>XisoSplitService</c>.</summary>
public sealed class XisoSplitServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(),
        $"XisoSplitServiceTests_{Guid.NewGuid():N}");

    private readonly XisoSplitService _service = new(new TestLogger().Logger);

    public XisoSplitServiceTests()
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
        File.WriteAllBytes(Path.Combine(sourceDirectory, "media", "data.bin"), new byte[128 * 1024]);

        var isoPath = Path.Combine(_tempRoot, "game.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(sourceDirectory, isoPath));
        return isoPath;
    }

    [Fact]
    public async Task SplitAndJoinRoundTripsByteIdentically()
    {
        var isoPath = CreateIso();
        var originalHash = (await XisoImageHash.ComputeAsync(isoPath)).Sha256;

        var partSize = Math.Max(2048, new FileInfo(isoPath).Length / 2 / 2048 * 2048);
        var parts = await _service.SplitAsync(isoPath, Path.Combine(_tempRoot, "game"), partSize);

        Assert.True(parts.Count >= 2);
        Assert.All(parts, static part => Assert.True(File.Exists(part)));

        var joinedPath = Path.Combine(_tempRoot, "joined.iso");
        var result = await _service.JoinAsync(parts[0], joinedPath);

        Assert.Equal(joinedPath, result);
        Assert.Equal(originalHash, (await XisoImageHash.ComputeAsync(result)).Sha256);
    }

    [Fact]
    public async Task SplitRefusesExistingParts()
    {
        var isoPath = CreateIso();
        var partSize = Math.Max(2048, new FileInfo(isoPath).Length / 2 / 2048 * 2048);
        await _service.SplitAsync(isoPath, Path.Combine(_tempRoot, "game"), partSize);

        await Assert.ThrowsAsync<IOException>(() =>
            _service.SplitAsync(isoPath, Path.Combine(_tempRoot, "game"), partSize));
    }
}
