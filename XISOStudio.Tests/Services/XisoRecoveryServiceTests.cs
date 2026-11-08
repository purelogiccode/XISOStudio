using XISOSharp;
using XISOStudio.Services;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests in-place repair and salvage in <c>XisoRecoveryService</c>.</summary>
public sealed class XisoRecoveryServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(),
        $"XisoRecoveryServiceTests_{Guid.NewGuid():N}");

    private readonly XisoRecoveryService _service = new(new TestLogger().Logger);

    public XisoRecoveryServiceTests()
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

    private string CreateIso(string name = "game.iso")
    {
        var sourceDirectory = Path.Combine(_tempRoot, "source-" + name);
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "default.xbe"), "fake xbe content");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "media"));
        File.WriteAllBytes(Path.Combine(sourceDirectory, "media", "data.bin"), [1, 2, 3, 4, 5]);

        var isoPath = Path.Combine(_tempRoot, name);
        Assert.Equal(0, XisoWriter.PackFromDirectory(sourceDirectory, isoPath));
        return isoPath;
    }

    private static void RemoveOptimizedTag(string isoPath)
    {
        using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
        stream.Write(new byte[Constants.OptimizedTagLength]);
    }

    [Fact]
    public async Task RepairFixesMissingOptimizedTagAndWritesBackup()
    {
        var isoPath = CreateIso();
        RemoveOptimizedTag(isoPath);
        Assert.False(XisoReader.AuditXiso(isoPath).IsOptimized);

        var result = await _service.RepairAsync(isoPath, dryRun: false);

        Assert.True(result.Success);
        Assert.NotEmpty(result.Fixed);
        Assert.True(File.Exists(result.BackupPath));
        Assert.True(XisoReader.AuditXiso(isoPath).IsOptimized);
    }

    [Fact]
    public async Task DryRunDoesNotChangeTheImage()
    {
        var isoPath = CreateIso();
        RemoveOptimizedTag(isoPath);
        var before = XisoImageHash.Compute(isoPath).Sha256;

        var result = await _service.RepairAsync(isoPath, dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal(before, XisoImageHash.Compute(isoPath).Sha256);
        Assert.False(XisoReader.AuditXiso(isoPath).IsOptimized);
    }

    [Fact]
    public async Task SalvageOfAValidImagePreservesTheTree()
    {
        var isoPath = CreateIso();
        var outputPath = Path.Combine(_tempRoot, "salvaged.iso");

        var result = await _service.SalvageAsync(isoPath, outputPath);

        Assert.True(result.Success);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(XisoChecksum.ComputeImageChecksumHex(isoPath),
            XisoChecksum.ComputeImageChecksumHex(result.OutputPath));
        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task SalvageOfAnInvalidImageFailsWithoutTouchingTheSource()
    {
        var badPath = Path.Combine(_tempRoot, "bad.iso");
        await File.WriteAllBytesAsync(badPath, new byte[4096]);
        var before = XisoImageHash.Compute(badPath).Sha256;

        await Assert.ThrowsAsync<XisoFormatException>(() =>
            _service.SalvageAsync(badPath, Path.Combine(_tempRoot, "bad.salvaged.iso")));

        Assert.Equal(before, XisoImageHash.Compute(badPath).Sha256);
    }
}
