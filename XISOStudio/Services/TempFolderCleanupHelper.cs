using Serilog;

namespace XISOStudio.Services;

/// <summary>
/// Cleans up orphaned XISOStudio work folders on the system temp path and fixed drives,
/// retrying deletions of locked files.
/// </summary>
public static class TempFolderCleanupHelper
{
    /// <summary>
    /// Deletes a directory with retry logic for locked files
    /// </summary>
    /// <param name="directoryPath">Directory to delete.</param>
    /// <param name="maxRetries">Maximum number of deletion attempts.</param>
    /// <param name="delayMs">Delay between attempts, in milliseconds.</param>
    /// <param name="logger">Optional logger that receives cleanup progress; may be <c>null</c>.</param>
    /// <param name="cancellationToken">Cancellation token for the retry delays.</param>
    public static async Task TryDeleteDirectoryWithRetryAsync(string directoryPath, int maxRetries, int delayMs,
        ILogger? logger, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (!Directory.Exists(directoryPath)) return;

                Directory.Delete(directoryPath, true);
                logger?.Information("Successfully deleted temp folder: {TempFolder}", Path.GetFileName(directoryPath));
                return;
            }
            catch (OperationCanceledException)
            {
                // Cleanup runs from finally blocks; cancellation must not mask the
                // original operation error.
                logger?.Debug("Deletion of '{TempFolder}' was canceled", Path.GetFileName(directoryPath));
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The last attempt must not throw: cleanup runs from finally blocks and
                // a propagation here would mask the original operation error.
                if (attempt < maxRetries)
                {
                    var reason = ex is UnauthorizedAccessException ? "access denied" : "files locked";
                    logger?.Warning(ex,
                        "Deletion attempt {Attempt}/{MaxRetries} failed for '{TempFolder}' ({Reason}). Retrying...",
                        attempt, maxRetries, Path.GetFileName(directoryPath), reason);
                    try
                    {
                        await Task.Delay(delayMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        logger?.Debug("Deletion retry for '{TempFolder}' was canceled",
                            Path.GetFileName(directoryPath));
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Warning(ex, "Failed to delete '{TempFolder}'", Path.GetFileName(directoryPath));
                return;
            }
        }

        logger?.Warning("Could not delete '{TempFolder}' after {MaxRetries} attempts. Manual cleanup may be needed.",
            Path.GetFileName(directoryPath), maxRetries);
    }

    /// <summary>
    ///     Work folders are always created as <c>&lt;parent&gt;/&lt;guid&gt;</c>, where the
    ///     parent is an <c>XISOStudio_*</c> folder or one of the image-explorer work
    ///     folders. Cleanup only removes those GUID-named children, and only when they are
    ///     old enough that no running instance can still be using them. Unrelated user
    ///     folders with the same name and another instance's fresh work folders are
    ///     therefore left alone.
    /// </summary>
    private static readonly TimeSpan MinimumOrphanAge = TimeSpan.FromHours(6);

    /// <summary>Names of the folders that contain GUID-named work directories.</summary>
    private static readonly string[] WorkDirectoryParentPatterns =
        ["XISOStudio_*", "ImageExplorer", "ImageExplorer_DragDrop"];

    /// <summary>
    ///     Finds stale GUID-named work folders under the given roots. Only GUID-named
    ///     children older than <see cref="MinimumOrphanAge" /> are returned; everything
    ///     else is considered user data or still in use.
    /// </summary>
    /// <param name="rootsToScan">Root folders scanned for stale work directories.</param>
    /// <param name="utcNow">Current UTC time used to compute the age of each candidate.</param>
    /// <param name="logger">Optional logger that receives scan diagnostics; may be <c>null</c>.</param>
    /// <returns>The list of stale GUID-named work directories found under the roots.</returns>
    internal static List<string> FindOrphanedWorkDirectories(IEnumerable<string> rootsToScan, DateTime utcNow,
        ILogger? logger)
    {
        var found = new List<string>();

        foreach (var root in rootsToScan)
        {
            foreach (var searchPattern in WorkDirectoryParentPatterns)
            {
                try
                {
                    foreach (var parent in Directory.EnumerateDirectories(root, searchPattern,
                                 SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            foreach (var child in Directory.EnumerateDirectories(parent))
                            {
                                if (!Guid.TryParse(Path.GetFileName(child), out _)) continue;

                                var lastWriteUtc = Directory.GetLastWriteTimeUtc(child);
                                if (utcNow - lastWriteUtc < MinimumOrphanAge) continue;

                                found.Add(child);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger?.Debug(ex, "Could not inspect temp folder '{ParentFolder}'", parent);
                        }
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    // Multi-user systems and Unix mounts expose roots the current user
                    // cannot read (/root, /.snapshots, /sys/kernel/tracing, ...). Skipping
                    // them is expected and must stay below Warning: the BugReportSink
                    // auto-reports warnings and an unreadable root is not a defect. Every
                    // pattern fails identically for such a root, so move on after the first.
                    logger?.Debug(ex, "Skipping inaccessible temp root {Root}", root);
                    break;
                }
                catch (Exception ex)
                {
                    logger?.Warning(ex, "Error enumerating temp folders on {Root}", root);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Cleans up orphaned XISOStudio work folders on all fixed drives
    /// </summary>
    /// <param name="logger">Logger that receives cleanup progress and failures.</param>
    /// <param name="cancellationToken">Cancellation token for the cleanup.</param>
    public static async Task CleanupTempFoldersAsync(ILogger logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Enumerating drives and directories can block for many seconds on idle or spinning
            // drives (DriveInfo.IsReady waits for the disk to respond), so always do it on a
            // background thread — callers may be on the UI thread.
            var directoriesToClean = await Task.Run(() =>
            {
                var rootsToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    rootsToScan.Add(Path.GetTempPath());
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "Could not resolve the system temp path for cleanup");
                }

                try
                {
                    // PathHelper falls back to this folder when the system temp filesystem
                    // is full, so orphaned work folders can live there too. Only scan it
                    // when it exists: a missing root would otherwise log a warning and be
                    // auto-reported by the bug-report sink.
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (!string.IsNullOrEmpty(localAppData))
                    {
                        var fallbackRoot = Path.Combine(localAppData, "XISOStudio");
                        if (Directory.Exists(fallbackRoot))
                            rootsToScan.Add(fallbackRoot);
                    }
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "Could not resolve the per-user temp fallback for cleanup");
                }

                try
                {
                    foreach (var drive in DriveInfo.GetDrives())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (drive is { IsReady: true, DriveType: DriveType.Fixed })
                            rootsToScan.Add(drive.Name);
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.Debug("Drive enumeration for temp folder cleanup was canceled");
                    throw;
                }
                catch (Exception ex)
                {
                    // Ignore drive enumeration errors
                    logger.Debug(ex, "Could not enumerate drives for temp folder cleanup");
                }

                return FindOrphanedWorkDirectories(rootsToScan, DateTime.UtcNow, logger);
            }, cancellationToken);

            foreach (var dir in directoriesToClean)
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.Information("Cleaning up orphaned temp folder: {TempFolder}", Path.GetFileName(dir));
                await TryDeleteDirectoryWithRetryAsync(dir, 3, 1000, logger, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            logger.Debug("Temp folder cleanup was canceled");
            throw;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Error cleaning up temporary folders");
        }
    }
}