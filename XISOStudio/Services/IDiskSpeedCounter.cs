namespace XISOStudio.Services;

/// <summary>
/// Platform abstraction for the disk read/write speed counters. Only the Windows
/// implementation references <c>System.Diagnostics.PerformanceCounter</c>, so the
/// cross-platform <see cref="DiskMonitorService" /> never needs to load that assembly
/// on macOS or Linux.
/// </summary>
internal interface IDiskSpeedCounter : IDisposable
{
    /// <summary>Gets the current disk read throughput in bytes per second.</summary>
    /// <returns>The read throughput reported by the platform counter.</returns>
    float GetReadBytesPerSecond();

    /// <summary>Gets the current disk write throughput in bytes per second.</summary>
    /// <returns>The write throughput reported by the platform counter.</returns>
    float GetWriteBytesPerSecond();
}
