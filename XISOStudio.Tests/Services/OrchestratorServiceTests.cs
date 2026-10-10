using XISOStudio.Interfaces;
using XISOStudio.Models;
using XISOStudio.Services;
using Moq;
using Serilog.Events;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests batch conversion and integrity-test orchestration, including archive cleanup and output naming, in <c>OrchestratorService</c>.</summary>
public class OrchestratorServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"OrchestratorTests_{Guid.NewGuid():N}");
    private readonly List<string> _tempFiles = [];
    private static readonly int[] Expected = new[] { 1, 2 };

    public OrchestratorServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch
            {
                // ignored
            }
        }

        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch
        {
            // ignored
        }

        GC.SuppressFinalize(this);
    }

    private string CreateTempFile(string name, string content = "")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    #region IsFatalEnvironmentalError Tests

    [Fact]
    public void IsFatalEnvironmentalErrorDirectoryNotFoundExceptionReturnsTrue()
    {
        var ex = new DirectoryNotFoundException("Path not found");
        Assert.True(OrchestratorService.IsFatalEnvironmentalError(ex));
    }

    [Theory]
    [InlineData(0x15, "The device is not ready")] // ERROR_NOT_READY
    [InlineData(0x03, "The system cannot find the path")] // ERROR_PATH_NOT_FOUND
    [InlineData(0x0F, "The system cannot find the drive")] // ERROR_INVALID_DRIVE
    [InlineData(0x37, "The device does not exist")] // ERROR_DEV_NOT_EXIST
    [InlineData(0x40, "The network name is no longer available")] // ERROR_NETNAME_DELETED
    [InlineData(0x45D, "The request could not be performed because of an I/O device error")] // ERROR_IO_DEVICE
    public void IsFatalEnvironmentalErrorIoExceptionWithFatalHResultReturnsTrue(int hresult, string message)
    {
        var ex = new IOException(message, hresult);
        Assert.True(OrchestratorService.IsFatalEnvironmentalError(ex));
    }

    [Theory]
    [InlineData(0x02, "File not found")] // ERROR_FILE_NOT_FOUND - not fatal
    [InlineData(0x05, "Access denied")] // ERROR_ACCESS_DENIED - not fatal
    [InlineData(0x20, "Sharing violation")] // ERROR_SHARING_VIOLATION - not fatal
    public void IsFatalEnvironmentalErrorIoExceptionWithNonFatalHResultReturnsFalse(int hresult, string message)
    {
        var ex = new IOException(message, hresult);
        Assert.False(OrchestratorService.IsFatalEnvironmentalError(ex));
    }

    [Fact]
    public void IsFatalEnvironmentalErrorIoExceptionWithDeviceMessageReturnsTrue()
    {
        var ex = new IOException("The device is not ready.");
        Assert.True(OrchestratorService.IsFatalEnvironmentalError(ex));
    }

    [Fact]
    public void IsFatalEnvironmentalErrorIoExceptionWithNetworkNameMessageReturnsTrue()
    {
        var ex = new IOException("The network name is no longer available.");
        Assert.True(OrchestratorService.IsFatalEnvironmentalError(ex));
    }

    [Fact]
    public void IsFatalEnvironmentalErrorIoExceptionWithCzechDeviceMessageReturnsTrue()
    {
        var ex = new IOException("Zařízení není připraveno");
        Assert.True(OrchestratorService.IsFatalEnvironmentalError(ex));
    }

    [Fact]
    public void IsFatalEnvironmentalErrorIoExceptionWithLocalizedDeviceIoMessageReturnsTrue()
    {
        var ex = new IOException("Impossibile eseguire la richiesta a causa di un errore di dispositivo I/O.");
        Assert.True(OrchestratorService.IsFatalEnvironmentalError(ex));
    }

    [Fact]
    public void IsFatalEnvironmentalErrorIoExceptionWithGenericMessageReturnsFalse()
    {
        var ex = new IOException("Something went wrong");
        Assert.False(OrchestratorService.IsFatalEnvironmentalError(ex));
    }

    [Fact]
    public void IsFatalEnvironmentalErrorArgumentExceptionReturnsFalse()
    {
        var ex = new ArgumentException("Bad argument");
        Assert.False(OrchestratorService.IsFatalEnvironmentalError(ex));
    }

    [Fact]
    public void IsFatalEnvironmentalErrorInvalidOperationExceptionReturnsFalse()
    {
        var ex = new InvalidOperationException("Invalid operation");
        Assert.False(OrchestratorService.IsFatalEnvironmentalError(ex));
    }

    [Fact]
    public void IsFatalEnvironmentalErrorFileNotFoundExceptionReturnsFalse()
    {
        var ex = new FileNotFoundException("File not found");
        Assert.False(OrchestratorService.IsFatalEnvironmentalError(ex));
    }

    [Fact]
    public void IsFatalEnvironmentalErrorIoExceptionWithCaseInsensitiveDeviceMessageReturnsTrue()
    {
        var ex = new IOException("The DEVICE is not available");
        Assert.True(OrchestratorService.IsFatalEnvironmentalError(ex));
    }

    #endregion

    #region Delete Originals Tests

    private static OrchestratorService CreateOrchestrator(
        Mock<IFileExtractor> extractor,
        FileProcessingStatus conversionStatus,
        bool integrityResult = false,
        Mock<IXisoSharpService>? xisoSharp = null,
        Mock<IChdService>? chdService = null,
        Mock<IXisoIntegrityService>? integrity = null,
        Mock<IFileMover>? fileMover = null,
        Mock<IDiskMonitorService>? diskMonitor = null,
        TestLogger? logger = null)
    {
        logger ??= new TestLogger();
        diskMonitor ??= new Mock<IDiskMonitorService>();
        diskMonitor.Setup(static d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(long.MaxValue);
        if (fileMover == null)
        {
            fileMover = new Mock<IFileMover>();
            fileMover.Setup(static m => m.MoveTestedFileAsync(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string source, string destinationFolder, string _, CancellationToken _) =>
                {
                    Directory.CreateDirectory(destinationFolder);
                    File.Move(source, Path.Combine(destinationFolder, Path.GetFileName(source)), true);
                    return Task.CompletedTask;
                });
        }

        if (integrity == null)
        {
            integrity = new Mock<IXisoIntegrityService>();
            integrity.Setup(static i => i.TestIsoIntegrityAsync(It.IsAny<string>(), It.IsAny<bool>(),
                    It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(integrityResult);
        }

        if (xisoSharp == null)
        {
            xisoSharp = new Mock<IXisoSharpService>();
            xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                    It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(conversionStatus);
        }

        if (chdService == null)
        {
            chdService = new Mock<IChdService>();
            chdService.Setup(static s => s.ConvertIsoToChdAsync(It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                    It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(conversionStatus);
        }

        return new OrchestratorService(extractor.Object, fileMover.Object, logger.Logger,
            integrity.Object, xisoSharp.Object, chdService.Object, diskMonitor.Object);
    }

    private static Task<CloudRetryResult> CloudRetrySkip(string fileName)
    {
        return Task.FromResult(CloudRetryResult.Skip);
    }

    private static Mock<IFileExtractor> CreateExtractor((long TotalSize, int FileCount) info,
        Action<string> writeExtractedFiles, ArchiveExtractionResult result)
    {
        var extractor = new Mock<IFileExtractor>();
        extractor.Setup(static e => e.GetArchiveInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(info);
        extractor.Setup(static e => e.ExtractArchiveAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string extractionPath, CancellationToken _) =>
            {
                writeExtractedFiles(extractionPath);
                return Task.FromResult(result);
            });
        return extractor;
    }

    private static Mock<IFileMover> CreateRecordingMover(
        List<(string Source, string Folder, string Reason)> moves, bool deleteSource)
    {
        var fileMover = new Mock<IFileMover>();
        fileMover.Setup(static m => m.MoveTestedFileAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string source, string folder, string reason, CancellationToken _) =>
            {
                moves.Add((source, folder, reason));
                if (deleteSource) File.Delete(source);
                return Task.CompletedTask;
            });
        return fileMover;
    }

    private Task RunConvertAsync(OrchestratorService orchestrator, bool deleteOriginals)
    {
        return orchestrator.ConvertAsync(_tempDir, Path.Combine(_tempDir, "out"), deleteOriginals, false, false,
            OutputFormat.Xiso, false, new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);
    }

    [Fact]
    public async Task ConvertAsyncDeleteOriginalsRemovesOriginalAfterSuccessfulConversion()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(),
            FileProcessingStatus.Converted);

        await RunConvertAsync(orchestrator, true);

        Assert.False(File.Exists(isoPath));
    }

    [Fact]
    public async Task ConvertAsyncDeleteOriginalsKeepsOriginalWhenAlreadyOptimized()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(),
            FileProcessingStatus.AlreadyOptimized);

        await RunConvertAsync(orchestrator, true);

        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task ConvertAsyncDeleteOriginalsKeepsArchiveWhenEveryEntryIsSkipped()
    {
        var archivePath = CreateTempFile("games.zip", "archive data");
        var extractor = new Mock<IFileExtractor>();
        extractor.Setup(static e => e.GetArchiveInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((100L, 1));
        extractor.Setup(static e => e.ExtractArchiveAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string extractionPath, CancellationToken _) =>
            {
                Directory.CreateDirectory(extractionPath);
                File.WriteAllText(Path.Combine(extractionPath, "game.iso"), "iso data");
                return new ArchiveExtractionResult(true, []);
            });
        var orchestrator = CreateOrchestrator(extractor,
            FileProcessingStatus.AlreadyOptimized);

        await RunConvertAsync(orchestrator, true);

        Assert.True(File.Exists(archivePath));
    }

    [Fact]
    public async Task ConvertAsyncDeleteOriginalsKeepsArchiveWhenExtractorSkippedAnEntry()
    {
        var archivePath = CreateTempFile("games.zip", "archive data");
        var extractor = new Mock<IFileExtractor>();
        extractor.Setup(static e => e.GetArchiveInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((100L, 2));
        extractor.Setup(static e => e.ExtractArchiveAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string extractionPath, CancellationToken _) =>
            {
                Directory.CreateDirectory(extractionPath);
                File.WriteAllText(Path.Combine(extractionPath, "disc1.iso"), "iso data");
                // The built-in extractor leaves additional ISOs in the archive on purpose.
                return new ArchiveExtractionResult(true, ["disc2.iso"]);
            });
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.Converted);

        await RunConvertAsync(orchestrator, true);

        Assert.True(File.Exists(archivePath));
    }

    [Fact]
    public async Task ConvertAsyncDeleteOriginalsKeepsArchiveWhenAnExtractedImageWasNotConverted()
    {
        var archivePath = CreateTempFile("games.zip", "archive data");
        var extractor = new Mock<IFileExtractor>();
        extractor.Setup(static e => e.GetArchiveInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((100L, 2));
        extractor.Setup(static e => e.ExtractArchiveAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string extractionPath, CancellationToken _) =>
            {
                Directory.CreateDirectory(extractionPath);
                File.WriteAllText(Path.Combine(extractionPath, "game.iso"), "iso data");
                File.WriteAllText(Path.Combine(extractionPath, "extra.cso"), "cso data");
                return new ArchiveExtractionResult(true, []);
            });
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.Converted);

        await RunConvertAsync(orchestrator, true);

        Assert.True(File.Exists(archivePath));
    }

    [Fact]
    public async Task ConvertFilesAsyncDeleteOriginalsKeepsArchiveWhenAnExtractedImageIsInvalid()
    {
        var archivePath = CreateTempFile("games.zip", "archive data");
        var extractor = new Mock<IFileExtractor>();
        extractor.Setup(static e => e.GetArchiveInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((100L, 1));
        extractor.Setup(static e => e.ExtractArchiveAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string extractionPath, CancellationToken _) =>
            {
                Directory.CreateDirectory(extractionPath);
                File.WriteAllText(Path.Combine(extractionPath, "game.iso"), "iso data");
                return new ArchiveExtractionResult(true, []);
            });
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.InvalidInput);
        var progress = new CollectingProgress();

        await orchestrator.ConvertFilesAsync([archivePath], Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(archivePath));
        Assert.Contains(progress.Reports, p => p.InvalidIsoCount == 1);
        Assert.Contains(progress.Reports,
            p => p.LogMessage?.Contains("Keeping the original archive", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ConvertFilesAsyncDeleteOriginalsReportsWhenArchiveCannotBeDeleted()
    {
        if (!OperatingSystem.IsWindows()) return;

        var archivePath = CreateTempFile("games.zip", "archive data");
        var extractor = new Mock<IFileExtractor>();
        extractor.Setup(static e => e.GetArchiveInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((100L, 1));
        extractor.Setup(static e => e.ExtractArchiveAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string extractionPath, CancellationToken _) =>
            {
                Directory.CreateDirectory(extractionPath);
                File.WriteAllText(Path.Combine(extractionPath, "game.iso"), "iso data");
                return new ArchiveExtractionResult(true, []);
            });
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.Converted);
        var progress = new CollectingProgress();

        // Hold the archive open so File.Delete fails deterministically.
        // ReSharper disable once UnusedVariable
        await using (var lockStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await orchestrator.ConvertFilesAsync([archivePath], Path.Combine(_tempDir, "out"), true, false, false,
                OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);
        }

        Assert.Contains(progress.Reports,
            p => p.LogMessage?.Contains("Could not delete original archive", StringComparison.Ordinal) == true);
        Assert.True(File.Exists(archivePath));
    }

    [Fact]
    public async Task ConvertAsyncDeleteOriginalsRemovesArchiveWhenEveryExtractedImageIsConverted()
    {
        var archivePath = CreateTempFile("games.zip", "archive data");
        var extractor = new Mock<IFileExtractor>();
        extractor.Setup(static e => e.GetArchiveInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((100L, 1));
        extractor.Setup(static e => e.ExtractArchiveAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string extractionPath, CancellationToken _) =>
            {
                Directory.CreateDirectory(extractionPath);
                File.WriteAllText(Path.Combine(extractionPath, "game.iso"), "iso data");
                return new ArchiveExtractionResult(true, []);
            });
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.Converted);

        await RunConvertAsync(orchestrator, true);

        Assert.False(File.Exists(archivePath));
    }

    #endregion

    #region Output Name Collision Tests

    [Fact]
    public async Task ConvertFilesAsyncSameNamedInputsGetDistinctOutputNames()
    {
        var disc1 = Path.Combine(_tempDir, "Disc1");
        var disc2 = Path.Combine(_tempDir, "Disc2");
        Directory.CreateDirectory(disc1);
        Directory.CreateDirectory(disc2);
        var file1 = Path.Combine(disc1, "game.iso");
        var file2 = Path.Combine(disc2, "game.iso");
        File.WriteAllText(file1, "iso data");
        File.WriteAllText(file2, "iso data");

        var outputNames = new List<string>();
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string outputName, OutputFormat _, bool _, bool _,
                IProgress<BatchOperationProgress> _, CancellationToken _) =>
            {
                outputNames.Add(outputName);
                return FileProcessingStatus.Converted;
            });

        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            xisoSharp: xisoSharp);

        await orchestrator.ConvertFilesAsync([file1, file2], Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.Equal(2, outputNames.Count);
        Assert.Equal(2, outputNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(outputNames, name => name.Equals("game.iso", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(outputNames, name => name.Equals("game (2).iso", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(file1));
        Assert.False(File.Exists(file2));
    }

    #endregion

    #region Test Error Handling Tests

    [Fact]
    public async Task TestFilesAsyncContinuesAfterUnreadableFile()
    {
        // A directory with an image extension cannot be opened as a file; before the fix
        // the resulting exception aborted the whole batch.
        var unreadablePath = Path.Combine(_tempDir, "unreadable.iso");
        Directory.CreateDirectory(unreadablePath);
        var goodPath = CreateTempFile("good.iso", "iso data");

        var testedPaths = new List<string>();
        var integrity = new Mock<IXisoIntegrityService>();
        integrity.Setup(static i => i.TestIsoIntegrityAsync(It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, bool _, IProgress<BatchOperationProgress> _, CancellationToken _) =>
            {
                testedPaths.Add(path);
                return true;
            });

        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrity: integrity);

        await orchestrator.TestFilesAsync(_tempDir, [unreadablePath, goodPath], false, false, false,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.Contains(testedPaths, path => path.Equals(goodPath, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Test Temp Cleanup Tests

    [Fact]
    public async Task TestFilesAsyncCleansUpTempCopyWhenCloudCopyFails()
    {
        var isoPath = CreateTempFile("locked.iso", "iso data");
        var tempRoot = Path.Combine(Path.GetTempPath(), "XISOStudio_Test");
        var before = Directory.Exists(tempRoot)
            ? Directory.GetDirectories(tempRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true);

        // Holding the file without sharing makes the readability probe fail, so the
        // orchestrator takes the cloud-copy path — which then also fails.
        await using (new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await orchestrator.TestFilesAsync(_tempDir, [isoPath], false, false, false,
                new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);
        }

        var after = Directory.Exists(tempRoot)
            ? Directory.GetDirectories(tempRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Empty(after.Except(before, StringComparer.OrdinalIgnoreCase));
    }

    #endregion

    #region File List Overload Tests

    [Fact]
    public async Task ConvertFilesAsyncProcessesOnlyProvidedFiles()
    {
        var selected = CreateTempFile("game1.iso", "iso data");
        var notSelected = CreateTempFile("game2.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(),
            FileProcessingStatus.Converted);

        await orchestrator.ConvertFilesAsync([selected], Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.False(File.Exists(selected));
        Assert.True(File.Exists(notSelected));
    }

    [Fact]
    public async Task ConvertFilesAsyncEmptyListDoesNothing()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(),
            FileProcessingStatus.Converted);

        await orchestrator.ConvertFilesAsync([], Path.Combine(_tempDir, "out"), true, false, false, OutputFormat.Xiso,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task ConvertFilesAsyncFiltersUnsupportedExtensions()
    {
        var binPath = CreateTempFile("game.bin", "bin data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(),
            FileProcessingStatus.Converted);

        await orchestrator.ConvertFilesAsync([binPath], Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(binPath));
    }

    [Fact]
    public async Task ConvertFilesAsyncEngineSkippedStatusCountsAsSkipped()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Skipped);
        var progress = new CollectingProgress();

        await orchestrator.ConvertFilesAsync([isoPath], Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.Contains(progress.Reports, p => p.SkippedCount == 1);
        Assert.DoesNotContain(progress.Reports, p => p.FailedCount > 0);
    }

    [Fact]
    public async Task ConvertFilesAsyncEngineInvalidInputCountsAsFailedAndInvalidIso()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.InvalidInput);
        var progress = new CollectingProgress();

        await orchestrator.ConvertFilesAsync([isoPath], Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.Contains(progress.Reports, p => p.FailedCount == 1 && p.InvalidIsoCount == 1);
        // An invalid input must never lead to deletion of the original file.
        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task ConvertFilesAsyncEngineFailureDoesNotCountAsInvalidIso()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Failed);
        var progress = new CollectingProgress();

        await orchestrator.ConvertFilesAsync([isoPath], Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.Contains(progress.Reports, p => p.FailedCount == 1);
        Assert.DoesNotContain(progress.Reports, p => p.InvalidIsoCount > 0);
    }

    [Fact]
    public async Task TestFilesAsyncOnlyTestsProvidedFiles()
    {
        var selected = CreateTempFile("game1.iso", "iso data");
        var notSelected = CreateTempFile("game2.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(),
            FileProcessingStatus.Converted, integrityResult: true);

        await orchestrator.TestFilesAsync(_tempDir, [selected], true, false, false,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.False(File.Exists(selected));
        Assert.True(File.Exists(Path.Combine(_tempDir, "_success", "game1.iso")));
        Assert.True(File.Exists(notSelected));
    }

    [Fact]
    public async Task TestFilesAsyncFailedFileMovesToFailedFolder()
    {
        var selected = CreateTempFile("game1.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(),
            FileProcessingStatus.Converted, integrityResult: false);

        await orchestrator.TestFilesAsync(_tempDir, [selected], false, true, false,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_tempDir, "_failed", "game1.iso")));
    }

    [Fact]
    public async Task TestFilesAsyncEmptyListDoesNothing()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(),
            FileProcessingStatus.Converted, integrityResult: true);

        await orchestrator.TestFilesAsync(_tempDir, [], true, true, false,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(isoPath));
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "_success")));
    }

    [Theory]
    [InlineData("game.cso")]
    [InlineData("game.1.cso")]
    [InlineData("game.zar")]
    [InlineData("game.chd")]
    public async Task TestFilesAsyncTestsCsoZarAndChdImages(string fileName)
    {
        var imagePath = CreateTempFile(fileName, "image data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(),
            FileProcessingStatus.Converted, integrityResult: true);

        await orchestrator.TestFilesAsync(_tempDir, [imagePath], true, false, false,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_tempDir, "_success", fileName)));
    }

    [Fact]
    public async Task TestFilesAsyncSkipsSplitCisoContinuationParts()
    {
        var part2 = CreateTempFile("game.2.cso", "part 2");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(),
            FileProcessingStatus.Converted, integrityResult: true);

        await orchestrator.TestFilesAsync(_tempDir, [part2], true, false, false,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(part2));
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "_success")));
    }

    [Fact]
    public async Task TestFilesAsyncMovesSplitCisoPartsWithFirstPart()
    {
        var part1 = CreateTempFile("game.1.cso", "part 1");
        var part2 = CreateTempFile("game.2.cso", "part 2");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(),
            FileProcessingStatus.Converted, integrityResult: true);

        await orchestrator.TestFilesAsync(_tempDir, [part1], true, false, false,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_tempDir, "_success", "game.1.cso")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "_success", "game.2.cso")));
        Assert.False(File.Exists(part1));
        Assert.False(File.Exists(part2));
    }

    [Fact]
    public async Task ConvertFilesAsyncZarOutputPassesZarFormatAndExtension()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        string? capturedName = null;
        var capturedFormat = OutputFormat.Xiso;
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string outputName, OutputFormat format, bool _, bool _,
                IProgress<BatchOperationProgress> _, CancellationToken _) =>
            {
                capturedName = outputName;
                capturedFormat = format;
                return FileProcessingStatus.Converted;
            });

        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            xisoSharp: xisoSharp);

        await orchestrator.ConvertFilesAsync([isoPath], Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Zar, new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.Equal("game.zar", capturedName);
        Assert.Equal(OutputFormat.Zar, capturedFormat);
    }

    [Fact]
    public async Task ConvertFilesAsyncCsoOutputPassesCsoFormatAndExtension()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        string? capturedName = null;
        var capturedFormat = OutputFormat.Xiso;
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string outputName, OutputFormat format, bool _, bool _,
                IProgress<BatchOperationProgress> _, CancellationToken _) =>
            {
                capturedName = outputName;
                capturedFormat = format;
                return FileProcessingStatus.Converted;
            });

        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            xisoSharp: xisoSharp);

        await orchestrator.ConvertFilesAsync([isoPath], Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Cso, new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.Equal("game.cso", capturedName);
        Assert.Equal(OutputFormat.Cso, capturedFormat);
    }

    [Fact]
    public async Task ConvertFilesAsyncChdOutputRoutesToChdServiceWithChdExtension()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        string? capturedName = null;
        var chdService = new Mock<IChdService>();
        chdService.Setup(static s => s.ConvertIsoToChdAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string outputName, bool _, bool _,
                IProgress<BatchOperationProgress> _, CancellationToken _) =>
            {
                capturedName = outputName;
                return FileProcessingStatus.Converted;
            });

        var xisoSharp = new Mock<IXisoSharpService>();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            xisoSharp: xisoSharp, chdService: chdService);

        await orchestrator.ConvertFilesAsync([isoPath], Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Chd, new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.Equal("game.chd", capturedName);
        xisoSharp.Verify(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Folder Overload Tests

    [Fact]
    public async Task ConvertAsyncMissingInputFolderThrowsIoException()
    {
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);

        var ex = await Assert.ThrowsAsync<IOException>(() => orchestrator.ConvertAsync(
            Path.Combine(_tempDir, "missing"), Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Xiso, false, new Progress<BatchOperationProgress>(), CloudRetrySkip,
            CancellationToken.None));

        Assert.Contains("does not exist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConvertAsyncEmptyFolderDoesNotReportProgress()
    {
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);

        await orchestrator.ConvertAsync(_tempDir, Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Xiso, false, progress, CloudRetrySkip, CancellationToken.None);

        Assert.Empty(progress.Reports);
    }

    [Fact]
    public async Task ConvertAsyncProcessesConvertibleFilesFoundInFolder()
    {
        var isoPath = CreateTempFile("folder.iso", "iso data");
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);

        await orchestrator.ConvertAsync(_tempDir, Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, false, progress, CloudRetrySkip, CancellationToken.None);

        Assert.False(File.Exists(isoPath));
        Assert.Contains(progress.Reports, p => p.SuccessCount == 1);
    }

    [Fact]
    public async Task ConvertAsyncIgnoresSubfoldersWhenSearchDisabled()
    {
        var nestedDir = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(nestedDir);
        var nestedIso = Path.Combine(nestedDir, "nested.iso");
        File.WriteAllText(nestedIso, "iso data");
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);

        await orchestrator.ConvertAsync(_tempDir, Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, false, progress, CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(nestedIso));
        Assert.Empty(progress.Reports);
    }

    [Fact]
    public async Task ConvertAsyncSearchesSubfoldersWhenEnabled()
    {
        var nestedDir = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(nestedDir);
        var nestedIso = Path.Combine(nestedDir, "nested.iso");
        File.WriteAllText(nestedIso, "iso data");
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);

        await orchestrator.ConvertAsync(_tempDir, Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, true, progress, CloudRetrySkip, CancellationToken.None);

        Assert.False(File.Exists(nestedIso));
        Assert.Contains(progress.Reports, p => p.SuccessCount == 1);
    }

    #endregion

    #region Selection Filtering Tests

    [Fact]
    public async Task ConvertFilesAsyncWhitespaceEntriesAreIgnored()
    {
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);

        await orchestrator.ConvertFilesAsync(["", "   ", "\t"], Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.Empty(progress.Reports);
    }

    [Fact]
    public async Task ConvertFilesAsyncNullEntryThrowsAndLeavesFilesUntouched()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);

        await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.ConvertFilesAsync([isoPath, null!],
            Path.Combine(_tempDir, "out"), true, false, false, OutputFormat.Xiso,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None));

        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task TestFilesAsyncWhitespaceEntriesAreIgnored()
    {
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true);

        await orchestrator.TestFilesAsync(_tempDir, ["", "  "], true, true, false, progress, CloudRetrySkip,
            CancellationToken.None);

        Assert.Empty(progress.Reports);
    }

    [Fact]
    public async Task TestFilesAsyncNullEntryThrowsAndLeavesFilesUntouched()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true);

        await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.TestFilesAsync(_tempDir, [isoPath, null!],
            true, true, false, new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None));

        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task ConvertFilesAsyncUnsupportedFilesOnlyLogsNoConvertibleFiles()
    {
        var binPath = CreateTempFile("game.bin", "bin data");
        var logger = new TestLogger();
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            logger: logger);

        await orchestrator.ConvertFilesAsync([binPath], Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.Empty(progress.Reports);
        Assert.True(logger.HasMessage("No convertible files selected"));
        Assert.True(File.Exists(binPath));
    }

    [Fact]
    public async Task TestFilesAsyncUnsupportedFilesOnlyLogsNoSupportedImages()
    {
        var binPath = CreateTempFile("game.bin", "bin data");
        var logger = new TestLogger();
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            logger: logger);

        await orchestrator.TestFilesAsync(_tempDir, [binPath], true, true, false, progress, CloudRetrySkip,
            CancellationToken.None);

        Assert.Empty(progress.Reports);
        Assert.True(logger.HasMessage("No supported image files selected"));
        Assert.True(File.Exists(binPath));
    }

    [Fact]
    public async Task ConvertFilesAsyncMixedSelectionProcessesOnlyConvertibleEntries()
    {
        var binPath = CreateTempFile("skip.bin", "bin data");
        var isoPath = CreateTempFile("keep.iso", "iso data");
        var convertedInputs = new List<string>();
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((string input, string _, string _, OutputFormat _, bool _, bool _,
                IProgress<BatchOperationProgress> _, CancellationToken _) =>
            {
                convertedInputs.Add(input);
                return Task.FromResult(FileProcessingStatus.Converted);
            });
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            xisoSharp: xisoSharp);

        await orchestrator.ConvertFilesAsync([binPath, isoPath], Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Xiso, new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.Equal(isoPath, Assert.Single(convertedInputs));
        Assert.True(File.Exists(binPath));
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task ConvertFilesAsyncCanceledTokenBeforeStartThrowsOperationCanceled()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.ConvertFilesAsync(
            [isoPath], Path.Combine(_tempDir, "out"), true, false, false, OutputFormat.Xiso,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, cts.Token));

        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task ConvertFilesAsyncMidBatchCancellationStopsBeforeNextFile()
    {
        var first = CreateTempFile("first.iso", "iso data");
        var second = CreateTempFile("second.iso", "iso data");
        using var cts = new CancellationTokenSource();
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string _, OutputFormat _, bool _, bool _,
                IProgress<BatchOperationProgress> _, CancellationToken _) =>
            {
                // ReSharper disable once AccessToDisposedClosure
                cts.Cancel();
                return Task.FromResult(FileProcessingStatus.Converted);
            });
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            xisoSharp: xisoSharp);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.ConvertFilesAsync(
            [first, second], Path.Combine(_tempDir, "out"), true, false, false, OutputFormat.Xiso,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, cts.Token));

        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task TestFilesAsyncCanceledTokenBeforeStartThrowsOperationCanceled()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.TestFilesAsync(
            _tempDir, [isoPath], true, true, false, new Progress<BatchOperationProgress>(), CloudRetrySkip,
            cts.Token));

        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task TestFilesAsyncMidBatchCancellationStopsBeforeNextFile()
    {
        var first = CreateTempFile("first.iso", "iso data");
        var second = CreateTempFile("second.iso", "iso data");
        using var cts = new CancellationTokenSource();
        var testedPaths = new List<string>();
        var integrity = new Mock<IXisoIntegrityService>();
        integrity.Setup(static i => i.TestIsoIntegrityAsync(It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((string path, bool _, IProgress<BatchOperationProgress> _, CancellationToken _) =>
            {
                testedPaths.Add(path);
                // ReSharper disable once AccessToDisposedClosure
                cts.Cancel();
                return Task.FromResult(true);
            });
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrity: integrity);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.TestFilesAsync(
            _tempDir, [first, second], false, false, false, new Progress<BatchOperationProgress>(), CloudRetrySkip,
            cts.Token));

        Assert.Equal(first, Assert.Single(testedPaths));
        Assert.True(File.Exists(second));
    }

    #endregion

    #region Output Name Collision Extended Tests

    [Fact]
    public async Task ConvertFilesAsyncThreeSameNamedInputsGetDistinctOutputNames()
    {
        var files = new List<string>();
        foreach (var folderName in new[] { "Disc1", "Disc2", "Disc3" })
        {
            var folder = Path.Combine(_tempDir, folderName);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "game.iso");
            File.WriteAllText(file, "iso data");
            files.Add(file);
        }

        var outputNames = new List<string>();
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string outputName, OutputFormat _, bool _, bool _,
                IProgress<BatchOperationProgress> _, CancellationToken _) =>
            {
                outputNames.Add(outputName);
                return Task.FromResult(FileProcessingStatus.Converted);
            });
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            xisoSharp: xisoSharp);

        await orchestrator.ConvertFilesAsync(files, Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Xiso, new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.Equal(3, outputNames.Count);
        Assert.Equal(3, outputNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("game.iso", outputNames, StringComparer.Ordinal);
        Assert.Contains("game (2).iso", outputNames, StringComparer.Ordinal);
        Assert.Contains("game (3).iso", outputNames, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ConvertFilesAsyncOutputInSourceFolderReportsOverwriteError()
    {
        var outDir = Path.Combine(_tempDir, "same");
        Directory.CreateDirectory(outDir);
        var isoPath = Path.Combine(outDir, "game.iso");
        File.WriteAllText(isoPath, "iso data");
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);

        await orchestrator.ConvertFilesAsync([isoPath], outDir, true, false, false, OutputFormat.Xiso,
            progress, CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(isoPath));
        Assert.Contains(progress.Reports,
            p => p.LogMessage?.Contains("would overwrite the source", StringComparison.Ordinal) == true);
        Assert.Contains(progress.Reports, p => p.FailedCount == 1);
    }

    #endregion

    #region Delete Originals Extended Tests

    [Fact]
    public async Task ConvertFilesAsyncKeepsOriginalWhenDeleteOriginalsDisabled()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);

        await RunConvertAsync(orchestrator, false);

        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task TestFilesAsyncWithoutMoveFlagsLeavesImagesInPlace()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true);

        await orchestrator.TestFilesAsync(_tempDir, [isoPath], false, false, false, progress, CloudRetrySkip,
            CancellationToken.None);

        Assert.True(File.Exists(isoPath));
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "_success")));
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "_failed")));
        Assert.Contains(progress.Reports, p => p.SuccessCount == 1);
    }

    #endregion

    #region System Update Propagation Tests

    [Fact]
    public async Task ConvertFilesAsyncPassesSkipSystemUpdateAndIntegrityToXisoEngine()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        bool? capturedSkip = null;
        bool? capturedIntegrity = null;
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string _, OutputFormat _, bool skipSystemUpdate, bool checkIntegrity,
                IProgress<BatchOperationProgress> _, CancellationToken _) =>
            {
                capturedSkip = skipSystemUpdate;
                capturedIntegrity = checkIntegrity;
                return Task.FromResult(FileProcessingStatus.Converted);
            });
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            xisoSharp: xisoSharp);

        await orchestrator.ConvertFilesAsync([isoPath], Path.Combine(_tempDir, "out"), false, true, true,
            OutputFormat.Xiso, new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.True(capturedSkip);
        Assert.True(capturedIntegrity);
    }

    [Fact]
    public async Task ConvertFilesAsyncPassesSkipSystemUpdateAndIntegrityToChdEngine()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        bool? capturedSkip = null;
        bool? capturedIntegrity = null;
        var chdService = new Mock<IChdService>();
        chdService.Setup(static s => s.ConvertIsoToChdAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string _, bool skipSystemUpdate, bool checkIntegrity,
                IProgress<BatchOperationProgress> _, CancellationToken _) =>
            {
                capturedSkip = skipSystemUpdate;
                capturedIntegrity = checkIntegrity;
                return Task.FromResult(FileProcessingStatus.Converted);
            });
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            chdService: chdService);

        await orchestrator.ConvertFilesAsync([isoPath], Path.Combine(_tempDir, "out"), false, true, true,
            OutputFormat.Chd, new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.True(capturedSkip);
        Assert.True(capturedIntegrity);
    }

    [Fact]
    public async Task ConvertFilesAsyncArchiveExtractedIsoReceivesSkipSystemUpdate()
    {
        var archivePath = CreateTempFile("games.zip", "archive data");
        bool? capturedSkip = null;
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string _, OutputFormat _, bool skipSystemUpdate, bool _,
                IProgress<BatchOperationProgress> _, CancellationToken _) =>
            {
                capturedSkip = skipSystemUpdate;
                return Task.FromResult(FileProcessingStatus.Converted);
            });
        var extractor = CreateExtractor((100L, 1), path =>
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "disc.iso"), "iso data");
        }, new ArchiveExtractionResult(true, []));
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.Converted, xisoSharp: xisoSharp);

        await orchestrator.ConvertFilesAsync([archivePath], Path.Combine(_tempDir, "out"), false, true, false,
            OutputFormat.Xiso, new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.True(capturedSkip);
    }

    #endregion

    #region Convert Versus Test Mode Tests

    [Fact]
    public async Task TestFilesAsyncNeverInvokesConversionServices()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var xisoSharp = new Mock<IXisoSharpService>();
        var chdService = new Mock<IChdService>();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true, xisoSharp: xisoSharp, chdService: chdService);

        await orchestrator.TestFilesAsync(_tempDir, [isoPath], false, false, false,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        xisoSharp.Verify(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        chdService.Verify(static s => s.ConvertIsoToChdAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConvertFilesAsyncNeverInvokesIntegrityService()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var integrity = new Mock<IXisoIntegrityService>();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrity: integrity);

        await orchestrator.ConvertFilesAsync([isoPath], Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Xiso, new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        integrity.Verify(static i => i.TestIsoIntegrityAsync(It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region File Mover Verification Tests

    [Fact]
    public async Task TestFilesAsyncMoveSuccessfulSendsFileToSuccessFolderThroughMover()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var moves = new List<(string Source, string Folder, string Reason)>();
        var fileMover = CreateRecordingMover(moves, deleteSource: false);
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true, fileMover: fileMover);

        await orchestrator.TestFilesAsync(_tempDir, [isoPath], true, false, false,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        var move = Assert.Single(moves);
        Assert.Equal(isoPath, move.Source);
        Assert.Equal(Path.Combine(_tempDir, "_success"), move.Folder);
        Assert.Equal("successfully tested", move.Reason);
    }

    [Fact]
    public async Task TestFilesAsyncMoveFailedSendsFileToFailedFolderThroughMover()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var moves = new List<(string Source, string Folder, string Reason)>();
        var fileMover = CreateRecordingMover(moves, deleteSource: false);
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: false, fileMover: fileMover);

        await orchestrator.TestFilesAsync(_tempDir, [isoPath], false, true, false,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        var move = Assert.Single(moves);
        Assert.Equal(isoPath, move.Source);
        Assert.Equal(Path.Combine(_tempDir, "_failed"), move.Folder);
        Assert.Equal("failed test", move.Reason);
    }

    [Fact]
    public async Task TestFilesAsyncMovesSplitCisoContinuationPartsThroughMover()
    {
        var part1 = CreateTempFile("game.1.cso", "part 1");
        var part2 = CreateTempFile("game.2.cso", "part 2");
        var moves = new List<(string Source, string Folder, string Reason)>();
        var fileMover = CreateRecordingMover(moves, deleteSource: true);
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true, fileMover: fileMover);

        await orchestrator.TestFilesAsync(_tempDir, [part1], true, false, false,
            new Progress<BatchOperationProgress>(), CloudRetrySkip, CancellationToken.None);

        Assert.Equal(2, moves.Count);
        Assert.Contains(moves, m => m.Source.Equals(part1, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(moves, m => m.Source.Equals(part2, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Skipped Archive Reporting Tests

    [Fact]
    public async Task ConvertFilesAsyncArchiveWithSkippedEntryExplainsWhyArchiveIsKept()
    {
        var archivePath = CreateTempFile("games.zip", "archive data");
        var extractor = CreateExtractor((100L, 2), path =>
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "disc1.iso"), "iso data");
        }, new ArchiveExtractionResult(true, ["disc2.iso"]));
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.Converted);

        await orchestrator.ConvertFilesAsync([archivePath], Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(archivePath));
        Assert.Contains(progress.Reports,
            p => p.LogMessage?.Contains("were not extracted", StringComparison.Ordinal) == true);
        Assert.Contains(progress.Reports, p => p.LogMessage?.Contains("disc2.iso", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ConvertFilesAsyncArchiveWithUnprocessedImageExplainsWhyArchiveIsKept()
    {
        var archivePath = CreateTempFile("games.zip", "archive data");
        var extractor = CreateExtractor((100L, 2), path =>
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "game.iso"), "iso data");
            File.WriteAllText(Path.Combine(path, "bonus.cso"), "cso data");
        }, new ArchiveExtractionResult(true, []));
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.Converted);

        await orchestrator.ConvertFilesAsync([archivePath], Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(archivePath));
        Assert.Contains(progress.Reports, p => p.LogMessage?.Contains("not plain", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ConvertFilesAsyncArchiveWithLoneSplitContinuationPartKeepsArchive()
    {
        var archivePath = CreateTempFile("games.zip", "archive data");
        var extractor = CreateExtractor((100L, 2), path =>
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "game.iso"), "iso data");
            File.WriteAllText(Path.Combine(path, "disc.2.cso"), "cso part data");
        }, new ArchiveExtractionResult(true, []));
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.Converted);

        await orchestrator.ConvertFilesAsync([archivePath], Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        // The .2.cso part is extracted but never converted; deleting the archive would
        // destroy the only copy of that part.
        Assert.True(File.Exists(archivePath));
        Assert.Contains(progress.Reports, p => p.LogMessage?.Contains("not plain", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ConvertFilesAsyncArchiveWithAlreadyOptimizedImageReportsSkippedArchive()
    {
        var archivePath = CreateTempFile("games.zip", "archive data");
        var extractor = CreateExtractor((100L, 1), path =>
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "game.iso"), "iso data");
        }, new ArchiveExtractionResult(true, []));
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.AlreadyOptimized);

        await orchestrator.ConvertFilesAsync([archivePath], Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(archivePath));
        Assert.Contains(progress.Reports, p => p.SkippedCount == 1);
        Assert.Contains(progress.Reports,
            p => p.LogMessage?.Contains("already optimized", StringComparison.Ordinal) == true);
    }

    #endregion

    #region Mocked Failure Handling Tests

    [Fact]
    public async Task ConvertFilesAsyncDirectoryNotFoundStopsBatchWithFatalReport()
    {
        var first = CreateTempFile("first.iso", "iso data");
        var second = CreateTempFile("second.iso", "iso data");
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((string input, string _, string _, OutputFormat _, bool _, bool _,
                    IProgress<BatchOperationProgress> _, CancellationToken _) =>
                input.Equals(first, StringComparison.OrdinalIgnoreCase)
                    ? Task.FromException<FileProcessingStatus>(new DirectoryNotFoundException("Output drive gone"))
                    : Task.FromResult(FileProcessingStatus.Converted));
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            xisoSharp: xisoSharp);

        await Assert.ThrowsAnyAsync<DirectoryNotFoundException>(() => orchestrator.ConvertFilesAsync(
            [first, second], Path.Combine(_tempDir, "out"), true, false, false, OutputFormat.Xiso, progress,
            CloudRetrySkip, CancellationToken.None));

        Assert.Contains(progress.Reports, p => p.LogMessage?.Contains("FATAL ERROR", StringComparison.Ordinal) == true);
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task ConvertFilesAsyncDeviceNotReadyStopsBatchWithFatalReport()
    {
        var first = CreateTempFile("first.iso", "iso data");
        var second = CreateTempFile("second.iso", "iso data");
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((string input, string _, string _, OutputFormat _, bool _, bool _,
                    IProgress<BatchOperationProgress> _, CancellationToken _) =>
                input.Equals(first, StringComparison.OrdinalIgnoreCase)
                    ? Task.FromException<FileProcessingStatus>(
                        new IOException("The device is not ready.", unchecked((int)0x80070015)))
                    : Task.FromResult(FileProcessingStatus.Converted));
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            xisoSharp: xisoSharp);

        await Assert.ThrowsAnyAsync<IOException>(() => orchestrator.ConvertFilesAsync(
            [first, second], Path.Combine(_tempDir, "out"), true, false, false, OutputFormat.Xiso, progress,
            CloudRetrySkip, CancellationToken.None));

        Assert.Contains(progress.Reports, p => p.LogMessage?.Contains("FATAL ERROR", StringComparison.Ordinal) == true);
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task ConvertFilesAsyncDiskSpaceErrorStopsBatchWithDiskReport()
    {
        var first = CreateTempFile("first.iso", "iso data");
        var second = CreateTempFile("second.iso", "iso data");
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((string input, string _, string _, OutputFormat _, bool _, bool _,
                    IProgress<BatchOperationProgress> _, CancellationToken _) =>
                input.Equals(first, StringComparison.OrdinalIgnoreCase)
                    ? Task.FromException<FileProcessingStatus>(
                        new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)))
                    : Task.FromResult(FileProcessingStatus.Converted));
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            xisoSharp: xisoSharp);

        await Assert.ThrowsAnyAsync<IOException>(() => orchestrator.ConvertFilesAsync(
            [first, second], Path.Combine(_tempDir, "out"), true, false, false, OutputFormat.Xiso, progress,
            CloudRetrySkip, CancellationToken.None));

        Assert.Contains(progress.Reports,
            p => p.LogMessage?.Contains("Not enough disk space", StringComparison.Ordinal) == true);
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task ConvertFilesAsyncGenericEngineFailureContinuesBatch()
    {
        var first = CreateTempFile("first.iso", "iso data");
        var second = CreateTempFile("second.iso", "iso data");
        var logger = new TestLogger();
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((string input, string _, string _, OutputFormat _, bool _, bool _,
                    IProgress<BatchOperationProgress> _, CancellationToken _) =>
                input.Equals(first, StringComparison.OrdinalIgnoreCase)
                    ? Task.FromException<FileProcessingStatus>(new InvalidOperationException("engine exploded"))
                    : Task.FromResult(FileProcessingStatus.Converted));
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            xisoSharp: xisoSharp, logger: logger);

        await orchestrator.ConvertFilesAsync([first, second], Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.True(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.Contains(progress.Reports, p => p.FailedCount == 1);
        Assert.Contains(progress.Reports, p => p.SuccessCount == 1);
        Assert.True(logger.HasMessage("Orchestrator error on"));
    }

    [Fact]
    public async Task ConvertFilesAsyncCorruptIsoMessageContinuesBatchWithFriendlyMessage()
    {
        var first = CreateTempFile("first.iso", "iso data");
        var second = CreateTempFile("second.iso", "iso data");
        var xisoSharp = new Mock<IXisoSharpService>();
        xisoSharp.Setup(static s => s.ConvertIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<OutputFormat>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((string input, string _, string _, OutputFormat _, bool _, bool _,
                    IProgress<BatchOperationProgress> _, CancellationToken _) =>
                input.Equals(first, StringComparison.OrdinalIgnoreCase)
                    ? Task.FromException<FileProcessingStatus>(
                        new InvalidOperationException("End of stream reached while reading"))
                    : Task.FromResult(FileProcessingStatus.Converted));
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            xisoSharp: xisoSharp);

        await orchestrator.ConvertFilesAsync([first, second], Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.Contains(progress.Reports,
            p => p.LogMessage?.Contains("corrupt or incomplete", StringComparison.Ordinal) == true);
        Assert.Contains(progress.Reports, p => p.SuccessCount == 1);
    }

    [Fact]
    public async Task TestFilesAsyncMoverEnvironmentalFailureStopsBatchWithFatalReport()
    {
        var first = CreateTempFile("first.iso", "iso data");
        var second = CreateTempFile("second.iso", "iso data");
        var fileMover = new Mock<IFileMover>();
        fileMover.Setup(static m => m.MoveTestedFileAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DirectoryNotFoundException("Test destination gone"));
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true, fileMover: fileMover);

        await Assert.ThrowsAnyAsync<DirectoryNotFoundException>(() => orchestrator.TestFilesAsync(
            _tempDir, [first, second], true, false, false, progress, CloudRetrySkip, CancellationToken.None));

        Assert.Contains(progress.Reports, p => p.LogMessage?.Contains("FATAL ERROR", StringComparison.Ordinal) == true);
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task TestFilesAsyncMoveFailureReportsFailedFileAndContinues()
    {
        var first = CreateTempFile("first.iso", "iso data");
        var second = CreateTempFile("second.iso", "iso data");
        var fileMover = new Mock<IFileMover>();
        fileMover.Setup(static m => m.MoveTestedFileAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("move failed"));
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true, fileMover: fileMover);

        await orchestrator.TestFilesAsync(_tempDir, [first, second], true, false, false, progress,
            CloudRetrySkip, CancellationToken.None);

        // A file whose move failed is reported as failed, not as a success, and the batch continues.
        Assert.Equal(2, progress.Reports.Count(p => p.FailedCount == 1));
        Assert.DoesNotContain(progress.Reports, p => p.SuccessCount > 0);
    }

    [Fact]
    public async Task TestFilesAsyncLockedFileMoveFailureIsLoggedAsEnvironmental()
    {
        var first = CreateTempFile("first.iso", "iso data");
        var second = CreateTempFile("second.iso", "iso data");
        var fileMover = new Mock<IFileMover>();
        fileMover.Setup(static m => m.MoveTestedFileAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException(
                "The process cannot access the file because it is being used by another process.",
                unchecked((int)0x80070020))); // ERROR_SHARING_VIOLATION
        var logger = new TestLogger();
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true, fileMover: fileMover, logger: logger);

        await orchestrator.TestFilesAsync(_tempDir, [first, second], true, false, false, progress,
            CloudRetrySkip, CancellationToken.None);

        // A locked file is environmental: it is reported to the user and counted as failed,
        // but it must never be logged at Warning or higher, which would auto-upload it as a
        // bug report (the bug-report sink forwards Warning and above).
        Assert.True(logger.HasMessage(LogEventLevel.Information, "Handled test error"));
        Assert.DoesNotContain(logger.Events, e => e.Level >= LogEventLevel.Warning);
        Assert.Equal(2, progress.Reports.Count(p => p.FailedCount == 1));
    }

    [Fact]
    public async Task TestFilesAsyncLockedSourceCopyFallbackIsLoggedAsEnvironmental()
    {
        // Windows enforces FileShare; on Unix an open file does not block reading.
        if (!OperatingSystem.IsWindows()) return;

        var locked = CreateTempFile("locked.iso", "iso data");
        await using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var logger = new TestLogger();
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true, logger: logger);

        await orchestrator.TestFilesAsync(_tempDir, [locked], true, false, false, progress,
            CloudRetrySkip, CancellationToken.None);

        // The unreadable source takes the copy fallback, which fails with a sharing
        // violation; that failure is environmental and must not become a bug report.
        Assert.True(logger.HasMessage(LogEventLevel.Information, "environmental error"));
        Assert.DoesNotContain(logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task TestFilesAsyncFailedTestWithFailingMoveCountsFileOnce()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var fileMover = new Mock<IFileMover>();
        fileMover.Setup(static m => m.MoveTestedFileAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("move failed"));
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: false, fileMover: fileMover);

        await orchestrator.TestFilesAsync(_tempDir, [isoPath], false, true, false, progress,
            CloudRetrySkip, CancellationToken.None);

        // The test already failed; the failed move must not count the same file a second time.
        Assert.Equal(1, progress.Reports.Count(p => p.FailedCount == 1));
    }

    [Fact]
    public async Task ConvertFilesAsyncUnexpectedArchiveErrorIsLoggedAsErrorAndContinues()
    {
        var archivePath = CreateTempFile("games.zip", "archive data");
        var second = CreateTempFile("good.iso", "iso data");
        var extractor = new Mock<IFileExtractor>();
        extractor.Setup(static e => e.GetArchiveInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((100L, 1));
        extractor.Setup(static e => e.ExtractArchiveAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected extractor bug"));
        var logger = new TestLogger();
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.Converted, logger: logger);
        var progress = new CollectingProgress();

        await orchestrator.ConvertFilesAsync([archivePath, second], Path.Combine(_tempDir, "out"), false, false,
            false, OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        // An unexpected archive failure must stay visible as an application error, while
        // the rest of the batch still runs.
        Assert.True(logger.HasMessage(LogEventLevel.Error, "Archive processing failed"));
        Assert.Contains(progress.Reports, p => string.Equals(p.FailedPathToAdd, archivePath, StringComparison.Ordinal));
        Assert.Contains(progress.Reports, p => p.SuccessCount == 1);
    }

    [Fact]
    public async Task TestFilesAsyncIntegrityErrorMarksFileFailedAndContinues()
    {
        var first = CreateTempFile("bad.iso", "iso data");
        var second = CreateTempFile("good.iso", "iso data");
        var integrity = new Mock<IXisoIntegrityService>();
        integrity.Setup(static i => i.TestIsoIntegrityAsync(It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<IProgress<BatchOperationProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((string path, bool _, IProgress<BatchOperationProgress> _, CancellationToken _) =>
                path.Equals(first, StringComparison.OrdinalIgnoreCase)
                    ? Task.FromException<bool>(new InvalidOperationException("broken image"))
                    : Task.FromResult(true));
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrity: integrity);

        await orchestrator.TestFilesAsync(_tempDir, [first, second], false, false, false, progress,
            CloudRetrySkip, CancellationToken.None);

        Assert.Contains(progress.Reports, p => p.FailedCount == 1);
        Assert.Contains(progress.Reports, p => p.SuccessCount == 1);
    }

    #endregion

    #region Progress Reporting Tests

    [Fact]
    public async Task ConvertFilesAsyncProgressIsMonotonicAndCountsSuccesses()
    {
        var first = CreateTempFile("a.iso", "a");
        var second = CreateTempFile("b.iso", "b");
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);

        await orchestrator.ConvertFilesAsync([first, second], Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.Equal(2, progress.Reports[0].TotalFiles);
        var processed = progress.Reports.Where(p => p.ProcessedCount.HasValue)
            .Select(p => p.ProcessedCount!.Value).ToList();
        Assert.Equal(Expected, processed);
        Assert.Equal(2, progress.Reports.Where(p => p.SuccessCount == 1).Sum(p => p.SuccessCount!.Value));
        Assert.DoesNotContain(progress.Reports, p => p.FailedCount > 0);
    }

    [Fact]
    public async Task ConvertFilesAsyncReportsStatusTextForEachFile()
    {
        var first = CreateTempFile("alpha.iso", "a");
        var second = CreateTempFile("beta.iso", "b");
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);

        await orchestrator.ConvertFilesAsync([first, second], Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.Contains(progress.Reports, p => p.StatusText?.Contains("alpha.iso", StringComparison.Ordinal) == true);
        Assert.Contains(progress.Reports, p => p.StatusText?.Contains("beta.iso", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task TestFilesAsyncProgressIsMonotonic()
    {
        var first = CreateTempFile("a.iso", "a");
        var second = CreateTempFile("b.iso", "b");
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true);

        await orchestrator.TestFilesAsync(_tempDir, [first, second], false, false, false, progress,
            CloudRetrySkip, CancellationToken.None);

        Assert.Equal(2, progress.Reports[0].TotalFiles);
        var processed = progress.Reports.Where(p => p.ProcessedCount.HasValue)
            .Select(p => p.ProcessedCount!.Value).ToList();
        Assert.Equal(Expected, processed);
        Assert.Equal(2, progress.Reports.Where(p => p.SuccessCount == 1).Sum(p => p.SuccessCount!.Value));
    }

    [Fact]
    public async Task TestFilesAsyncReportsSourceDriveForEachFile()
    {
        var first = CreateTempFile("a.iso", "a");
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true);

        await orchestrator.TestFilesAsync(_tempDir, [first], false, false, false, progress,
            CloudRetrySkip, CancellationToken.None);

        // The disk-speed display must monitor the drive the image is read from, not the temp drive.
        Assert.Contains(progress.Reports,
            p => string.Equals(p.CurrentDrive, PathHelper.GetDriveLetter(first), StringComparison.Ordinal));
    }

    #endregion

    #region Cloud Copy Path Tests

    [Fact]
    public async Task ConvertFilesAsyncUnreadableFileTakesCloudCopyPathAndReportsFailure()
    {
        var isoPath = CreateTempFile("locked.iso", "iso data");
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);

        await using (new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await orchestrator.ConvertFilesAsync([isoPath], Path.Combine(_tempDir, "out"), true, false, false,
                OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);
        }

        Assert.True(File.Exists(isoPath));
        Assert.Contains(progress.Reports,
            p => p.LogMessage?.Contains("Copying to local temp", StringComparison.Ordinal) == true);
        Assert.Contains(progress.Reports, p => p.LogMessage?.Contains("Copy failed", StringComparison.Ordinal) == true);
        Assert.Contains(progress.Reports, p => p.FailedCount == 1);
    }

    [Fact]
    public async Task ConvertFilesAsyncUnreadableFileDoesNotRequestCloudRetry()
    {
        var isoPath = CreateTempFile("locked.iso", "iso data");
        var retryRequested = false;
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted);

        await using (new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await orchestrator.ConvertFilesAsync([isoPath], Path.Combine(_tempDir, "out"), true, false, false,
                OutputFormat.Xiso, new Progress<BatchOperationProgress>(),
                _ =>
                {
                    retryRequested = true;
                    return Task.FromResult(CloudRetryResult.Skip);
                }, CancellationToken.None);
        }

        Assert.False(retryRequested);
        Assert.True(File.Exists(isoPath));
    }

    [Fact]
    public async Task TestFilesAsyncUnreadableFileTakesCloudCopyPathAndFails()
    {
        var isoPath = CreateTempFile("locked-test.iso", "iso data");
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.Converted,
            integrityResult: true);

        await using (new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await orchestrator.TestFilesAsync(_tempDir, [isoPath], false, false, false, progress,
                CloudRetrySkip, CancellationToken.None);
        }

        Assert.True(File.Exists(isoPath));
        Assert.Contains(progress.Reports, p => p.LogMessage?.Contains("Copy failed", StringComparison.Ordinal) == true);
        Assert.Contains(progress.Reports, p => p.FailedCount == 1);
        Assert.DoesNotContain(progress.Reports, p => p.SuccessCount > 0);
    }

    #endregion

    #region Archive Temp And Disk Tests

    [Fact]
    public async Task ConvertFilesAsyncArchiveExtractionFailureCleansTempFolder()
    {
        var archivePath = CreateTempFile("broken.zip", "archive data");
        var extractedPaths = new List<string>();
        var extractor = CreateExtractor((100L, 1), path =>
        {
            extractedPaths.Add(path);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "partial.iso"), "partial data");
        }, ArchiveExtractionResult.Failed);
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.Converted);

        await orchestrator.ConvertFilesAsync([archivePath], Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        var extractionPath = Assert.Single(extractedPaths);
        Assert.False(Directory.Exists(extractionPath));
        Assert.True(File.Exists(archivePath));
        Assert.Contains(progress.Reports, p => p.FailedCount == 1);
    }

    [Fact]
    public async Task ConvertFilesAsyncArchiveDiskSpaceFailureStopsBatch()
    {
        var archivePath = CreateTempFile("huge.zip", "archive data");
        var extractor = new Mock<IFileExtractor>();
        extractor.Setup(static e => e.GetArchiveInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long.MaxValue, 1));
        var diskMonitor = new Mock<IDiskMonitorService>();
        diskMonitor.Setup(static d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(0);
        diskMonitor.Setup(static d => d.FindDrivesWithFreeSpace(It.IsAny<long>(), It.IsAny<string>()))
            .Returns(Array.Empty<string>());
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.Converted, diskMonitor: diskMonitor);

        await Assert.ThrowsAsync<IOException>(() => orchestrator.ConvertFilesAsync([archivePath],
            Path.Combine(_tempDir, "out"), false, false, false, OutputFormat.Xiso, progress, CloudRetrySkip,
            CancellationToken.None));

        Assert.Contains(progress.Reports,
            p => p.LogMessage?.Contains("Not enough disk space", StringComparison.Ordinal) == true);
        diskMonitor.Verify(static d => d.FindDrivesWithFreeSpace(It.IsAny<long>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ConvertFilesAsyncArchiveInfoFailureFallsBackToDefaultTempPath()
    {
        var archivePath = CreateTempFile("odd.zip", "archive data");
        var extractor = new Mock<IFileExtractor>();
        extractor.Setup(static e => e.GetArchiveInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cannot analyze"));
        extractor.Setup(static e => e.ExtractArchiveAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string extractionPath, CancellationToken _) =>
            {
                Directory.CreateDirectory(extractionPath);
                File.WriteAllText(Path.Combine(extractionPath, "game.iso"), "iso data");
                return new ArchiveExtractionResult(true, []);
            });
        var logger = new TestLogger();
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(extractor, FileProcessingStatus.Converted, logger: logger);

        await orchestrator.ConvertFilesAsync([archivePath], Path.Combine(_tempDir, "out"), false, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.True(logger.HasMessage("Could not analyze archive"));
        Assert.Contains(progress.Reports, p => p.SuccessCount == 1);
    }

    #endregion

    #region Conversion Status Tests

    [Fact]
    public async Task ConvertFilesAsyncAlreadyOptimizedCountsAsSkipped()
    {
        var isoPath = CreateTempFile("game.iso", "iso data");
        var progress = new CollectingProgress();
        var orchestrator = CreateOrchestrator(new Mock<IFileExtractor>(), FileProcessingStatus.AlreadyOptimized);

        await orchestrator.ConvertFilesAsync([isoPath], Path.Combine(_tempDir, "out"), true, false, false,
            OutputFormat.Xiso, progress, CloudRetrySkip, CancellationToken.None);

        Assert.Contains(progress.Reports, p => p.SkippedCount == 1);
        Assert.DoesNotContain(progress.Reports, p => p.FailedCount > 0);
        Assert.True(File.Exists(isoPath));
    }

    #endregion
}