using System.Diagnostics;
using System.Runtime.Versioning;

namespace XISOStudio.Services;

/// <summary>
/// Windows performance-counter implementation of <see cref="IDiskSpeedCounter" />.
/// The type is only loaded on Windows: <see cref="DiskMonitorService" /> calls
/// <see cref="TryCreate" /> from a guarded, non-inlined method so macOS and Linux never
/// resolve <c>System.Diagnostics.PerformanceCounter</c>, which is not available there.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDiskSpeedCounter : IDiskSpeedCounter
{
    private readonly PerformanceCounter _readCounter;
    private readonly PerformanceCounter _writeCounter;

    private WindowsDiskSpeedCounter(PerformanceCounter readCounter, PerformanceCounter writeCounter)
    {
        _readCounter = readCounter;
        _writeCounter = writeCounter;
    }

    /// <summary>
    /// Creates the read and write counters for a LogicalDisk instance. The caller must be
    /// on Windows and must already have verified that the drive is a local fixed drive.
    /// </summary>
    /// <param name="instanceName">Performance-counter instance name, for example <c>C:</c>.</param>
    /// <param name="statusMessage">Reason the counters are unavailable, or <c>null</c> on success.</param>
    /// <returns>The initialized counters, or <c>null</c> when the counters are unavailable.</returns>
    internal static IDiskSpeedCounter? TryCreate(string instanceName, out string? statusMessage)
    {
        statusMessage = null;

        if (!PerformanceCounterCategory.Exists("LogicalDisk"))
        {
            statusMessage = "Disk speed monitoring unavailable - performance counters disabled";
            return null;
        }

        if (!PerformanceCounterCategory.InstanceExists(instanceName, "LogicalDisk"))
        {
            statusMessage = $"Disk speed monitoring unavailable for drive {instanceName}";
            return null;
        }

        var readCounter = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", instanceName, true);
        PerformanceCounter? writeCounter = null;
        var succeeded = false;
        try
        {
            readCounter.NextValue(); // Prime the counter

            writeCounter = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", instanceName, true);
            writeCounter.NextValue(); // Prime the counter

            var counters = new WindowsDiskSpeedCounter(readCounter, writeCounter);
            succeeded = true;
            return counters;
        }
        finally
        {
            // Only dispose on failure; on success the counters are owned by the result.
            if (!succeeded)
            {
                writeCounter?.Dispose();
                readCounter.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public float GetReadBytesPerSecond()
    {
        return _readCounter.NextValue();
    }

    /// <inheritdoc />
    public float GetWriteBytesPerSecond()
    {
        return _writeCounter.NextValue();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _readCounter.Dispose();
        _writeCounter.Dispose();
    }
}
