using XISOStudio.Models;
using XISOStudio.Services;
using Serilog.Events;
using XISOSharp;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests structural and deep integrity validation of XISO, CSO, ZAR, and CHD images in <c>XisoIntegrityService</c>.</summary>
public sealed class XisoIntegrityServiceTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"XisoIntegrityServiceTests_{Guid.NewGuid():N}");

    private readonly TestLogger _logger = new();

    public XisoIntegrityServiceTests()
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

    private string CreateOptimizedXiso(string name = "game.iso")
    {
        var sourceDir = Path.Combine(_tempRoot, "source");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "default.xbe"), "fake xbe content");
        var mediaDir = Path.Combine(sourceDir, "media");
        Directory.CreateDirectory(mediaDir);
        File.WriteAllBytes(Path.Combine(mediaDir, "data.bin"), new byte[8192]);

        var isoPath = Path.Combine(_tempRoot, name);
        var result = XisoWriter.PackFromDirectory(sourceDir, isoPath);
        Assert.Equal(0, result);
        return isoPath;
    }

    private static void RemoveOptimizedTag(string isoPath)
    {
        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Write, FileShare.None);
        fs.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
        fs.Write(new byte[Constants.OptimizedTagLength]);
    }

    private static void CorruptRootDirectoryPointer(string isoPath)
    {
        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Write, FileShare.None);
        fs.Seek(Constants.HeaderOffset + 20, SeekOrigin.Begin);
        fs.Write(BitConverter.GetBytes(0xFFFFFFF0u));
    }

    private XisoIntegrityService CreateService()
    {
        return new XisoIntegrityService(_logger.Logger);
    }

    [Fact]
    public async Task ValidXisoPassesStructuralTest()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(isoPath, false, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.True(passed);
    }

    [Fact]
    public async Task ValidXisoPassesDeepScan()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(isoPath, true, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.True(passed);
    }

    [Fact]
    public async Task RawNonOptimizedIsoPassesStructuralTest()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(isoPath, false, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.True(passed);
        Assert.True(_logger.HasMessage("not optimized"));
    }

    [Fact]
    public async Task RawNonOptimizedIsoPassesDeepScan()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(isoPath, true, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.True(passed);
    }

    [Fact]
    public async Task RawNonOptimizedIsoWithCorruptTreeFails()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        CorruptRootDirectoryPointer(isoPath);
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(isoPath, false, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.False(passed);
    }

    private string CreateCiso(string name = "game.cso")
    {
        var isoPath = CreateOptimizedXiso($"{Path.GetFileNameWithoutExtension(name)}.iso");
        var csoPath = Path.Combine(_tempRoot, name);
        Assert.Equal(0, CisoWriter.CompressToCso(isoPath, csoPath));
        return csoPath;
    }

    private string CreateZar(string name = "game.zar")
    {
        var isoPath = CreateOptimizedXiso($"{Path.GetFileNameWithoutExtension(name)}.iso");
        var zarPath = Path.Combine(_tempRoot, name);
        Assert.True(XisoZarchive.CreateZar(isoPath, zarPath, quiet: true));
        return zarPath;
    }

    private string CreateChd(string name = "game.chd")
    {
        var isoPath = CreateOptimizedXiso($"{Path.GetFileNameWithoutExtension(name)}.iso");
        var chdPath = Path.Combine(_tempRoot, name);
        return ChdTestHelper.CreateDvdChd(isoPath, chdPath);
    }

    private string CreateNonDvdChd()
    {
        var rawPath = Path.Combine(_tempRoot, "raw.bin");
        File.WriteAllBytes(rawPath, new byte[64 * 1024]);
        var chdPath = Path.Combine(_tempRoot, "raw.chd");
        return ChdTestHelper.CreateRawChd(rawPath, chdPath);
    }

    [Fact]
    public async Task ValidCisoPassesStructuralTest()
    {
        var csoPath = CreateCiso();
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(csoPath, false, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.True(passed);
    }

    [Fact]
    public async Task ValidCisoPassesDeepScan()
    {
        var csoPath = CreateCiso();
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(csoPath, true, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.True(passed);
    }

    [Fact]
    public async Task ValidZarPassesStructuralTest()
    {
        var zarPath = CreateZar();
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(zarPath, false, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.True(passed);
        Assert.True(_logger.HasMessage("ZAR structure is valid"));
    }

    [Fact]
    public async Task ValidZarPassesDeepScan()
    {
        var zarPath = CreateZar();
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(zarPath, true, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.True(passed);
        Assert.True(_logger.HasMessage("ZAR deep scan completed successfully"));
    }

    [Fact]
    public async Task ValidChdPassesStructuralTest()
    {
        var chdPath = CreateChd();
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(chdPath, false, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.True(passed);
        Assert.True(_logger.HasMessage("CHD container is valid"));
        Assert.True(_logger.HasMessage("Structure is valid"));
    }

    [Fact]
    public async Task ValidChdPassesDeepScan()
    {
        var chdPath = CreateChd();
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(chdPath, true, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.True(passed);
        Assert.True(_logger.HasMessage("CHD container is valid"));
    }

    [Fact]
    public async Task NonDvdChdIsRejected()
    {
        var chdPath = CreateNonDvdChd();
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(chdPath, false, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.False(passed);
        Assert.True(_logger.HasMessage("not a supported Xbox DVD image"));
    }

    [Fact]
    public async Task CorruptChdFailsDeepScan()
    {
        var chdPath = CreateChd();

        // Flip a byte in the compressed data so a hunk fails its CRC during verification.
        var bytes = await File.ReadAllBytesAsync(chdPath);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(chdPath, bytes);

        var service = CreateService();
        var passed = await service.TestIsoIntegrityAsync(chdPath, true, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.False(passed);
    }

    [Fact]
    public async Task InvalidChdFailsWithoutBugReport()
    {
        var badChd = Path.Combine(_tempRoot, "bad.chd");
        await File.WriteAllTextAsync(badChd, "this is not a chd image");
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(badChd, false, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.False(passed);
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task InvalidZarFailsWithoutBugReport()
    {
        var badZar = Path.Combine(_tempRoot, "bad.zar");
        await File.WriteAllTextAsync(badZar, "this is not a zar archive");
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(badZar, false, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.False(passed);
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task InvalidImageFailsWithoutBugReport()
    {
        var badIso = Path.Combine(_tempRoot, "bad.iso");
        await File.WriteAllTextAsync(badIso, "this is not an xiso image");
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(badIso, false, new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.False(passed);
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task MissingFileFailsWithoutBugReport()
    {
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(Path.Combine(_tempRoot, "missing.iso"), false,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.False(passed);
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task InvalidImageReportsInvalidIsoCount()
    {
        var badIso = Path.Combine(_tempRoot, "bad.iso");
        await File.WriteAllTextAsync(badIso, "this is not an xiso image");
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(badIso, false, progress, CancellationToken.None);

        Assert.False(passed);
        Assert.Equal(1, progress.Reports.Where(p => p.InvalidIsoCount.HasValue).Sum(p => p.InvalidIsoCount!.Value));
    }

    [Fact]
    public async Task MissingFileDoesNotReportInvalidIsoCount()
    {
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(Path.Combine(_tempRoot, "missing.iso"), false, progress,
            CancellationToken.None);

        Assert.False(passed);
        Assert.DoesNotContain(progress.Reports, p => p.InvalidIsoCount > 0);
    }

    private static string TruncateFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 32);
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length / 2).ToArray());
        return path;
    }

    private string CreateEmptyFile(string name)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static int TotalInvalidIsoCount(CollectingProgress progress)
    {
        return progress.Reports.Where(p => p.InvalidIsoCount.HasValue).Sum(p => p.InvalidIsoCount!.Value);
    }

    [Fact]
    public async Task MalformedCisoFailsWithoutThrowing()
    {
        var badCso = Path.Combine(_tempRoot, "bad.cso");
        await File.WriteAllTextAsync(badCso, "this is not a ciso image");
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(badCso, false, new CollectingProgress(),
            CancellationToken.None);

        Assert.False(passed);
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task TruncatedIsoFailsStructuralTest()
    {
        var isoPath = TruncateFile(CreateOptimizedXiso());
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(isoPath, false, progress, CancellationToken.None);

        Assert.False(passed);
        Assert.Equal(1, TotalInvalidIsoCount(progress));
    }

    [Fact]
    public async Task TruncatedIsoFailsDeepScan()
    {
        var isoPath = TruncateFile(CreateOptimizedXiso());
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(isoPath, true, progress, CancellationToken.None);

        Assert.False(passed);
        Assert.Equal(1, TotalInvalidIsoCount(progress));
    }

    [Fact]
    public async Task TruncatedCisoFailsStructuralTest()
    {
        var csoPath = TruncateFile(CreateCiso());
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(csoPath, false, new CollectingProgress(),
            CancellationToken.None);

        Assert.False(passed);
    }

    [Fact]
    public async Task TruncatedCisoFailsDeepScan()
    {
        var csoPath = TruncateFile(CreateCiso());
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(csoPath, true, new CollectingProgress(),
            CancellationToken.None);

        Assert.False(passed);
    }

    [Fact]
    public async Task TruncatedZarFailsStructuralTest()
    {
        var zarPath = TruncateFile(CreateZar());
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(zarPath, false, new CollectingProgress(),
            CancellationToken.None);

        Assert.False(passed);
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task TruncatedZarFailsDeepScan()
    {
        var zarPath = TruncateFile(CreateZar());
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(zarPath, true, new CollectingProgress(),
            CancellationToken.None);

        Assert.False(passed);
    }

    [Fact]
    public async Task TruncatedChdFailsDeepScan()
    {
        var chdPath = TruncateFile(CreateChd());
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(chdPath, true, new CollectingProgress(),
            CancellationToken.None);

        Assert.False(passed);
    }

    [Fact]
    public async Task EmptyIsoFailsWithoutThrowing()
    {
        var isoPath = CreateEmptyFile("empty.iso");
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(isoPath, false, new CollectingProgress(),
            CancellationToken.None);

        Assert.False(passed);
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task EmptyCisoFailsWithoutThrowing()
    {
        var csoPath = CreateEmptyFile("empty.cso");
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(csoPath, false, new CollectingProgress(),
            CancellationToken.None);

        Assert.False(passed);
    }

    [Fact]
    public async Task EmptyZarFailsWithoutThrowing()
    {
        var zarPath = CreateEmptyFile("empty.zar");
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(zarPath, false, new CollectingProgress(),
            CancellationToken.None);

        Assert.False(passed);
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task EmptyChdFailsWithoutThrowing()
    {
        var chdPath = CreateEmptyFile("empty.chd");
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(chdPath, false, new CollectingProgress(),
            CancellationToken.None);

        Assert.False(passed);
    }

    [Fact]
    public async Task ShallowIsoScanDoesNotRunSurfaceScan()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(isoPath, false, progress, CancellationToken.None);

        Assert.True(passed);
        Assert.DoesNotContain(progress.Reports,
            static p => p.StatusText?.StartsWith("Surface scan:", StringComparison.Ordinal) == true);
        Assert.False(_logger.HasMessage("Surface scan completed successfully"));
    }

    [Fact]
    public async Task DeepIsoScanReportsSurfaceScanProgress()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(isoPath, true, progress, CancellationToken.None);

        Assert.True(passed);
        Assert.Contains(progress.Reports,
            static p => string.Equals(p.StatusText, "Surface scan: 100%", StringComparison.Ordinal));
        Assert.True(_logger.HasMessage("Surface scan completed successfully"));
    }

    [Fact]
    public async Task ShallowZarScanDoesNotRunDeepScan()
    {
        var zarPath = CreateZar();
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(zarPath, false, progress, CancellationToken.None);

        Assert.True(passed);
        Assert.DoesNotContain(progress.Reports,
            static p => p.StatusText?.StartsWith("Deep scan:", StringComparison.Ordinal) == true);
        Assert.False(_logger.HasMessage("ZAR deep scan completed successfully"));
    }

    [Fact]
    public async Task DeepZarScanReportsProgress()
    {
        var zarPath = CreateZar();
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(zarPath, true, progress, CancellationToken.None);

        Assert.True(passed);
        Assert.Contains(progress.Reports,
            static p => string.Equals(p.StatusText, "Deep scan: 100%", StringComparison.Ordinal));
        Assert.True(_logger.HasMessage("ZAR deep scan completed successfully"));
    }

    [Fact]
    public async Task DeepChdScanReportsVerificationProgress()
    {
        var chdPath = CreateChd();
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(chdPath, true, progress, CancellationToken.None);

        Assert.True(passed);
        Assert.True(SpinWait.SpinUntil(() => progress.Reports.Any(static p =>
            p.StatusText?.StartsWith("CHD verification:", StringComparison.Ordinal) == true), 10000));
    }

    [Fact]
    public async Task ValidIsoStructuralTestLogsReportDetails()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();

        var passed = await service.TestIsoIntegrityAsync(isoPath, false, new CollectingProgress(),
            CancellationToken.None);

        Assert.True(passed);
        Assert.True(_logger.HasMessage("Starting structural integrity test for: \"game.iso\""));
        Assert.True(_logger.HasMessage("Structure is valid"));
        Assert.True(_logger.HasMessage("not data checksums"));
    }

    [Fact]
    public async Task CorruptIsoLogsStructuralIssues()
    {
        var isoPath = CreateOptimizedXiso();
        CorruptRootDirectoryPointer(isoPath);
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(isoPath, false, progress, CancellationToken.None);

        Assert.False(passed);
        Assert.True(_logger.HasMessage("Structural validation failed"));
        Assert.True(_logger.HasMessage("exceeds file length"));
        Assert.Equal(1, TotalInvalidIsoCount(progress));
    }

    [Fact]
    public async Task InvalidZarReportsInvalidIsoCount()
    {
        var badZar = Path.Combine(_tempRoot, "bad.zar");
        await File.WriteAllTextAsync(badZar, "this is not a zar archive");
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(badZar, false, progress, CancellationToken.None);

        Assert.False(passed);
        Assert.Equal(1, TotalInvalidIsoCount(progress));
    }

    [Fact]
    public async Task InvalidChdReportsInvalidIsoCount()
    {
        var badChd = Path.Combine(_tempRoot, "bad.chd");
        await File.WriteAllTextAsync(badChd, "this is not a chd image");
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(badChd, false, progress, CancellationToken.None);

        Assert.False(passed);
        Assert.Equal(1, TotalInvalidIsoCount(progress));
    }

    [Fact]
    public async Task WrongMediaTypeChdReportsInvalidIsoCount()
    {
        var chdPath = CreateNonDvdChd();
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(chdPath, false, progress, CancellationToken.None);

        Assert.False(passed);
        Assert.Equal(1, TotalInvalidIsoCount(progress));
        Assert.True(_logger.HasMessage("not a supported Xbox DVD image"));
    }

    [Fact]
    public async Task ValidIsoDoesNotReportInvalidIsoCount()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(isoPath, false, progress, CancellationToken.None);

        Assert.True(passed);
        Assert.Equal(0, TotalInvalidIsoCount(progress));
    }

    [Fact]
    public async Task MissingZarFailsWithoutBugReport()
    {
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(Path.Combine(_tempRoot, "missing.zar"), false,
            progress, CancellationToken.None);

        Assert.False(passed);
        Assert.Equal(0, TotalInvalidIsoCount(progress));
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task MissingChdFailsWithoutThrowing()
    {
        var service = CreateService();
        var progress = new CollectingProgress();

        var passed = await service.TestIsoIntegrityAsync(Path.Combine(_tempRoot, "missing.chd"), false,
            progress, CancellationToken.None);

        Assert.False(passed);
        Assert.Equal(0, TotalInvalidIsoCount(progress));
    }
}