using System.Globalization;
using CHDSharp;
using CHDSharp.Models;
using XISOStudio.Interfaces;
using XISOStudio.Models;
using XISOStudio.Services;
using Moq;
using Serilog.Events;
using XISOSharp;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests ISO to CHD conversion, including optimization handling and output validation, in <c>ChdService</c>.</summary>
public sealed class ChdServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"ChdServiceTests_{Guid.NewGuid():N}");
    private readonly TestLogger _logger = new();

    public ChdServiceTests()
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
        Assert.Equal(0, XisoWriter.PackFromDirectory(sourceDir, isoPath));
        return isoPath;
    }

    private static void RemoveOptimizedTag(string isoPath)
    {
        using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Seek(Constants.OptimizedTagOffset, SeekOrigin.Begin);
        stream.Write(new byte[Constants.OptimizedTagLength]);
    }

    private ChdService CreateService()
    {
        var diskMonitor = new Mock<IDiskMonitorService>();
        diskMonitor.Setup(static d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(long.MaxValue);
        var xisoSharpService = new XisoSharpService(_logger.Logger, diskMonitor.Object);
        return new ChdService(_logger.Logger, xisoSharpService);
    }

    private static void AssertValidDvdChd(string chdPath)
    {
        Assert.True(File.Exists(chdPath));

        Assert.Equal(ChdError.Chderrnone, Chd.Classify(chdPath, out var classification));
        Assert.Equal("dvd", classification);

        using var stream = File.OpenRead(chdPath);
        var result = Chd.CheckFile(stream, Path.GetFileName(chdPath), deepCheck: true);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ConvertOptimizedIsoProducesValidDvdChd()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoToChdAsync(isoPath, outputFolder, "game.chd", false, true,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        AssertValidDvdChd(Path.Combine(outputFolder, "game.chd"));
    }

    [Fact]
    public async Task ConvertLogsEncodingAndVerificationProgress()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoToChdAsync(isoPath, outputFolder, "game.chd", false, true,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.True(_logger.HasMessage("Encoding '"));
        Assert.True(_logger.HasMessage("Compressing to CHD:"));
        Assert.True(_logger.HasMessage("CHD encoding completed"));
        Assert.True(_logger.HasMessage("Verifying CHD output"));
        Assert.True(_logger.HasMessage("Successfully converted"));
    }

    [Fact]
    public async Task ConvertNonOptimizedIsoRewritesToGamePartitionBeforeEncoding()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        Assert.False(XisoReader.IsOptimizedImage(isoPath));

        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoToChdAsync(isoPath, outputFolder, "game.chd", false, false,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        AssertValidDvdChd(Path.Combine(outputFolder, "game.chd"));
    }

    [Fact]
    public async Task ConvertUsesSuppliedOutputNameVerbatim()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoToChdAsync(isoPath, outputFolder, "custom-name.chd", false, false,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        AssertValidDvdChd(Path.Combine(outputFolder, "custom-name.chd"));
    }

    [Fact]
    public async Task OutputOverwritingSourceReturnsFailedAndKeepsSource()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();

        var status = await service.ConvertIsoToChdAsync(isoPath, _tempRoot, "game.iso", false, false,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task MissingInputReturnsFailed()
    {
        var service = CreateService();

        var status = await service.ConvertIsoToChdAsync(Path.Combine(_tempRoot, "missing.iso"),
            Path.Combine(_tempRoot, "out"), "missing.chd", false, false,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
    }

    [Fact]
    public async Task SkipSystemUpdateRewritesOptimizedImageBeforeEncoding()
    {
        var isoPath = CreateOptimizedXiso();
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FileProcessingStatus.Failed);
        var service = new ChdService(_logger.Logger, xisoSharp.Object);

        var status = await service.ConvertIsoToChdAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.chd", true,
            false, new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        xisoSharp.Verify(s => s.ConvertIsoAsync(isoPath, It.IsAny<string>(), "source.iso", OutputFormat.Xiso, true,
            false, It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OptimizedImageWithoutSkipSystemUpdateIsPackedDirectly()
    {
        var isoPath = CreateOptimizedXiso();
        var xisoSharp = new Mock<IXisoSharpService>();
        var service = new ChdService(_logger.Logger, xisoSharp.Object);
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoToChdAsync(isoPath, outputFolder, "game.chd", false, false,
            new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        xisoSharp.Verify(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvalidImageReturnsFailedWithoutBugReport()
    {
        var badIso = Path.Combine(_tempRoot, "bad.iso");
        File.WriteAllText(badIso, "this is not an xiso image");
        var service = CreateService();

        var status = await service.ConvertIsoToChdAsync(badIso, Path.Combine(_tempRoot, "out"), "bad.chd", false,
            false, new Progress<BatchOperationProgress>(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.InvalidInput, status);
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    private string CreateXisoWithSystemUpdate(string name = "game-su.iso")
    {
        var sourceDir = Path.Combine(_tempRoot, "source-su");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "default.xbe"), "fake xbe content");
        var updateDir = Path.Combine(sourceDir, "$SystemUpdate");
        Directory.CreateDirectory(updateDir);
        File.WriteAllText(Path.Combine(updateDir, "su.bin"), "system update payload");

        var isoPath = Path.Combine(_tempRoot, name);
        Assert.Equal(0, XisoWriter.PackFromDirectory(sourceDir, isoPath));
        return isoPath;
    }

    private static string ChdTempRoot => Path.Combine(Path.GetTempPath(), "XISOStudio_Chd");

    private static HashSet<string> SnapshotChdTempFolders()
    {
        return Directory.Exists(ChdTempRoot)
            ? new HashSet<string>(Directory.GetDirectories(ChdTempRoot), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

    [Fact]
    public async Task MissingInputLogsNotFound()
    {
        var service = CreateService();

        var status = await service.ConvertIsoToChdAsync(Path.Combine(_tempRoot, "missing.iso"),
            Path.Combine(_tempRoot, "out"), "missing.chd", false, false, new CollectingProgress(),
            CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(_logger.HasMessage("Input file not found"));
    }

    [Fact]
    public async Task NullOutputFolderReturnsFailed()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();

        var status = await service.ConvertIsoToChdAsync(isoPath, null!, "game.chd", false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
    }

    [Fact]
    public async Task EmptyOutputFolderReturnsFailed()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();

        var status = await service.ConvertIsoToChdAsync(isoPath, "", "game.chd", false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
    }

    [Fact]
    public async Task NullOutputNameReturnsFailed()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();

        var status = await service.ConvertIsoToChdAsync(isoPath, Path.Combine(_tempRoot, "out"), null!, false,
            false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
    }

    [Fact]
    public async Task EmptyOutputNameReturnsFailed()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoToChdAsync(isoPath, outputFolder, "", false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(Directory.Exists(outputFolder));
        Assert.Empty(Directory.GetFileSystemEntries(outputFolder));
    }

    [Fact]
    public async Task UnsupportedExtensionWithGarbageReturnsInvalidInput()
    {
        var badFile = Path.Combine(_tempRoot, "not-an-image.zip");
        File.WriteAllText(badFile, "this is not an xiso image");
        var service = CreateService();

        var status = await service.ConvertIsoToChdAsync(badFile, Path.Combine(_tempRoot, "out"), "bad.chd",
            false, false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.InvalidInput, status);
    }

    [Fact]
    public async Task OutputNameWithDirectorySegmentsIsSanitized()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoToChdAsync(isoPath, outputFolder, "nested/game.chd", false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        AssertValidDvdChd(Path.Combine(outputFolder, "game.chd"));
        Assert.False(Directory.Exists(Path.Combine(outputFolder, "nested")));
    }

    [Fact]
    public async Task ExistingOutputFileIsReplaced()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");
        Directory.CreateDirectory(outputFolder);
        var outputPath = Path.Combine(outputFolder, "game.chd");
        await File.WriteAllTextAsync(outputPath, "stale output");

        var status = await service.ConvertIsoToChdAsync(isoPath, outputFolder, "game.chd", false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        AssertValidDvdChd(outputPath);
    }

    [Fact]
    public async Task OutputFolderIsCreatedWhenMissing()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "nested", "deep", "out");

        var status = await service.ConvertIsoToChdAsync(isoPath, outputFolder, "game.chd", false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        AssertValidDvdChd(Path.Combine(outputFolder, "game.chd"));
    }

    [Fact]
    public async Task CancellationBeforeStartThrowsAndLeavesNoOutput()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConvertIsoToChdAsync(isoPath,
            outputFolder, "game.chd", false, false, new CollectingProgress(), new CancellationToken(true)));

        Assert.True(Directory.Exists(outputFolder));
        Assert.Empty(Directory.GetFileSystemEntries(outputFolder));
    }

    [Fact]
    public async Task CancellationBeforeStartCleansTemporaryWorkingFolder()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var before = SnapshotChdTempFolders();
        var service = CreateService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConvertIsoToChdAsync(isoPath,
            Path.Combine(_tempRoot, "out"), "game.chd", false, false, new CollectingProgress(),
            new CancellationToken(true)));

        Assert.True(before.SetEquals(SnapshotChdTempFolders()));
    }

    [Fact]
    public async Task ProgressPercentagesAreMonotonicAndComplete()
    {
        var isoPath = CreateOptimizedXiso();
        var progress = new CollectingProgress();
        var xisoSharp = new Mock<IXisoSharpService>();
        var service = new ChdService(_logger.Logger, xisoSharp.Object);

        var status = await service.ConvertIsoToChdAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.chd",
            false, false, progress, CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.Contains(progress.Reports,
            static p => string.Equals(p.StatusText, "Compressing to CHD...", StringComparison.Ordinal));
        Assert.Contains(progress.Reports,
            static p => string.Equals(p.StatusText, "Checking CHD output...", StringComparison.Ordinal));

        var percents = ParsePercents(progress.Reports, "Compressing to CHD: ");
        Assert.NotEmpty(percents);
        Assert.Equal(percents.Order().ToList(), percents);
        Assert.Equal(100, percents[^1]);
        Assert.True(percents.Count <= 25, $"Expected throttled progress, got {percents.Count} reports");
    }

    [Fact]
    public async Task ProgressIncludesPreparationForNonOptimizedImage()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var progress = new CollectingProgress();
        var service = CreateService();

        var status = await service.ConvertIsoToChdAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.chd",
            false, false, progress, CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.Contains(progress.Reports,
            static p => string.Equals(p.StatusText, "Preparing XISO for CHD compression...", StringComparison.Ordinal));
        Assert.Contains(progress.Reports,
            static p => string.Equals(p.StatusText, "Compressing to CHD...", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IntegrityCheckTrueVerifiesOutput()
    {
        var isoPath = CreateOptimizedXiso();
        var progress = new CollectingProgress();
        var xisoSharp = new Mock<IXisoSharpService>();
        var service = new ChdService(_logger.Logger, xisoSharp.Object);

        var status = await service.ConvertIsoToChdAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.chd",
            false, true, progress, CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.Contains(progress.Reports,
            static p => string.Equals(p.StatusText, "Verifying CHD output...", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IntegrityCheckFalseOnlyChecksHeader()
    {
        var isoPath = CreateOptimizedXiso();
        var progress = new CollectingProgress();
        var xisoSharp = new Mock<IXisoSharpService>();
        var service = new ChdService(_logger.Logger, xisoSharp.Object);

        var status = await service.ConvertIsoToChdAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.chd",
            false, false, progress, CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.Contains(progress.Reports,
            static p => string.Equals(p.StatusText, "Checking CHD output...", StringComparison.Ordinal));
        Assert.DoesNotContain(progress.Reports,
            static p => p.StatusText?.StartsWith("Verifying CHD", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task SkipSystemUpdateStripsUpdateFolderFromChd()
    {
        var isoPath = CreateXisoWithSystemUpdate();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoToChdAsync(isoPath, outputFolder, "game-su.chd", true, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        var outputPath = Path.Combine(outputFolder, "game-su.chd");
        AssertValidDvdChd(outputPath);

        using var explorer = ImageExplorerFactory.Open(outputPath);
        var names = explorer.ListChildren("/").Select(static e => e.Name).ToList();
        Assert.Contains(names, static n => n.Equals("default.xbe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, static n => n.Equals("$SystemUpdate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NonOptimizedImageIsRewrittenThroughXisoServiceBeforeEncoding()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var before = SnapshotChdTempFolders();
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string input, string outputFolder, string _, OutputFormat _, bool _, bool _,
                IProgress<BatchOperationProgress>? _, CancellationToken _) =>
            {
                Directory.CreateDirectory(outputFolder);
                File.Copy(input, Path.Combine(outputFolder, "source.iso"), overwrite: true);
                return FileProcessingStatus.Converted;
            });
        var service = new ChdService(_logger.Logger, xisoSharp.Object);

        var status = await service.ConvertIsoToChdAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.chd",
            false, false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        xisoSharp.Verify(s => s.ConvertIsoAsync(isoPath,
            It.Is<string>(static folder => folder.Contains("XISOStudio_Chd", StringComparison.OrdinalIgnoreCase)),
            "source.iso", OutputFormat.Xiso, false, false,
            It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(before.SetEquals(SnapshotChdTempFolders()));
    }

    [Fact]
    public async Task RewriteFailureReturnsFailedAndLogs()
    {
        var isoPath = CreateOptimizedXiso();
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FileProcessingStatus.Failed);
        var service = new ChdService(_logger.Logger, xisoSharp.Object);
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoToChdAsync(isoPath, outputFolder, "game.chd", true, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(_logger.HasMessage("Could not prepare"));
        Assert.Empty(Directory.GetFileSystemEntries(outputFolder));
    }

    [Fact]
    public async Task RewriteInvalidInputReturnsInvalidInput()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FileProcessingStatus.InvalidInput);
        var service = new ChdService(_logger.Logger, xisoSharp.Object);

        var status = await service.ConvertIsoToChdAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.chd",
            false, false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.InvalidInput, status);
    }

    [Fact]
    public async Task FailedConversionLeavesNoFilesInOutputFolder()
    {
        var badIso = Path.Combine(_tempRoot, "bad.iso");
        File.WriteAllText(badIso, "this is not an xiso image");
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");

        var status = await service.ConvertIsoToChdAsync(badIso, outputFolder, "bad.chd", false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.InvalidInput, status);
        Assert.True(Directory.Exists(outputFolder));
        Assert.Empty(Directory.GetFileSystemEntries(outputFolder));
    }

    [Fact]
    public async Task OptimizedPackingLogsDirectPackingAndSuccess()
    {
        var isoPath = CreateOptimizedXiso();
        var service = CreateService();

        var status = await service.ConvertIsoToChdAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.chd",
            false, false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.True(_logger.HasMessage(LogEventLevel.Information, "already an optimized XISO"));
        Assert.True(_logger.HasMessage(LogEventLevel.Information, "Successfully converted"));
    }

    [Fact]
    public async Task TemporaryWorkingFoldersAreRemovedAfterSuccess()
    {
        var isoPath = CreateOptimizedXiso();
        RemoveOptimizedTag(isoPath);
        var before = SnapshotChdTempFolders();
        var service = CreateService();

        var status = await service.ConvertIsoToChdAsync(isoPath, Path.Combine(_tempRoot, "out"), "game.chd",
            false, false, new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Converted, status);
        Assert.True(before.SetEquals(SnapshotChdTempFolders()));
    }

    [Fact]
    public async Task OutputOverwriteCheckIsCaseInsensitiveOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var isoPath = CreateOptimizedXiso("game.iso");
        var service = CreateService();

        var status = await service.ConvertIsoToChdAsync(isoPath, _tempRoot, "GAME.ISO", false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task LockedExistingOutputReturnsFailedWithoutBugReport()
    {
        if (!OperatingSystem.IsWindows()) return;

        var isoPath = CreateOptimizedXiso();
        var service = CreateService();
        var outputFolder = Path.Combine(_tempRoot, "out");
        Directory.CreateDirectory(outputFolder);
        var outputPath = Path.Combine(outputFolder, "game.chd");

        // Keep the stale output locked for the whole conversion attempt, as an emulator would.
        await using var lockedOutput = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var status = await service.ConvertIsoToChdAsync(isoPath, outputFolder, "game.chd", false, false,
            new CollectingProgress(), CancellationToken.None);

        Assert.Equal(FileProcessingStatus.Failed, status);
        Assert.True(_logger.HasMessage(LogEventLevel.Information, "in use by another process"));
        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }
}