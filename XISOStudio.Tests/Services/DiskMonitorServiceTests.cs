using XISOStudio.Services;
using Serilog.Events;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests disk monitoring, free space queries, and drive selection in <c>DiskMonitorService</c>.</summary>
public class DiskMonitorServiceTests
{
    private static DiskMonitorService CreateService()
    {
        var logger = new TestLogger();
        return new DiskMonitorService(logger.Logger);
    }

    private static DiskMonitorService CreateService(out TestLogger logger)
    {
        logger = new TestLogger();
        return new DiskMonitorService(logger.Logger);
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DiskMonitorTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    #region Constructor Tests

    [Fact]
    public void ConstructorInitializesWithValidParameters()
    {
        var service = CreateService();
        Assert.NotNull(service);
    }

    [Fact]
    public void ConstructorDriveLetterIsNull()
    {
        var service = CreateService();
        Assert.Null(service.CurrentDriveLetter);
    }

    [Fact]
    public void ConstructorStatusMessageIsNull()
    {
        var service = CreateService();
        Assert.Null(service.StatusMessage);
    }

    #endregion

    #region StartMonitoring Tests

    [Fact]
    public void StartMonitoringUncPathShowsNetworkStatusOnWindows()
    {
        var service = CreateService();

        service.StartMonitoring(@"\\server\share");

        if (OperatingSystem.IsWindows())
        {
            // A UNC path has no drive letter; it must still reach the network-drive branch
            // instead of being treated as an unchanged drive.
            Assert.Equal("Disk speed monitoring unavailable for network drives", service.StatusMessage);
        }
        else
        {
            // Performance counters are not available at all on Linux/macOS.
            Assert.Null(service.StatusMessage);
        }
    }

    [Fact]
    public void StartMonitoringNullPathLeavesServiceInactive()
    {
        using var service = CreateService();

        service.StartMonitoring(null);

        Assert.Null(service.CurrentDriveLetter);
        Assert.Null(service.StatusMessage);
    }

    [Fact]
    public void StartMonitoringEmptyPathLeavesServiceInactive()
    {
        using var service = CreateService();

        service.StartMonitoring("");

        Assert.Null(service.CurrentDriveLetter);
        Assert.Null(service.StatusMessage);
    }

    [Fact]
    public void StartMonitoringInvalidPathDoesNotThrow()
    {
        using var service = CreateService();

        // ReSharper disable once AccessToDisposedClosure
        var exception = Record.Exception(() => service.StartMonitoring("\0"));

        Assert.Null(exception);
    }

    [Fact]
    public void StartMonitoringInvalidPathLeavesStatusMessageNull()
    {
        using var service = CreateService();

        service.StartMonitoring("\0");

        Assert.Null(service.CurrentDriveLetter);
        Assert.Null(service.StatusMessage);
    }

    [Fact]
    public void StartMonitoringUncPathDoesNotSetDriveLetter()
    {
        using var service = CreateService();

        service.StartMonitoring(@"\\server\share");

        Assert.Null(service.CurrentDriveLetter);
    }

    [Fact]
    public void StartMonitoringUncPathRepeatedKeepsNetworkStatusOnWindows()
    {
        using var service = CreateService();

        service.StartMonitoring(@"\\server\share");
        service.StartMonitoring(@"\\server\share");

        var expected = OperatingSystem.IsWindows()
            ? "Disk speed monitoring unavailable for network drives"
            : null;
        Assert.Equal(expected, service.StatusMessage);
    }

    [Fact]
    public void StartMonitoringLocalPathReportsConsistentState()
    {
        using var service = CreateService(out var logger);

        service.StartMonitoring(Path.GetTempPath());

        if (!OperatingSystem.IsWindows())
        {
            // Performance counters are not available at all on Linux/macOS.
            Assert.Null(service.StatusMessage);
            Assert.Null(service.CurrentDriveLetter);
            return;
        }

        if (service.StatusMessage == null)
        {
            Assert.NotNull(service.CurrentDriveLetter);
            Assert.True(logger.HasMessage(LogEventLevel.Information, "Monitoring disk speed for drive"));
        }
        else
        {
            Assert.Contains("unavailable", service.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void StartMonitoringLocalPathTwiceKeepsStatusConsistent()
    {
        using var service = CreateService();
        var path = Path.GetTempPath();

        service.StartMonitoring(path);
        var firstStatus = service.StatusMessage;
        var firstDrive = service.CurrentDriveLetter;

        service.StartMonitoring(path);

        Assert.Equal(firstStatus, service.StatusMessage);
        Assert.Equal(firstDrive, service.CurrentDriveLetter);
    }

    #endregion

    #region GetAvailableFreeSpace Tests

    [Fact]
    public void GetAvailableFreeSpaceNullPathReturnsZero()
    {
        var service = CreateService();
        var result = service.GetAvailableFreeSpace(null);
        Assert.Equal(0, result);
    }

    [Fact]
    public void GetAvailableFreeSpaceEmptyPathReturnsZero()
    {
        var service = CreateService();
        var result = service.GetAvailableFreeSpace("");
        Assert.Equal(0, result);
    }

    [Fact]
    public void GetAvailableFreeSpaceLocalDriveReturnsPositiveValue()
    {
        var service = CreateService();
        var root = Path.GetPathRoot(Path.GetTempPath());
        Assert.False(string.IsNullOrEmpty(root));
        var result = service.GetAvailableFreeSpace(root);
        Assert.True(result > 0, $"Expected positive free space, got {result}");
    }

    [Fact]
    public void GetAvailableFreeSpaceLocalDriveSubfolderReturnsPositiveValue()
    {
        var service = CreateService();
        var result = service.GetAvailableFreeSpace(AppContext.BaseDirectory);
        Assert.True(result > 0, $"Expected positive free space, got {result}");
    }

    [Fact]
    public void GetAvailableFreeSpaceTempPathReturnsPositive()
    {
        using var service = CreateService();

        var result = service.GetAvailableFreeSpace(Path.GetTempPath());

        Assert.True(result > 0, $"Expected positive free space, got {result}");
    }

    [Fact]
    public void GetAvailableFreeSpaceNewSubfolderReturnsPositive()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var service = CreateService();

            var result = service.GetAvailableFreeSpace(directory);

            Assert.True(result > 0, $"Expected positive free space, got {result}");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void GetAvailableFreeSpaceNonExistentChildPathReturnsPositive()
    {
        // On Unix, DriveInfo resolves a path's containing filesystem only when the path
        // exists; a non-existent child legitimately reports 0 there.
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}");
        using var service = CreateService();

        var result = service.GetAvailableFreeSpace(path);

        // The drive containing a non-existent child can still be inspected.
        Assert.True(result > 0, $"Expected positive free space, got {result}");
    }

    [Fact]
    public void GetAvailableFreeSpaceInvalidPathWithNullCharacterReturnsZero()
    {
        using var service = CreateService();

        var result = service.GetAvailableFreeSpace("invalid\0path");

        Assert.Equal(0, result);
    }

    #endregion

    #region FindDriveWithFreeSpace Tests

    [Fact]
    public void FindDriveWithFreeSpaceZeroBytesReturnsDrive()
    {
        var service = CreateService();
        var result = service.FindDriveWithFreeSpace(0);
        Assert.NotNull(result);
    }

    [Fact]
    public void FindDriveWithFreeSpaceEnormousRequirementReturnsNull()
    {
        var service = CreateService();
        // 1 Exabyte - no drive should have this much space
        var result = service.FindDriveWithFreeSpace(long.MaxValue / 2);
        Assert.Null(result);
    }

    [Fact]
    public void FindDriveWithFreeSpaceExcludesSpecifiedDrive()
    {
        var service = CreateService();
        var tempRoot = Path.GetPathRoot(Path.GetTempPath());
        Assert.False(string.IsNullOrEmpty(tempRoot));
        var excludedDrive = tempRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var result = service.FindDriveWithFreeSpace(0, excludedDrive);

        if (result != null)
        {
            // Exact root comparison: on Unix the excluded root can be "/" (trimmed to ""),
            // which is a substring of every mount point, so a substring check is invalid.
            var trimmedResult = result.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.False(string.Equals(trimmedResult, excludedDrive, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void FindDriveWithFreeSpaceNegativeRequirementReturnsDrive()
    {
        using var service = CreateService();

        // A negative requirement is smaller than the safety buffer, so any ready local drive qualifies.
        var result = service.FindDriveWithFreeSpace(-1);

        Assert.NotNull(result);
    }

    [Fact]
    public void FindDriveWithFreeSpaceExcludedDriveWithTrailingSeparatorIsSkipped()
    {
        using var service = CreateService();
        var tempRoot = Path.GetPathRoot(Path.GetTempPath());
        Assert.False(string.IsNullOrEmpty(tempRoot));
        var excludedRoot = tempRoot + Path.DirectorySeparatorChar;

        var result = service.FindDriveWithFreeSpace(0, excludedRoot);

        if (result != null)
        {
            var trimmedResult = result.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var trimmedExcluded = excludedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.False(string.Equals(trimmedResult, trimmedExcluded, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void FindDriveWithFreeSpaceEnormousRequirementWithExclusionReturnsNull()
    {
        using var service = CreateService();

        var result = service.FindDriveWithFreeSpace(long.MaxValue / 2, Path.GetPathRoot(Path.GetTempPath()));

        Assert.Null(result);
    }

    [Fact]
    public void FindDrivesWithFreeSpaceZeroBytesReturnsDrives()
    {
        using var service = CreateService();

        var result = service.FindDrivesWithFreeSpace(0);

        Assert.NotEmpty(result);
    }

    [Fact]
    public void FindDrivesWithFreeSpaceEnormousRequirementReturnsEmpty()
    {
        using var service = CreateService();

        // 1 Exabyte - no drive should have this much space
        var result = service.FindDrivesWithFreeSpace(long.MaxValue / 2);

        Assert.Empty(result);
    }

    [Fact]
    public void FindDrivesWithFreeSpaceExcludesSpecifiedDrive()
    {
        using var service = CreateService();
        var tempRoot = Path.GetPathRoot(Path.GetTempPath());
        Assert.False(string.IsNullOrEmpty(tempRoot));
        var excludedDrive = tempRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var result = service.FindDrivesWithFreeSpace(0, excludedDrive);

        Assert.DoesNotContain(result, root => string.Equals(
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            excludedDrive, StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region StopMonitoring Tests

    [Fact]
    public void StopMonitoringClearsDriveLetter()
    {
        var service = CreateService();
        service.StopMonitoring();
        Assert.Null(service.CurrentDriveLetter);
    }

    [Fact]
    public void StopMonitoringClearsStatusMessage()
    {
        var service = CreateService();
        service.StopMonitoring();
        Assert.Null(service.StatusMessage);
    }

    [Fact]
    public void StopMonitoringCalledMultipleTimesDoesNotThrow()
    {
        var service = CreateService();
        service.StopMonitoring();
        var exception = Record.Exception(service.StopMonitoring);
        Assert.Null(exception);
    }

    [Fact]
    public void StopMonitoringAfterStartResetsState()
    {
        using var service = CreateService();

        service.StartMonitoring(Path.GetTempPath());
        service.StopMonitoring();

        Assert.Null(service.CurrentDriveLetter);
        Assert.Null(service.StatusMessage);
    }

    [Fact]
    public void StopMonitoringAfterUncStartClearsNetworkStatusOnWindows()
    {
        using var service = CreateService();

        service.StartMonitoring(@"\\server\share");
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(service.StatusMessage);
        }

        service.StopMonitoring();

        Assert.Null(service.CurrentDriveLetter);
        Assert.Null(service.StatusMessage);
    }

    #endregion

    #region Speed Format Tests

    [Fact]
    public void GetCurrentReadSpeedFormattedWithoutMonitoringReturnsNa()
    {
        var service = CreateService();
        var result = service.GetCurrentReadSpeedFormatted();
        Assert.Equal("N/A", result);
    }

    [Fact]
    public void GetCurrentWriteSpeedFormattedWithoutMonitoringReturnsNa()
    {
        var service = CreateService();
        var result = service.GetCurrentWriteSpeedFormatted();
        Assert.Equal("N/A", result);
    }

    [Fact]
    public void GetCurrentReadSpeedFormattedAfterStopMonitoringReturnsNa()
    {
        using var service = CreateService();

        service.StartMonitoring(Path.GetTempPath());
        service.StopMonitoring();

        Assert.Equal("N/A", service.GetCurrentReadSpeedFormatted());
    }

    [Fact]
    public void GetCurrentWriteSpeedFormattedAfterStopMonitoringReturnsNa()
    {
        using var service = CreateService();

        service.StartMonitoring(Path.GetTempPath());
        service.StopMonitoring();

        Assert.Equal("N/A", service.GetCurrentWriteSpeedFormatted());
    }

    [Fact]
    public void GetCurrentReadSpeedFormattedAfterUncStartReturnsNa()
    {
        using var service = CreateService();

        service.StartMonitoring(@"\\server\share");

        // No read counter is ever created for a network path.
        Assert.Equal("N/A", service.GetCurrentReadSpeedFormatted());
    }

    [Fact]
    public void GetCurrentWriteSpeedFormattedAfterUncStartReturnsNa()
    {
        using var service = CreateService();

        service.StartMonitoring(@"\\server\share");

        // No write counter is ever created for a network path.
        Assert.Equal("N/A", service.GetCurrentWriteSpeedFormatted());
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void DisposeCalledMultipleTimesDoesNotThrow()
    {
        var service = CreateService();
        service.Dispose();
        var exception = Record.Exception(service.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public void DisposeAfterStopMonitoringDoesNotThrow()
    {
        var service = CreateService();
        service.StopMonitoring();
        var exception = Record.Exception(service.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public void DisposeAfterStartMonitoringDoesNotThrow()
    {
        var service = CreateService();

        service.StartMonitoring(Path.GetTempPath());
        var exception = Record.Exception(service.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void DisposeThenStopMonitoringDoesNotThrow()
    {
        var service = CreateService();

        service.Dispose();
        var exception = Record.Exception(service.StopMonitoring);

        Assert.Null(exception);
    }

    [Fact]
    public void DisposeThenGetAvailableFreeSpaceReturnsPositive()
    {
        var service = CreateService();

        service.Dispose();
        var result = service.GetAvailableFreeSpace(Path.GetTempPath());

        Assert.True(result > 0, $"Expected positive free space, got {result}");
    }

    #endregion
}