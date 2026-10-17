using System.Reflection;
using XISOStudio.Interfaces;
using XISOStudio.Services;
using Moq;
using Serilog.Events;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests file relocation behavior of <c>FileMoverService</c>, including destination collisions and free space handling.</summary>
public class FileMoverServiceTests : IDisposable
{
    private readonly TestLogger _logger = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"FileMoverTests_{Guid.NewGuid():N}");

    public FileMoverServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
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

    private FileMoverService CreateService(long availableFreeSpace)
    {
        return CreateService(availableFreeSpace, out _);
    }

    private FileMoverService CreateService(long availableFreeSpace, out Mock<IDiskMonitorService> diskMonitor)
    {
        diskMonitor = new Mock<IDiskMonitorService>();
        diskMonitor.Setup(static d => d.GetAvailableFreeSpace(It.IsAny<string>())).Returns(availableFreeSpace);
        return new FileMoverService(_logger.Logger, diskMonitor.Object);
    }

    private string WriteSourceFile(string fileName, string content)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static string? InvokeGetVolumeRoot(string path)
    {
        var method = typeof(FileMoverService).GetMethod(
            "GetVolumeRoot", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string?)method.Invoke(null, [path]);
    }

    private static bool InvokeIsCrossVolumeMove(FileMoverService service, string sourceFile, string destinationFolder)
    {
        var method = typeof(FileMoverService).GetMethod(
            "IsCrossVolumeMove", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (bool)method.Invoke(service, [sourceFile, destinationFolder])!;
    }

    [Fact]
    public async Task MoveTestedFileAsyncSameVolumeMovesFileDespiteLowReportedFreeSpace()
    {
        var source = Path.Combine(_tempDir, "game.iso");
        await File.WriteAllTextAsync(source, new string('x', 4096));
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(1);

        await service.MoveTestedFileAsync(source, destinationFolder, "successfully tested", CancellationToken.None);

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(destinationFolder, "game.iso")));
    }

    [Fact]
    public async Task MoveTestedFileAsyncSkipsWhenDestinationAlreadyExists()
    {
        var source = Path.Combine(_tempDir, "game.iso");
        await File.WriteAllTextAsync(source, "source data");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        Directory.CreateDirectory(destinationFolder);
        await File.WriteAllTextAsync(Path.Combine(destinationFolder, "game.iso"), "existing data");
        var service = CreateService(long.MaxValue);

        await service.MoveTestedFileAsync(source, destinationFolder, "successfully tested", CancellationToken.None);

        Assert.True(File.Exists(source));
        Assert.Equal("existing data", await File.ReadAllTextAsync(Path.Combine(destinationFolder, "game.iso")));
    }

    #region Same-Volume Move Tests

    [Fact]
    public async Task MoveTestedFileAsyncSameVolumeMovesFileAndPreservesContent()
    {
        var source = WriteSourceFile("game.iso", "payload-123");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(long.MaxValue);

        await service.MoveTestedFileAsync(source, destinationFolder, "successfully tested", CancellationToken.None);

        Assert.False(File.Exists(source));
        Assert.Equal("payload-123", await File.ReadAllTextAsync(Path.Combine(destinationFolder, "game.iso")));
    }

    [Fact]
    public async Task MoveTestedFileAsyncSameVolumeDoesNotQueryDiskMonitor()
    {
        var source = WriteSourceFile("game.iso", "data");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(1, out var diskMonitor);

        await service.MoveTestedFileAsync(source, destinationFolder, "reason", CancellationToken.None);

        diskMonitor.Verify(static d => d.GetAvailableFreeSpace(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MoveTestedFileAsyncCreatesNestedDestinationFolderAutomatically()
    {
        var source = WriteSourceFile("game.iso", "data");
        var destinationFolder = Path.Combine(_tempDir, "_success", "nested", "deep");
        var service = CreateService(long.MaxValue);

        await service.MoveTestedFileAsync(source, destinationFolder, "reason", CancellationToken.None);

        Assert.True(Directory.Exists(destinationFolder));
        Assert.True(File.Exists(Path.Combine(destinationFolder, "game.iso")));
    }

    [Fact]
    public async Task MoveTestedFileAsyncSuccessfulMoveLogsReasonAndDestination()
    {
        var source = WriteSourceFile("game.iso", "data");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(long.MaxValue);

        await service.MoveTestedFileAsync(source, destinationFolder, "successfully tested", CancellationToken.None);

        Assert.True(_logger.HasMessage(LogEventLevel.Information, "Moved \"game.iso\" (\"successfully tested\")"));
        Assert.True(_logger.HasMessage(LogEventLevel.Information, destinationFolder));
    }

    #endregion

    #region Missing Source Tests

    [Fact]
    public async Task MoveTestedFileAsyncMissingSourceLogsSourceNoLongerExists()
    {
        var source = Path.Combine(_tempDir, "missing.iso");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(long.MaxValue);

        await service.MoveTestedFileAsync(source, destinationFolder, "reason", CancellationToken.None);

        Assert.True(_logger.HasMessage("Source file no longer exists"));
    }

    [Fact]
    public async Task MoveTestedFileAsyncMissingSourceCreatesDestinationFolder()
    {
        var source = Path.Combine(_tempDir, "missing.iso");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(long.MaxValue);

        await service.MoveTestedFileAsync(source, destinationFolder, "reason", CancellationToken.None);

        Assert.True(Directory.Exists(destinationFolder));
        Assert.False(File.Exists(Path.Combine(destinationFolder, "missing.iso")));
    }

    [Fact]
    public async Task MoveTestedFileAsyncMissingSourceDoesNotQueryDiskMonitor()
    {
        var source = Path.Combine(_tempDir, "missing.iso");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(long.MaxValue, out var diskMonitor);

        await service.MoveTestedFileAsync(source, destinationFolder, "reason", CancellationToken.None);

        diskMonitor.Verify(static d => d.GetAvailableFreeSpace(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MoveTestedFileAsyncDirectoryAsSourceLogsMissingSource()
    {
        var sourceDirectory = Path.Combine(_tempDir, "not-a-file");
        Directory.CreateDirectory(sourceDirectory);
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(long.MaxValue);

        await service.MoveTestedFileAsync(sourceDirectory, destinationFolder, "reason", CancellationToken.None);

        Assert.True(_logger.HasMessage("Source file no longer exists"));
        Assert.True(Directory.Exists(sourceDirectory));
    }

    [Fact]
    public async Task MoveTestedFileAsyncInvalidFileNameLogsMissingSource()
    {
        var source = Path.Combine(_tempDir, "bad<name>.iso");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(long.MaxValue);

        await service.MoveTestedFileAsync(source, destinationFolder, "reason", CancellationToken.None);

        Assert.True(_logger.HasMessage("Source file no longer exists"));
    }

    [Fact]
    public async Task MoveTestedFileAsyncEmptySourcePathLogsMissingSource()
    {
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(long.MaxValue);

        var exception = await Record.ExceptionAsync(() =>
            service.MoveTestedFileAsync("", destinationFolder, "reason", CancellationToken.None));

        Assert.Null(exception);
        Assert.True(_logger.HasMessage("Source file no longer exists"));
    }

    [Fact]
    public async Task MoveTestedFileAsyncNullSourceThrowsArgumentNullException()
    {
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(long.MaxValue);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.MoveTestedFileAsync(null!, destinationFolder, "reason", CancellationToken.None));
    }

    [Fact]
    public async Task MoveTestedFileAsyncNullDestinationFolderThrowsArgumentNullException()
    {
        var source = WriteSourceFile("game.iso", "data");
        var service = CreateService(long.MaxValue);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.MoveTestedFileAsync(source, null!, "reason", CancellationToken.None));
    }

    #endregion

    #region Duplicate Destination Tests

    [Fact]
    public async Task MoveTestedFileAsyncDestinationExistsSkipsMoveAndLogs()
    {
        var source = WriteSourceFile("game.iso", "source data");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        Directory.CreateDirectory(destinationFolder);
        await File.WriteAllTextAsync(Path.Combine(destinationFolder, "game.iso"), "existing data");
        var service = CreateService(long.MaxValue, out var diskMonitor);

        await service.MoveTestedFileAsync(source, destinationFolder, "reason", CancellationToken.None);

        Assert.True(File.Exists(source));
        Assert.True(_logger.HasMessage("Destination file already exists"));
        diskMonitor.Verify(static d => d.GetAvailableFreeSpace(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MoveTestedFileAsyncDestinationExistsDoesNotOverwriteExistingContent()
    {
        var source = WriteSourceFile("game.iso", "source data");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        Directory.CreateDirectory(destinationFolder);
        await File.WriteAllTextAsync(Path.Combine(destinationFolder, "game.iso"), "existing data");
        var service = CreateService(long.MaxValue);

        await service.MoveTestedFileAsync(source, destinationFolder, "reason", CancellationToken.None);

        Assert.Equal("existing data", await File.ReadAllTextAsync(Path.Combine(destinationFolder, "game.iso")));
    }

    [Fact]
    public async Task MoveTestedFileAsyncDestinationExistsLeavesSourceFileIntact()
    {
        var source = WriteSourceFile("game.iso", "source data");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        Directory.CreateDirectory(destinationFolder);
        await File.WriteAllTextAsync(Path.Combine(destinationFolder, "game.iso"), "existing data");
        var service = CreateService(long.MaxValue);

        await service.MoveTestedFileAsync(source, destinationFolder, "reason", CancellationToken.None);

        Assert.Equal("source data", await File.ReadAllTextAsync(source));
    }

    #endregion

    #region Failure Reporting Tests

    [Fact]
    public async Task MoveTestedFileAsyncMoveFailureLogsAndRethrows()
    {
        var source = WriteSourceFile("game.iso", "data");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        // A directory occupying the destination file path makes File.Move fail deterministically.
        Directory.CreateDirectory(Path.Combine(destinationFolder, "game.iso"));
        var service = CreateService(long.MaxValue);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            service.MoveTestedFileAsync(source, destinationFolder, "reason", CancellationToken.None));

        Assert.True(_logger.HasMessage(LogEventLevel.Information, "Error moving"));
        Assert.True(File.Exists(source));
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task MoveTestedFileAsyncPreCanceledTokenThrowsOperationCanceledException()
    {
        var source = WriteSourceFile("game.iso", "data");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(long.MaxValue);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.MoveTestedFileAsync(source, destinationFolder, "reason", cts.Token));
    }

    [Fact]
    public async Task MoveTestedFileAsyncPreCanceledTokenLogsCancellation()
    {
        var source = WriteSourceFile("game.iso", "data");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(long.MaxValue);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.MoveTestedFileAsync(source, destinationFolder, "reason", cts.Token));

        Assert.True(_logger.HasMessage(LogEventLevel.Information, "cancelled"));
    }

    [Fact]
    public async Task MoveTestedFileAsyncPreCanceledTokenDoesNotCreateDestinationFolder()
    {
        var source = WriteSourceFile("game.iso", "data");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(long.MaxValue);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.MoveTestedFileAsync(source, destinationFolder, "reason", cts.Token));

        Assert.False(Directory.Exists(destinationFolder));
        Assert.True(File.Exists(source));
    }

    #endregion

    #region Volume Classification Tests

    [Fact]
    public void GetVolumeRootLocalPathReturnsNonEmptyRoot()
    {
        var root = InvokeGetVolumeRoot(_tempDir);

        Assert.False(string.IsNullOrEmpty(root));
        Assert.StartsWith(root, Path.GetFullPath(_tempDir), PathHelper.PathComparison);
    }

    [Fact]
    public void GetVolumeRootNestedPathReturnsSameRootAsParent()
    {
        var nested = Path.Combine(_tempDir, "a", "b");
        Directory.CreateDirectory(nested);

        var parentRoot = InvokeGetVolumeRoot(_tempDir);
        var nestedRoot = InvokeGetVolumeRoot(nested);

        Assert.Equal(parentRoot, nestedRoot);
    }

    [Fact]
    public void GetVolumeRootEmptyPathThrowsArgumentException()
    {
        var exception = Assert.Throws<TargetInvocationException>(() => InvokeGetVolumeRoot(""));

        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public void GetVolumeRootNullPathThrowsArgumentException()
    {
        var exception = Assert.Throws<TargetInvocationException>(() => InvokeGetVolumeRoot(null!));

        Assert.IsType<ArgumentException>(exception.InnerException, exactMatch: false);
    }

    [Fact]
    public void IsCrossVolumeMoveSameVolumeReturnsFalse()
    {
        var source = Path.Combine(_tempDir, "game.iso");
        var destinationFolder = Path.Combine(_tempDir, "_success");
        var service = CreateService(0);

        Assert.False(InvokeIsCrossVolumeMove(service, source, destinationFolder));
    }

    [Fact]
    public void IsCrossVolumeMoveDifferentVolumeReturnsTrue()
    {
        var service = CreateService(0);
        var destinationFolder = Path.Combine(_tempDir, "_success");

        if (OperatingSystem.IsWindows())
        {
            // The \\?\ prefix produces a different path root than the plain drive root,
            // which exercises the different-volume branch on a single-drive machine.
            var extendedSource = @"\\?\" + Path.Combine(_tempDir, "game.iso");
            Assert.True(InvokeIsCrossVolumeMove(service, extendedSource, destinationFolder));
            return;
        }

        var roots = DriveInfo.GetDrives()
            .Where(static d => d.IsReady)
            .Select(static d => d.RootDirectory.FullName)
            .Where(static r => !string.IsNullOrEmpty(r))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (roots.Count < 2) return;

        Assert.True(InvokeIsCrossVolumeMove(
            service,
            Path.Combine(roots[0], "game.iso"),
            Path.Combine(roots[1], "_success")));
    }

    [Fact]
    public void IsCrossVolumeMoveEmptySourceReturnsTrue()
    {
        var service = CreateService(0);
        var destinationFolder = Path.Combine(_tempDir, "_success");

        Assert.True(InvokeIsCrossVolumeMove(service, "", destinationFolder));
    }

    [Fact]
    public void IsCrossVolumeMoveUnresolvablePathLogsDebug()
    {
        var service = CreateService(0);
        var destinationFolder = Path.Combine(_tempDir, "_success");

        Assert.True(InvokeIsCrossVolumeMove(service, "", destinationFolder));
        Assert.True(_logger.HasMessage(LogEventLevel.Debug, "Could not resolve volume roots"));
    }

    #endregion

    #region Cross-Volume Space Check Tests

    [Fact]
    public async Task MoveTestedFileAsyncCrossVolumeInsufficientSpaceThrows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = WriteSourceFile("game.iso", new string('x', 4096));
        var destinationFolder = Path.Combine(_tempDir, "_cross");
        var service = CreateService(10, out var diskMonitor);

        await Assert.ThrowsAsync<IOException>(() => service.MoveTestedFileAsync(@"\\?\" + source,
            destinationFolder, "reason", CancellationToken.None));

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(destinationFolder, "game.iso")));
        diskMonitor.Verify(static d => d.GetAvailableFreeSpace(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task MoveTestedFileAsyncCrossVolumeInsufficientSpaceLogsRequiredAndAvailableSpace()
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = WriteSourceFile("game.iso", new string('x', 4096));
        var destinationFolder = Path.Combine(_tempDir, "_cross");
        var service = CreateService(10);

        await Assert.ThrowsAsync<IOException>(() => service.MoveTestedFileAsync(@"\\?\" + source,
            destinationFolder, "reason", CancellationToken.None));

        Assert.True(_logger.HasMessage("Insufficient disk space"));
        Assert.True(_logger.HasMessage("Required:"));
        Assert.True(_logger.HasMessage("Available:"));
    }

    [Fact]
    public async Task MoveTestedFileAsyncCrossVolumeInsufficientSpaceQueriesDestinationFolder()
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = WriteSourceFile("game.iso", new string('x', 4096));
        var destinationFolder = Path.Combine(_tempDir, "_cross");
        var service = CreateService(10, out var diskMonitor);

        await Assert.ThrowsAsync<IOException>(() => service.MoveTestedFileAsync(@"\\?\" + source,
            destinationFolder, "reason", CancellationToken.None));

        diskMonitor.Verify(d => d.GetAvailableFreeSpace(destinationFolder), Times.Once);
    }

    [Fact]
    public async Task MoveTestedFileAsyncCrossVolumeSufficientSpaceMovesFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = WriteSourceFile("game.iso", "cross data");
        var destinationFolder = Path.Combine(_tempDir, "_cross");
        var service = CreateService(long.MaxValue);

        await service.MoveTestedFileAsync(@"\\?\" + source, destinationFolder, "reason", CancellationToken.None);

        Assert.False(File.Exists(source));
        var destinationFile = Path.Combine(destinationFolder, "game.iso");
        Assert.True(File.Exists(destinationFile));
        Assert.Equal("cross data", await File.ReadAllTextAsync(destinationFile));
    }

    [Fact]
    public async Task MoveTestedFileAsyncCrossVolumeZeroFreeSpaceReportedProceedsWithMove()
    {
        if (!OperatingSystem.IsWindows()) return;

        var source = WriteSourceFile("game.iso", new string('x', 4096));
        var destinationFolder = Path.Combine(_tempDir, "_cross");
        var service = CreateService(0);

        await service.MoveTestedFileAsync(@"\\?\" + source, destinationFolder, "reason", CancellationToken.None);

        // A reported free space of 0 means "unknown", so the guard must be skipped.
        Assert.True(File.Exists(Path.Combine(destinationFolder, "game.iso")));
    }

    #endregion
}