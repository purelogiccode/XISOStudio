using System.Diagnostics;
using System.Runtime.InteropServices;
using XISOStudio.Interfaces;
using Serilog;

namespace XISOStudio.Services;

/// <summary>
/// Monitors disk read and write speed for a drive using Windows performance counters and
/// resolves available free space for local drives and network shares.
/// </summary>
public class DiskMonitorService : IDiskMonitorService, IDisposable
{
    private readonly ILogger _logger;
    private PerformanceCounter? _diskReadSpeedCounter;
    private PerformanceCounter? _diskWriteSpeedCounter;

    /// <summary>Drive letter currently being monitored, or <c>null</c> when monitoring is inactive.</summary>
    public string? CurrentDriveLetter { get; private set; }

    /// <summary>Reason monitoring is unavailable, or <c>null</c> when monitoring is active.</summary>
    public string? StatusMessage { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DiskMonitorService"/> class.
    /// </summary>
    /// <param name="logger">Logger used to report monitoring state changes and errors.</param>
    public DiskMonitorService(ILogger logger)
    {
        _logger = logger.ForContext<DiskMonitorService>();
    }

    // P/Invoke for GetDiskFreeSpaceEx which works with UNC paths (Windows only)
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    /// <summary>
    /// Starts monitoring disk speed for the drive containing the specified path, stopping any
    /// previous monitoring first. Network paths and non-Windows platforms are reported through
    /// <see cref="StatusMessage"/> instead.
    /// </summary>
    /// <param name="path">Path whose drive should be monitored; may be <c>null</c>.</param>
    public void StartMonitoring(string? path)
    {
        string? driveLetter;
        bool isNetworkPath;
        try
        {
            driveLetter = PathHelper.GetDriveLetter(path);
            isNetworkPath = PathHelper.IsNetworkPath(path);
        }
        catch (Exception ex)
        {
            // Monitoring is best-effort: an unreadable path must never break the operation.
            StopMonitoring();
            StatusMessage = "Disk speed monitoring unavailable - unable to inspect the path";
            _logger.Warning(ex, "Failed to inspect the path for disk monitoring: {Path}", path);
            return;
        }

        // Never treat a UNC path (which has no drive letter, so both values are null) as
        // "same drive" before the network check below; otherwise its status is never shown.
        if (!isNetworkPath &&
            string.Equals(CurrentDriveLetter, driveLetter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        StopMonitoring();

        if (!OperatingSystem.IsWindows())
        {
            // Windows performance counters are not available on Linux/macOS; the UI shows N/A.
            return;
        }

        // Check for network drives - explicitly excluded from speed monitoring
        if (isNetworkPath)
        {
            StatusMessage = "Disk speed monitoring unavailable for network drives";
            _logger.Information("Disk speed monitoring unavailable for network drives.");
            return;
        }

        if (string.IsNullOrEmpty(driveLetter))
        {
            StatusMessage = "Disk speed monitoring unavailable - unable to determine drive letter";
            return;
        }

        var perfCounterInstanceName = driveLetter.EndsWith(':') ? driveLetter : driveLetter + ":";

        try
        {
            // Check if LogicalDisk category exists
            if (!PerformanceCounterCategory.Exists("LogicalDisk"))
            {
                StatusMessage = "Disk speed monitoring unavailable - performance counters disabled";
                _logger.Information(
                    "Performance counter category 'LogicalDisk' not available. Performance counters may be disabled.");
                return;
            }

            // Check if drive instance exists
            if (!PerformanceCounterCategory.InstanceExists(perfCounterInstanceName, "LogicalDisk"))
            {
                StatusMessage = $"Disk speed monitoring unavailable for drive {perfCounterInstanceName}";
                _logger.Information("Performance counter for drive {Drive} not available.", perfCounterInstanceName);
                return;
            }

            // Initialize read speed counter. Assign the field before priming so the catch
            // path (StopMonitoring) disposes the handle when NextValue throws.
            var readCounter =
                new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", perfCounterInstanceName, true);
            _diskReadSpeedCounter = readCounter;
            readCounter.NextValue(); // Prime the counter

            // Initialize write speed counter
            var writeCounter =
                new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", perfCounterInstanceName, true);
            _diskWriteSpeedCounter = writeCounter;
            writeCounter.NextValue(); // Prime the counter

            CurrentDriveLetter = driveLetter;
            StatusMessage = null; // Clear any previous status
            _logger.Information("Monitoring disk speed for drive: {Drive}", perfCounterInstanceName);
        }
        catch (Exception ex)
        {
            // StopMonitoring clears the status message, so set it afterwards — otherwise the
            // UI can never show the reason why monitoring is unavailable.
            StopMonitoring();
            StatusMessage = "Disk speed monitoring unavailable - performance counter error";
            // Performance counters being unavailable/broken is environmental, not a defect.
            _logger.Information(ex, "Failed to initialize disk monitor for {Drive}", perfCounterInstanceName);
        }
    }

    /// <summary>
    /// Stops monitoring, releases the performance counters, and clears the drive letter and
    /// status message.
    /// </summary>
    public void StopMonitoring()
    {
        try
        {
            _diskReadSpeedCounter?.Dispose();
            _diskReadSpeedCounter = null;
            _diskWriteSpeedCounter?.Dispose();
            _diskWriteSpeedCounter = null;
            CurrentDriveLetter = null;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            // Monitoring is best-effort; never let counter disposal break the caller.
            _logger.Warning(ex, "Error while releasing the disk performance counters");
        }
    }

    /// <summary>
    /// Gets the current disk read speed formatted for display, stopping monitoring when the
    /// counter cannot be read.
    /// </summary>
    /// <returns>The formatted read speed, or "N/A" when monitoring is unavailable.</returns>
    public string GetCurrentReadSpeedFormatted()
    {
        if (!OperatingSystem.IsWindows()) return "N/A";
        if (_diskReadSpeedCounter == null) return "N/A";

        try
        {
            var val = _diskReadSpeedCounter.NextValue();
            return Formatter.FormatBytesPerSecond(val);
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "Failed to read current disk read speed. Stopping monitoring.");
            StopMonitoring();
            return "N/A";
        }
    }

    /// <summary>
    /// Gets the current disk write speed formatted for display, stopping monitoring when the
    /// counter cannot be read.
    /// </summary>
    /// <returns>The formatted write speed, or "N/A" when monitoring is unavailable.</returns>
    public string GetCurrentWriteSpeedFormatted()
    {
        if (!OperatingSystem.IsWindows()) return "N/A";
        if (_diskWriteSpeedCounter == null) return "N/A";

        try
        {
            var val = _diskWriteSpeedCounter.NextValue();
            return Formatter.FormatBytesPerSecond(val);
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "Failed to read current disk write speed. Stopping monitoring.");
            StopMonitoring();
            return "N/A";
        }
    }

    /// <summary>
    /// Gets the number of free bytes available on the drive or network share containing the
    /// specified path.
    /// </summary>
    /// <param name="path">Path to inspect; may be <c>null</c>.</param>
    /// <returns>The available free bytes, or 0 when the value cannot be determined.</returns>
    public long GetAvailableFreeSpace(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }

        try
        {
            // Handle UNC paths (network shares) using P/Invoke (Windows only)
            if (OperatingSystem.IsWindows() && PathHelper.IsUncPath(path))
            {
                // For UNC paths, use GetDiskFreeSpaceEx which works with network shares
                // We need to pass the share root (\\server\share) not a subdirectory
                var shareInfo = PathHelper.TryGetUncShareInfo(path);
                if (shareInfo.HasValue)
                {
                    var shareRoot = $@"\\{shareInfo.Value.Server}\{shareInfo.Value.Share}";
                    if (GetDiskFreeSpaceEx(shareRoot, out var freeBytesAvailable, out _, out _))
                    {
                        return (long)freeBytesAvailable;
                    }
                }

                // Fallback: try the path as-is
                if (GetDiskFreeSpaceEx(path, out var freeBytes, out _, out _))
                {
                    return (long)freeBytes;
                }

                return 0;
            }

            // Handle mapped network drives and local drives
            var driveLetter = PathHelper.GetDriveLetter(path);
            if (!string.IsNullOrEmpty(driveLetter))
            {
                var driveInfo = new DriveInfo(driveLetter);
                if (driveInfo.IsReady)
                {
                    return driveInfo.AvailableFreeSpace;
                }
            }

            // Fallback for other cases
            var fallbackDriveInfo = new DriveInfo(path);
            if (fallbackDriveInfo.IsReady)
            {
                return fallbackDriveInfo.AvailableFreeSpace;
            }
        }
        catch (Exception ex)
        {
            // Ignore errors and return 0
            _logger.Information(ex, "Failed to determine available free space for path: {Path}", path);
        }

        return 0;
    }

