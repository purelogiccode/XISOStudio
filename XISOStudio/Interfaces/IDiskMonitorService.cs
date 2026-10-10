namespace XISOStudio.Interfaces;

/// <summary>
/// Monitors disk read/write speed and reports available free space for local and network drives.
/// </summary>
public interface IDiskMonitorService
{
    /// <summary>Gets the drive letter currently being monitored, or <c>null</c> when monitoring is not active.</summary>
    string? CurrentDriveLetter { get; }

    /// <summary>Gets the reason monitoring is unavailable, or <c>null</c> when monitoring is active.</summary>
    string? StatusMessage { get; }

    /// <summary>Starts monitoring read/write speed for the drive containing <paramref name="path" />.</summary>
    /// <param name="path">A path on the drive to monitor; network paths are not monitored.</param>
    void StartMonitoring(string? path);

    /// <summary>Stops monitoring and releases the underlying performance counters.</summary>
    void StopMonitoring();

    /// <summary>
    /// Gets the current read speed formatted for display (for example "12.5 MB/s"),
    /// or "N/A" when monitoring is unavailable.
    /// </summary>
    /// <returns>The formatted read speed, or "N/A" when monitoring is unavailable.</returns>
    string GetCurrentReadSpeedFormatted();

    /// <summary>
    /// Gets the current write speed formatted for display (for example "12.5 MB/s"),
    /// or "N/A" when monitoring is unavailable.
    /// </summary>
    /// <returns>The formatted write speed, or "N/A" when monitoring is unavailable.</returns>
    string GetCurrentWriteSpeedFormatted();

    /// <summary>
    /// Gets the available free space in bytes on the drive containing <paramref name="path" />,
    /// or 0 when it cannot be determined.
    /// </summary>
    /// <param name="path">A path on the drive to query.</param>
    /// <returns>The available free space in bytes, or 0 when it cannot be determined.</returns>
    long GetAvailableFreeSpace(string? path);

    /// <summary>
    /// Finds a local drive with enough free space for <paramref name="requiredBytes" />,
    /// including a safety buffer.
    /// </summary>
    /// <param name="requiredBytes">Number of bytes that must be available.</param>
    /// <param name="excludeDrive">Optional drive root to exclude from the search.</param>
    /// <returns>The root path of a suitable drive, or <c>null</c> when none was found.</returns>
    string? FindDriveWithFreeSpace(long requiredBytes, string? excludeDrive = null);

    /// <summary>
    /// Finds every local drive with enough free space for <paramref name="requiredBytes" />,
    /// including a safety buffer, so callers can fall back to the next drive when a
    /// candidate turns out to be unwritable.
    /// </summary>
    /// <param name="requiredBytes">Number of bytes that must be available.</param>
    /// <param name="excludeDrive">Optional drive root to exclude from the search.</param>
    /// <returns>The root paths of the suitable drives, ordered by drive enumeration; empty when none was found.</returns>
    IReadOnlyList<string> FindDrivesWithFreeSpace(long requiredBytes, string? excludeDrive = null);
}