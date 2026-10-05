using System.Globalization;
using XISOStudio.Interfaces;
using XISOStudio.Models;
using XISOStudio.Services;
using Moq;
using Serilog.Events;
using XISOSharp;
using Xunit;
using ZArchiveSharp;

namespace XISOStudio.Tests.Services;

/// <summary>Tests image conversion to XISO, CSO, and ZAR formats, including optimization and space checks, in <c>XisoSharpService</c>.</summary>
public sealed class XisoSharpServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"XisoSharpServiceTests_{Guid.NewGuid():N}");
    private readonly TestLogger _logger = new();

    public XisoSharpServiceTests()
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
        File.WriteAllBytes(Path.Combine(mediaDir, "data.bin"), new byte[4096]);

        var isoPath = Path.Combine(_tempRoot, name);
        var result = XisoWriter.PackFromDirectory(sourceDir, isoPath);
        Assert.Equal(0, result);
        return isoPath;
    }

    private XisoSharpService CreateService()
    {
        var diskMonitor = new Mock<IDiskMonitorService>();
        diskMonitor.Setup(static d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(long.MaxValue);
        return new XisoSharpService(_logger.Logger, diskMonitor.Object);
    }

    [Fact]
    public async Task AlreadyOptimizedImageReturnsAlreadyOptimized()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "game.iso", OutputFormat.Xiso, false, false,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.AlreadyOptimized, status);
        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task AlreadyOptimizedImageKeepsExistingOutput()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");
        Directory.CreateDirectory(outputFolder);
        var existingOutput = Path.Combine(outputFolder, "game.iso");
        await File.WriteAllTextAsync(existingOutput, "existing output should survive");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "game.iso", OutputFormat.Xiso, false, false,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.AlreadyOptimized, status);
        Assert.True(File.Exists(existingOutput));
        Assert.Equal("existing output should survive", await File.ReadAllTextAsync(existingOutput));
    }

    [Fact]
    public async Task OutputOverwritingSourceReturnsFailedAndKeepsSource()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();

        var status = await service.ConvertIsoAsync(isoPath, _tempRoot, "game.iso", OutputFormat.Xiso, false, false,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task InvalidImageReturnsFailedWithoutBugReport()
    {
        var badIso = Path.Combine(_tempRoot, "bad.iso");
        File.WriteAllText(badIso, "this is not an xiso image");
        var service = CreateService();

        var status = await service.ConvertIsoAsync(badIso, Path.Combine(_tempRoot, "out"), "bad.iso", OutputFormat.Xiso,
            false,
            false,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.InvalidInput, status);
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task NonOptimizedImageIsConvertedToOutputFolder()
    {
        var isoPath = CreateOptimizedXiso();

        // Clear the optimized tag so the image is treated as a standard (non-optimized) XISO
        await using (var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            stream.Write(new byte[Constants.OptimizedTagLength]);
        }

        Assert.False(XisoReader.IsOptimizedImage(isoPath));

        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "game.iso", OutputFormat.Xiso, false, true,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        var outputPath = Path.Combine(outputFolder, "game.iso");
        Assert.True(File.Exists(outputPath));
        Assert.True(XisoReader.AuditXiso(outputPath).IsValid);
    }

    [Fact]
    public async Task ExplicitOutputNameIsUsedForTemporaryInput()
    {
        var isoPath = CreateOptimizedXiso("iso_000001.iso");

        // Clear the optimized tag so the image is treated as a standard (non-optimized) XISO
        await using (var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            stream.Write(new byte[Constants.OptimizedTagLength]);
        }

        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "My Game.iso", OutputFormat.Xiso, false, true,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.True(File.Exists(Path.Combine(outputFolder, "My Game.iso")));
        Assert.False(File.Exists(Path.Combine(outputFolder, "iso_000001.iso")));
    }

    [Fact]
    public async Task ExistingOutputIsReplacedWhenConversionProceeds()
    {
        var isoPath = CreateOptimizedXiso();

        // Clear the optimized tag so the image is treated as a standard (non-optimized) XISO
        await using (var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            stream.Write(new byte[Constants.OptimizedTagLength]);
        }

        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");
        Directory.CreateDirectory(outputFolder);
        var outputPath = Path.Combine(outputFolder, "game.iso");
        await File.WriteAllTextAsync(outputPath, "stale output");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "game.iso", OutputFormat.Xiso, false, true,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.True(XisoReader.AuditXiso(outputPath).IsValid);
    }

    [Fact]
    public async Task LockedExistingOutputReturnsFailedWithoutBugReport()
    {
        if (!OperatingSystem.IsWindows()) return;

        var isoPath = CreateOptimizedXiso();

        // Clear the optimized tag so the image is treated as a standard (non-optimized) XISO
        // and the conversion reaches the pre-existing-output deletion.
        await using (var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            stream.Write(new byte[Constants.OptimizedTagLength]);
        }

        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");
        Directory.CreateDirectory(outputFolder);
        var outputPath = Path.Combine(outputFolder, "game.iso");

        // Keep the stale output locked for the whole conversion attempt, as an emulator would.
        await using var lockedOutput = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "game.iso", OutputFormat.Xiso, false,
            false, new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(_logger.HasMessage(LogEventLevel.Information, "in use by another process"));
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task MissingInputReturnsFailed()
    {
        var service = CreateService();

        var status = await service.ConvertIsoAsync(Path.Combine(_tempRoot, "missing.iso"),
            Path.Combine(_tempRoot, "out"), "missing.iso", OutputFormat.Xiso, false, false,
            new Progress<BatchOperationProgress>(),
            CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
    }

    [Fact]
    public async Task ZarOutputPacksAlreadyOptimizedImage()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "game.zar", OutputFormat.Zar, false, false,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        var outputPath = Path.Combine(outputFolder, "game.zar");
        Assert.True(File.Exists(outputPath));

        // The archive must open and contain the game files.
        var extractDir = Path.Combine(_tempRoot, "zar-out");
        ZArchiveTool.Extract(outputPath, extractDir);
        Assert.True(File.Exists(Path.Combine(extractDir, "default.xbe")));
    }

    [Fact]
    public async Task ZarOutputWithIntegrityCheckAcceptsRawNonOptimizedImage()
    {
        var isoPath = CreateOptimizedXiso();

        // Clear the optimized tag so the image is treated as a standard (non-optimized) XISO
        await using (var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            stream.Write(new byte[Constants.OptimizedTagLength]);
        }

        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        // The source audit must not require the optimized tag: raw dumps are packed as-is.
        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "game.zar", OutputFormat.Zar, false, true,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.True(File.Exists(Path.Combine(outputFolder, "game.zar")));
    }

    [Fact]
    public async Task ZarOutputWithSkipSystemUpdateExcludesUpdateFolder()
    {
        var isoPath = CreateXisoWithSystemUpdate("game-su.iso");
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "game-su.zar", OutputFormat.Zar, true,
            false, new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        var extractDir = Path.Combine(_tempRoot, "zar-su-out");
        ZArchiveTool.Extract(Path.Combine(outputFolder, "game-su.zar"), extractDir);
        Assert.True(File.Exists(Path.Combine(extractDir, "default.xbe")));
        Assert.False(Directory.Exists(Path.Combine(extractDir, "$SystemUpdate")));
    }

    [Fact]
    public async Task CsoOutputCompressesAlreadyOptimizedImage()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "game.cso", OutputFormat.Cso, false, false,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        var outputPath = Path.Combine(outputFolder, "game.cso");
        Assert.True(File.Exists(outputPath));
        Assert.True(CisoReader.IsCso(outputPath));

        // Round-trip: the CISO must decompress back to a valid XISO.
        var decompressed = Path.Combine(_tempRoot, "decompressed.iso");
        Assert.Equal(0, CisoReader.DecompressToIso(outputPath, decompressed));
        Assert.True(XisoReader.AuditXiso(decompressed).IsValid);
    }

    [Fact]
    public async Task CsoOutputFromNonOptimizedImageRewritesBeforeCompressing()
    {
        var isoPath = CreateOptimizedXiso();

        // Clear the optimized tag so the CSO path exercises the temporary XISO rewrite.
        await using (var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            stream.Write(new byte[Constants.OptimizedTagLength]);
        }

        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "game.cso", OutputFormat.Cso, false, true,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        var outputPath = Path.Combine(outputFolder, "game.cso");
        Assert.True(File.Exists(outputPath));
        Assert.True(CisoReader.IsCso(outputPath));

        var decompressed = Path.Combine(_tempRoot, "decompressed2.iso");
        Assert.Equal(0, CisoReader.DecompressToIso(outputPath, decompressed));
        Assert.True(XisoReader.AuditXiso(decompressed).IsValid);
    }

    [Fact]
    public async Task CsoOutputWithSkipSystemUpdateStripsUpdateFolderFromOptimizedImage()
    {
        var isoPath = CreateXisoWithSystemUpdate("game-su-cso.iso");
        Assert.True(XisoReader.IsOptimizedImage(isoPath));
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "game-su.cso", OutputFormat.Cso, true,
            false, new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);

        using var explorer = ImageExplorerFactory.Open(Path.Combine(outputFolder, "game-su.cso"));
        var names = explorer.ListChildren("/").Select(static e => e.Name).ToList();
        Assert.Contains(names, name => name.Equals("default.xbe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("$SystemUpdate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task XisoOutputWithSkipSystemUpdateRewritesOptimizedImage()
    {
        var isoPath = CreateXisoWithSystemUpdate("game-su-xiso.iso");
        Assert.True(XisoReader.IsOptimizedImage(isoPath));
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "game-su.iso", OutputFormat.Xiso, true,
            false, new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);

        using var explorer = ImageExplorerFactory.Open(Path.Combine(outputFolder, "game-su.iso"));
        var names = explorer.ListChildren("/").Select(static e => e.Name).ToList();
        Assert.Contains(names, name => name.Equals("default.xbe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Equals("$SystemUpdate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CsoOutputSpaceCheckUsesCompressedEstimate()
    {
        var isoPath = CreateOptimizedXiso();
        var inputSize = new FileInfo(isoPath).Length;
        var halfSize = inputSize / 2;
        var csoRequired = halfSize + Math.Max(halfSize / 10, 200L * 1024 * 1024);
        var diskMonitor = new Mock<IDiskMonitorService>();
        diskMonitor.Setup(static d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(csoRequired + 1);
        var service = new XisoSharpService(_logger.Logger, diskMonitor.Object);

        var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), "space.cso",
            OutputFormat.Cso, false, false, new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.False(_logger.HasMessage("Not enough disk space"));
    }

    [Fact]
    public async Task XisoOutputSpaceCheckUsesRawSize()
    {
        var isoPath = CreateOptimizedXiso();

        // Clear the optimized tag so the raw-size pre-check runs instead of the skip path.
        await using (var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
            stream.Write(new byte[Constants.OptimizedTagLength]);
        }

        var inputSize = new FileInfo(isoPath).Length;
        var halfSize = inputSize / 2;
        var csoRequired = halfSize + Math.Max(halfSize / 10, 200L * 1024 * 1024);
        var diskMonitor = new Mock<IDiskMonitorService>();
        diskMonitor.Setup(static d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(csoRequired + 1);
        var service = new XisoSharpService(_logger.Logger, diskMonitor.Object);

        var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), "space.iso",
            OutputFormat.Xiso, false, false, new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(_logger.HasMessage("Not enough disk space"));
    }

    [Fact]
    public async Task InvalidImageZarOutputReturnsFailed()
    {
        var badIso = Path.Combine(_tempRoot, "bad-zar.iso");
        File.WriteAllText(badIso, "this is not an xiso image");
        var service = CreateService();

        var status = await service.ConvertIsoAsync(badIso, Path.Combine(_tempRoot, "out"), "bad-zar.zar",
            OutputFormat.Zar, false, false, new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.InvalidInput, status);
    }

    private static void RemoveOptimizedTag(string isoPath)
    {
        using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
        stream.Write(new byte[Constants.OptimizedTagLength]);
    }

    private static void CorruptRootDirectoryPointer(string isoPath)
    {
        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Write, FileShare.None);
        fs.Seek(Constants.HeaderOffset + 20, SeekOrigin.Begin);
        fs.Write(BitConverter.GetBytes(0xFFFFFFF0u));
    }

    private string CreateXisoWithSystemUpdate(string name, bool optimized = true)
    {
        var sourceDir = Path.Combine(_tempRoot, "source-su");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "default.xbe"), "fake xbe content");
        var updateDir = Path.Combine(sourceDir, "$SystemUpdate");
        Directory.CreateDirectory(updateDir);
        File.WriteAllText(Path.Combine(updateDir, "su.bin"), "system update payload");

        var isoPath = Path.Combine(_tempRoot, name);
        Assert.Equal(0, XisoWriter.PackFromDirectory(sourceDir, isoPath));
        if (!optimized) RemoveOptimizedTag(isoPath);
        return isoPath;
    }

    private static List<int> ParsePercents(IReadOnlyList<BatchOperationProgress> reports, string prefix)
    {
        var percents = new List<int>();
        foreach (var report in reports)
        {
            if (report.StatusText?.StartsWith(prefix, StringComparison.Ordinal) != true)
                continue;

            var token = report.StatusText[prefix.Length..].Trim().TrimEnd('%');
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                percents.Add(percent);
        }

        return percents;
    }

    private static bool WaitForStatus(CollectingProgress progress, Func<string?, bool> predicate,
        int timeoutMs = 10000)
    {
        return SpinWait.SpinUntil(() => progress.Reports.Any(report => predicate(report.StatusText)), timeoutMs);
    }

    [Fact]
    public async Task MissingInputZarReturnsFailed()
    {
        var service = CreateService();

        var status = await service.ConvertIsoAsync(Path.Combine(_tempRoot, "missing.iso"),
            Path.Combine(_tempRoot, "out"), "missing.zar", OutputFormat.Zar, false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(_logger.HasMessage("Input file not found"));
    }

    [Fact]
    public async Task MissingInputCsoReturnsFailed()
    {
        var service = CreateService();

        var status = await service.ConvertIsoAsync(Path.Combine(_tempRoot, "missing.iso"),
            Path.Combine(_tempRoot, "out"), "missing.cso", OutputFormat.Cso, false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
    }

    [Fact]
    public async Task NullOutputFolderReturnsFailed()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();

        var status = await service.ConvertIsoAsync(isoPath, null!, "game.iso", OutputFormat.Xiso, false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
    }

    [Fact]
    public async Task EmptyOutputFolderReturnsFailed()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var service = CreateService();

        var status = await service.ConvertIsoAsync(isoPath, "", "game.iso", OutputFormat.Xiso, false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
    }

    [Fact]
    public async Task NullOutputNameReturnsFailed()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var service = CreateService();

        var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), null!,
            OutputFormat.Xiso, false, false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
    }

    [Fact]
    public async Task OutputNameWithDirectorySegmentsIsSanitized()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "nested/game.iso", OutputFormat.Xiso,
            false, false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.True(File.Exists(Path.Combine(outputFolder, "game.iso")));
        Assert.False(Directory.Exists(Path.Combine(outputFolder, "nested")));
    }

    [Fact]
    public async Task UnsupportedExtensionWithGarbageReturnsInvalidInput()
    {
        var badFile = Path.Combine(_tempRoot, "not-an-image.zip");
        File.WriteAllText(badFile, "this is not an xiso image");
        var service = CreateService();

        var status = await service.ConvertIsoAsync(badFile, Path.Combine(_tempRoot, "out"), "bad.iso",
            OutputFormat.Xiso, false, false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.InvalidInput, status);
    }

    [Theory]
    [InlineData(OutputFormat.Zar)]
    [InlineData(OutputFormat.Cso)]
    public async Task OutputOverwritingSourceReturnsFailedForCompressedFormats(OutputFormat format)
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();

        var status = await service.ConvertIsoAsync(isoPath, _tempRoot, "game.iso", format, false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(File.Exists(isoPath));
    }

    [Theory]
    [InlineData(OutputFormat.Xiso)]
    [InlineData(OutputFormat.Zar)]
    [InlineData(OutputFormat.Cso)]
    public async Task CancellationBeforeStartThrowsForAllFormats(OutputFormat format)
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var service = CreateService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConvertIsoAsync(isoPath,
            Path.Combine(_tempRoot, "out"), "game.iso", format, false, false, new CollectingProgress(),
            new CancellationToken(true)));
    }

    [Fact]
    public async Task CancellationDoesNotOverrideAlreadyOptimizedResult()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();

        var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.iso",
            OutputFormat.Xiso, false, false, new CollectingProgress(), new CancellationToken(true));

        Assert.Equal(FileProcessingStatus.AlreadyOptimized, status);
    }

    [Fact]
    public async Task InsufficientSpaceForZarReturnsFailed()
    {
        var isoPath = CreateOptimizedXiso();
        var diskMonitor = new Mock<IDiskMonitorService>();
        diskMonitor.Setup(static d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(1);
        var service = new XisoSharpService(_logger.Logger, diskMonitor.Object);

        var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), "space.zar",
            OutputFormat.Zar, false, false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(_logger.HasMessage("Not enough disk space"));
    }

    [Fact]
    public async Task InsufficientSpaceForCsoReturnsFailed()
    {
        var isoPath = CreateOptimizedXiso();
        var diskMonitor = new Mock<IDiskMonitorService>();
        diskMonitor.Setup(static d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(1);
        var service = new XisoSharpService(_logger.Logger, diskMonitor.Object);

        var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), "space.cso",
            OutputFormat.Cso, false, false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(_logger.HasMessage("Not enough disk space"));
    }

    [Fact]
    public async Task ZeroAvailableSpaceIsTreatedAsUnknownAndConversionProceeds()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var diskMonitor = new Mock<IDiskMonitorService>();
        diskMonitor.Setup(static d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(0);
        var service = new XisoSharpService(_logger.Logger, diskMonitor.Object);

        var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.iso",
            OutputFormat.Xiso, false, false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
    }

    [Fact]
    public async Task CsoProgressReportsAreThrottledToFivePercentSteps()
    {
        var isoPath = CreateOptimizedXiso();
        var progress = new CollectingProgress();
        var service = CreateService();

        var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.cso",
            OutputFormat.Cso, false, false, progress, CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.True(WaitForStatus(progress,
            static text => string.Equals(text, "Finalizing output...", StringComparison.Ordinal)));
        Assert.Contains(progress.Reports,
            static p => p.StatusText?.StartsWith("Compressing ", StringComparison.Ordinal) == true &&
                        p.StatusText.EndsWith("sectors...", StringComparison.Ordinal));

        var percents = ParsePercents(progress.Reports, "Compressing: ");
        Assert.NotEmpty(percents);
        Assert.Equal(percents.Order().ToList(), percents);
        Assert.True(percents[^1] >= 90, $"Expected near-complete progress, got {percents[^1]}%");
        Assert.True(percents.Count <= 25, $"Expected throttled progress, got {percents.Count} reports");
    }

    [Fact]
    public async Task XisoRewriteProgressReportsFileCountAndFinalization()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var progress = new CollectingProgress();
        var service = CreateService();

        var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.iso",
            OutputFormat.Xiso, false, false, progress, CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.True(WaitForStatus(progress,
            static text => string.Equals(text, "Finalizing output...", StringComparison.Ordinal)));
        Assert.Contains(progress.Reports,
            static p => p.StatusText?.StartsWith("Packing", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task InvalidImageCsoOutputReturnsInvalidInput()
    {
        var badIso = Path.Combine(_tempRoot, "bad-cso.iso");
        File.WriteAllText(badIso, "this is not an xiso image");
        var service = CreateService();

        var status = await service.ConvertIsoAsync(badIso, Path.Combine(_tempRoot, "out"), "bad-cso.cso",
            OutputFormat.Cso, false, false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.InvalidInput, status);
    }

    [Theory]
    [InlineData(OutputFormat.Zar)]
    [InlineData(OutputFormat.Cso)]
    public async Task CorruptSourceWithIntegrityCheckReturnsFailedForCompressedFormats(OutputFormat format)
    {
        var isoPath = CreateOptimizedXiso();
        CorruptRootDirectoryPointer(isoPath);
        var service = CreateService();
        var extension = format == OutputFormat.Zar ? "zar" : "cso";

        var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), $"corrupt.{extension}",
            format, false, true, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(_logger.HasMessage("Source image failed structural validation"));
    }

    [Fact]
    public async Task AuditSourceImageLogsRawImageNotice()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var service = CreateService();

        var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.zar",
            OutputFormat.Zar, false, true, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.True(_logger.HasMessage("not optimized (raw ISO)"));
    }

    [Fact]
    public async Task XisoOutputWithSkipSystemUpdateOnNonOptimizedImageStripsUpdateFolder()
    {
        var isoPath = CreateXisoWithSystemUpdate("raw-su.iso", optimized: false);
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "raw-su.iso", OutputFormat.Xiso, true,
            false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        using var explorer = ImageExplorerFactory.Open(Path.Combine(outputFolder, "raw-su.iso"));
        var names = explorer.ListChildren("/").Select(static e => e.Name).ToList();
        Assert.Contains(names, static n => n.Equals("default.xbe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, static n => n.Equals("$SystemUpdate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CsoOutputWithSkipSystemUpdateOnNonOptimizedImageStripsUpdateFolder()
    {
        var isoPath = CreateXisoWithSystemUpdate("raw-su-cso.iso", optimized: false);
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "raw-su.cso", OutputFormat.Cso, true,
            false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        using var explorer = ImageExplorerFactory.Open(Path.Combine(outputFolder, "raw-su.cso"));
        var names = explorer.ListChildren("/").Select(static e => e.Name).ToList();
        Assert.Contains(names, static n => n.Equals("default.xbe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, static n => n.Equals("$SystemUpdate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ZarOutputWithSkipSystemUpdateOnNonOptimizedImageStripsUpdateFolder()
    {
        var isoPath = CreateXisoWithSystemUpdate("raw-su-zar.iso", optimized: false);
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoAsync(isoPath, outputFolder, "raw-su.zar", OutputFormat.Zar, true,
            false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        var extractDir = Path.Combine(_tempRoot, "raw-su-zar-out");
        ZArchiveTool.Extract(Path.Combine(outputFolder, "raw-su.zar"), extractDir);
        Assert.True(File.Exists(Path.Combine(extractDir, "default.xbe")));
        Assert.False(Directory.Exists(Path.Combine(extractDir, "$SystemUpdate")));
    }

    [Fact]
    public async Task XisoSkipSystemUpdateWithIntegrityCheckAuditsOutput()
    {
        var isoPath = CreateXisoWithSystemUpdate("su-opt.iso");
        var service = CreateService();

        var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), "su-opt.iso",
            OutputFormat.Xiso, true, true, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.True(_logger.HasMessage("Output XISO passed validation"));
    }

    [Fact]
    public async Task LockedInputReturnsFailedInsteadOfInvalidInput()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();

        await using (new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.iso",
                OutputFormat.Xiso, false, false, new CollectingProgress(), CancellationToken.None);

            Assert.Equal(FileProcessingStatus.Failed, status);
        }
    }

    [Fact]
    public async Task ChdFormatIsRejectedInsteadOfProducingAnXiso()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ConvertIsoAsync(isoPath, outputFolder,
            "game.chd", OutputFormat.Chd, false, false, new CollectingProgress(), CancellationToken.None));

        // CHD encoding is owned by IChdService: no mislabeled XISO may be written.
        Assert.False(File.Exists(Path.Combine(outputFolder, "game.chd")));
    }

    [Fact]
    public async Task ConversionDoesNotForwardControlCharactersOrEmptyLibraryLines()
    {
        // The rewrite/repack path is where XISOSharp emits its per-character backspace
        // progress animation; every forwarded line must be a complete, sanitized line.
        var isoPath = CreateXisoWithSystemUpdate("control-chars.iso", optimized: true);
        var service = CreateService();

        var status = await service.ConvertIsoAsync(isoPath, Path.Combine(_tempRoot, "out"), "control-chars.iso",
            OutputFormat.Xiso, true, false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.DoesNotContain(_logger.Events, e =>
        {
            var message = e.RenderMessage(CultureInfo.InvariantCulture);
            return message.Contains('\b') || message.Contains('\r') || message.Trim() is "  [xiso]";
        });
    }
}