    /// <summary>
    /// Finds the first eligible local drive with enough free space for the required size plus
    /// the standard safety buffer.
    /// </summary>
    /// <param name="requiredBytes">Number of bytes that must be available.</param>
    /// <param name="excludeDrive">Optional drive root to skip during the search.</param>
    /// <returns>The name of a suitable drive, or <c>null</c> when none has enough free space.</returns>
    public string? FindDriveWithFreeSpace(long requiredBytes, string? excludeDrive = null)
    {
        var drives = FindDrivesWithFreeSpace(requiredBytes, excludeDrive);
        return drives.Count > 0 ? drives[0] : null;
    }

    /// <summary>
    /// Finds every eligible local drive with enough free space for the required size plus
    /// the standard safety buffer, so callers can try the next drive when a candidate is
    /// not writable (ACL restrictions, BitLocker-locked or read-only volumes).
    /// </summary>
    /// <param name="requiredBytes">Number of bytes that must be available.</param>
    /// <param name="excludeDrive">Optional drive root to skip during the search.</param>
    /// <returns>The names of the suitable drives; empty when none has enough free space.</returns>
    public IReadOnlyList<string> FindDrivesWithFreeSpace(long requiredBytes, string? excludeDrive = null)
    {
        var candidates = new List<string>();

        try
        {
            var excludedRoot = excludeDrive?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var requiredWithBuffer = PathHelper.AddSafetyBuffer(requiredBytes);

            var drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                // Inspecting a single drive can fail (a volume can disappear or become
                // unreadable mid-scan); skip that drive instead of losing the whole search.
                try
                {
                    if (!drive.IsReady)
                        continue;

                    // Windows: only fixed local drives are eligible. Unix: DriveType is not
                    // meaningful, so accept anything that is not removable or networked.
                    if (OperatingSystem.IsWindows())
                    {
                        if (drive.DriveType != DriveType.Fixed)
                            continue;
                    }
                    else if (drive.DriveType is DriveType.Removable or DriveType.Network)
                    {
                        continue;
                    }

                    var root = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (excludedRoot != null && root.Equals(excludedRoot, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (drive.AvailableFreeSpace >= requiredWithBuffer)
                        candidates.Add(drive.Name);
                }
                catch (Exception ex)
                {
                    _logger.Information(ex, "Skipping drive while searching for free space: {Drive}", drive.Name);
                }
            }
        }
        catch (Exception ex)
        {
            // Ignore errors during drive enumeration
            _logger.Information(ex, "Failed to enumerate drives while searching for free space.");
        }

        return candidates;
    }

    /// <summary>
    /// Stops monitoring and releases all resources used by the service.
    /// </summary>
    public void Dispose()
    {
        try
        {
            StopMonitoring();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Error while disposing the disk monitor");
        }
        finally
        {
            GC.SuppressFinalize(this);
        }
    }
}