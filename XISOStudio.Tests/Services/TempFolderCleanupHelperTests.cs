using XISOStudio.Services;
using Serilog.Events;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests retry-based temp directory deletion and orphaned work directory discovery in <c>TempFolderCleanupHelper</c>.</summary>
public class TempFolderCleanupHelperTests : IDisposable
{
    private readonly List<string> _tempRoots = [];

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch
            {
                // ignored
            }
        }

        GC.SuppressFinalize(this);
    }

    private string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CleanupTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    private static string CreateWorkDirectory(string root, string parentName, DateTime lastWriteUtc)
    {
        var directory = Path.Combine(root, parentName, Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        Directory.SetLastWriteTimeUtc(directory, lastWriteUtc);
        return directory;
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncNonExistentDirectoryDoesNotThrow()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"NonExistent_Test_{Guid.NewGuid()}");
        var logger = new TestLogger();

        var exception = await Record.ExceptionAsync(() =>
            TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(nonExistentPath, 3, 100, logger.Logger));

        Assert.Null(exception);
        Assert.DoesNotContain(logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncExistingEmptyDirectoryGetsDeleted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"XISOStudio_Test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        var logger = new TestLogger();

        try
        {
            Assert.True(Directory.Exists(tempDir));

            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(tempDir, 3, 100, logger.Logger);

            Assert.False(Directory.Exists(tempDir));
            Assert.True(logger.HasMessage("Successfully deleted"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncNullLoggerDoesNotThrow()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), $"NonExistent_Test_{Guid.NewGuid()}");

        var exception = await Record.ExceptionAsync(() =>
            TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(nonExistentPath, 3, 100, null));

        Assert.Null(exception);
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncExistingDirectoryCallsLogSuccess()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"XISOStudio_Test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        var logger = new TestLogger();

        try
        {
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(tempDir, 3, 100, logger.Logger);

            Assert.True(logger.HasMessage("Successfully deleted temp folder:"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncLockedDirectoryDoesNotThrowAndLogsWarning()
    {
        // On Unix an open file does not prevent directory deletion, so the locked-folder
        // retry path (Windows file-locking semantics) cannot be exercised there.
        if (!OperatingSystem.IsWindows()) return;

        var tempDir = Path.Combine(Path.GetTempPath(), $"XISOStudio_Test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        var lockedFile = Path.Combine(tempDir, "locked.txt");
        await File.WriteAllTextAsync(lockedFile, "locked");
        var logger = new TestLogger();

        await using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exception = await Record.ExceptionAsync(() =>
                TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(tempDir, 2, 10, logger.Logger));

            Assert.Null(exception);
            Assert.True(logger.HasMessage(LogEventLevel.Warning, "Could not delete"));
        }

        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncCanceledTokenDoesNotThrowOperationCanceled()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"XISOStudio_Test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        var lockedFile = Path.Combine(tempDir, "locked.txt");
        await File.WriteAllTextAsync(lockedFile, "locked");
        var logger = new TestLogger();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Cleanup runs from finally blocks: a canceled token must stop the retry loop
            // without throwing and masking the original operation error.
            var exception = await Record.ExceptionAsync(() =>
                TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(tempDir, 3, 10, logger.Logger, cts.Token));

            Assert.Null(exception);
        }

        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
    }

    #region FindOrphanedWorkDirectories Tests

    [Fact]
    public void FindOrphanedWorkDirectoriesOldDirectoryWithUtcNowReturnsIt()
    {
        var root = CreateTempRoot();
        var utcNow = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var stale = CreateWorkDirectory(root, "XISOStudio_Extract", utcNow.AddHours(-24));
        var logger = new TestLogger();

        var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([root], utcNow, logger.Logger);

        Assert.Contains(found, path => path.Equals(stale, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesRecentDirectoryWithUtcNowDoesNotReturnIt()
    {
        var root = CreateTempRoot();
        var utcNow = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var fresh = CreateWorkDirectory(root, "XISOStudio_Extract", utcNow.AddHours(-1));
        var logger = new TestLogger();

        var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([root], utcNow, logger.Logger);

        Assert.DoesNotContain(found, path => path.Equals(fresh, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesDirectoryExactlyAtMinimumAgeIsReturned()
    {
        var root = CreateTempRoot();
        var utcNow = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var boundary = CreateWorkDirectory(root, "XISOStudio_Extract", utcNow.AddHours(-6));

        var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([root], utcNow, null);

        Assert.Contains(found, path => path.Equals(boundary, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesFutureUtcNowMakesFreshDirectoryOrphaned()
    {
        var root = CreateTempRoot();
        var utcNow = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var directory = CreateWorkDirectory(root, "XISOStudio_Test", utcNow);

        var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([root], utcNow.AddHours(24), null);

        Assert.Contains(found, path => path.Equals(directory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesPastUtcNowExcludesOldDirectory()
    {
        var root = CreateTempRoot();
        var utcNow = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var directory = CreateWorkDirectory(root, "XISOStudio_Test", utcNow.AddHours(-24));

        var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([root], utcNow.AddHours(-48), null);

        Assert.DoesNotContain(found, path => path.Equals(directory, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("XISOStudio_Extract")]
    [InlineData("XISOStudio_Test")]
    [InlineData("ImageExplorer")]
    [InlineData("ImageExplorer_DragDrop")]
    public void FindOrphanedWorkDirectoriesScansKnownParentPatterns(string parentName)
    {
        var root = CreateTempRoot();
        var utcNow = DateTime.UtcNow;
        var stale = CreateWorkDirectory(root, parentName, utcNow.AddHours(-12));

        var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([root], utcNow, null);

        Assert.Contains(found, path => path.Equals(stale, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesNonGuidChildIsIgnored()
    {
        var root = CreateTempRoot();
        var utcNow = DateTime.UtcNow;
        var child = Path.Combine(root, "XISOStudio_Extract", "not-a-guid");
        Directory.CreateDirectory(child);
        Directory.SetLastWriteTimeUtc(child, utcNow.AddHours(-24));

        var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([root], utcNow, null);

        Assert.DoesNotContain(found, path => path.Equals(child, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesGrandchildGuidDirectoryIsIgnored()
    {
        var root = CreateTempRoot();
        var utcNow = DateTime.UtcNow;
        var grandchild = Path.Combine(root, "XISOStudio_Extract", "sub", Guid.NewGuid().ToString());
        Directory.CreateDirectory(grandchild);
        Directory.SetLastWriteTimeUtc(grandchild, utcNow.AddHours(-24));

        var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([root], utcNow, null);

        Assert.DoesNotContain(found, path => path.Equals(grandchild, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesNestedWorkParentIsIgnored()
    {
        var root = CreateTempRoot();
        var utcNow = DateTime.UtcNow;
        var stale = Path.Combine(root, "UserData", "XISOStudio_Extract", Guid.NewGuid().ToString());
        Directory.CreateDirectory(stale);
        Directory.SetLastWriteTimeUtc(stale, utcNow.AddHours(-24));

        var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([root], utcNow, null);

        Assert.DoesNotContain(found, path => path.Equals(stale, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesGuidDirectoryDirectlyUnderRootIsIgnored()
    {
        var root = CreateTempRoot();
        var utcNow = DateTime.UtcNow;
        var stray = Path.Combine(root, Guid.NewGuid().ToString());
        Directory.CreateDirectory(stray);
        Directory.SetLastWriteTimeUtc(stray, utcNow.AddHours(-24));

        var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([root], utcNow, null);

        Assert.DoesNotContain(found, path => path.Equals(stray, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesMissingRootLogsWarningAndReturnsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"Missing_{Guid.NewGuid():N}");
        var logger = new TestLogger();

        var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([missing], DateTime.UtcNow, logger.Logger);

        Assert.Empty(found);
        Assert.True(logger.HasMessage(LogEventLevel.Warning, "Error enumerating temp folders"));
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesMissingRootWithNullLoggerDoesNotThrow()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"Missing_{Guid.NewGuid():N}");

        var exception = Record.Exception(() =>
            TempFolderCleanupHelper.FindOrphanedWorkDirectories([missing], DateTime.UtcNow, null));

        Assert.Null(exception);
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesInaccessibleRootIsSkippedWithoutWarning()
    {
        // Dropping read permission portably is Unix-only, and the reported failures came
        // from Linux mounts (/root, /.snapshots, /sys/kernel/tracing) the user cannot read.
        if (OperatingSystem.IsWindows()) return;

        var root = CreateTempRoot();
        var logger = new TestLogger();

        File.SetUnixFileMode(root, UnixFileMode.None);
        try
        {
            // Root bypasses permission checks, so skip when the OS does not enforce them.
            try
            {
                _ = Directory.EnumerateDirectories(root, "XISOStudio_*", SearchOption.TopDirectoryOnly).Any();
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // Expected: the root is inaccessible now.
            }

            var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([root], DateTime.UtcNow, logger.Logger);

            Assert.Empty(found);
            Assert.True(logger.HasMessage(LogEventLevel.Debug, "Skipping inaccessible temp root"));
            Assert.DoesNotContain(logger.Events, e => e.Level >= LogEventLevel.Warning);
        }
        finally
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesMultipleRootsReturnsAllStaleDirectories()
    {
        var root1 = CreateTempRoot();
        var root2 = CreateTempRoot();
        var utcNow = DateTime.UtcNow;
        var stale1 = CreateWorkDirectory(root1, "XISOStudio_Extract", utcNow.AddHours(-12));
        var stale2 = CreateWorkDirectory(root2, "ImageExplorer", utcNow.AddHours(-12));

        var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([root1, root2], utcNow, null);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, path => path.Equals(stale1, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(found, path => path.Equals(stale2, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesEmptyRootsReturnsEmpty()
    {
        var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([], DateTime.UtcNow, null);

        Assert.Empty(found);
    }

    [Fact]
    public void FindOrphanedWorkDirectoriesReturnsOnlyStaleGuidFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CleanupScan_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var logger = new TestLogger();

        try
        {
            var stale = Path.Combine(root, "XISOStudio_Extract", Guid.NewGuid().ToString());
            var fresh = Path.Combine(root, "XISOStudio_Test", Guid.NewGuid().ToString("N"));
            var notGuid = Path.Combine(root, "XISOStudio_Extract", "not-a-guid");
            var userFolder = Path.Combine(root, "XISOStudio_backup");
            Directory.CreateDirectory(stale);
            Directory.CreateDirectory(fresh);
            Directory.CreateDirectory(notGuid);
            Directory.CreateDirectory(userFolder);

            Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-24));
            Directory.SetLastWriteTimeUtc(notGuid, DateTime.UtcNow.AddHours(-24));
            Directory.SetLastWriteTimeUtc(userFolder, DateTime.UtcNow.AddHours(-24));

            var found = TempFolderCleanupHelper.FindOrphanedWorkDirectories([root], DateTime.UtcNow, logger.Logger);

            Assert.Contains(found, path => path.Equals(stale, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(found, path => path.Equals(fresh, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(found, path => path.Equals(notGuid, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(found, path => path.Equals(userFolder, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    #endregion

    #region TryDeleteDirectoryWithRetryAsync Extended Tests

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncDeletedDirectoryLogsDirectoryName()
    {
        var root = CreateTempRoot();
        var directoryName = Path.GetFileName(root);
        var logger = new TestLogger();

        await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(root, 3, 10, logger.Logger);

        Assert.False(Directory.Exists(root));
        Assert.True(logger.HasMessage(directoryName));
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncNestedDirectoriesAreDeletedRecursively()
    {
        var root = CreateTempRoot();
        var nested = Path.Combine(root, "a", "b");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "file.txt"), "data");
        var logger = new TestLogger();

        await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(root, 3, 10, logger.Logger);

        Assert.False(Directory.Exists(root));
        Assert.True(logger.HasMessage("Successfully deleted"));
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncMaxRetriesZeroLogsCouldNotDelete()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"Missing_{Guid.NewGuid():N}");
        var logger = new TestLogger();

        await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(missing, 0, 10, logger.Logger);

        Assert.True(logger.HasMessage(LogEventLevel.Warning, "Could not delete"));
        Assert.True(logger.HasMessage(LogEventLevel.Warning, "after 0 attempts"));
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncSingleRetryLockedDirectoryLogsAttemptCount()
    {
        // Locked-folder retry behavior is Windows-specific (see above).
        if (!OperatingSystem.IsWindows()) return;

        var root = CreateTempRoot();
        var lockedFile = Path.Combine(root, "locked.txt");
        await File.WriteAllTextAsync(lockedFile, "locked");
        var logger = new TestLogger();

        await using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(root, 1, 10, logger.Logger);
        }

        Assert.True(Directory.Exists(root));
        Assert.True(logger.HasMessage(LogEventLevel.Warning, "after 1 attempts"));
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncLockedDirectoryLogsRetryWarning()
    {
        // Locked-folder retry behavior is Windows-specific (see above).
        if (!OperatingSystem.IsWindows()) return;

        var root = CreateTempRoot();
        var lockedFile = Path.Combine(root, "locked.txt");
        await File.WriteAllTextAsync(lockedFile, "locked");
        var logger = new TestLogger();

        await using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(root, 2, 5, logger.Logger);
        }

        Assert.True(logger.HasMessage(LogEventLevel.Warning, "Deletion attempt 1/2 failed"));
        Assert.True(logger.HasMessage(LogEventLevel.Warning, "files locked"));
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncLockedDirectoryPreservesFileAfterFailure()
    {
        // Locked-folder retry behavior is Windows-specific (see above).
        if (!OperatingSystem.IsWindows()) return;

        var root = CreateTempRoot();
        var lockedFile = Path.Combine(root, "locked.txt");
        await File.WriteAllTextAsync(lockedFile, "locked");
        var logger = new TestLogger();

        await using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(root, 2, 5, logger.Logger);
        }

        Assert.True(File.Exists(lockedFile));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncLockedDirectoryWithNullLoggerDoesNotThrow()
    {
        var root = CreateTempRoot();
        var lockedFile = Path.Combine(root, "locked.txt");
        await File.WriteAllTextAsync(lockedFile, "locked");

        var exception = await Record.ExceptionAsync(async () =>
        {
            await using var stream = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(root, 2, 5, null);
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncCanceledDuringRetryDelayLogsCancellation()
    {
        // The cancellation path only runs while retrying a locked folder (Windows-specific).
        if (!OperatingSystem.IsWindows()) return;

        var root = CreateTempRoot();
        var lockedFile = Path.Combine(root, "locked.txt");
        await File.WriteAllTextAsync(lockedFile, "locked");
        var logger = new TestLogger();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(root, 3, 10, logger.Logger, cts.Token);
        }

        Assert.True(logger.HasMessage(LogEventLevel.Debug, "was canceled"));
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncCanceledTokenWithUnlockedDirectoryStillDeletes()
    {
        var root = CreateTempRoot();
        var logger = new TestLogger();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // The token only affects the retry delay, so an unlocked directory is deleted on the first attempt.
        await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(root, 3, 10, logger.Logger, cts.Token);

        Assert.False(Directory.Exists(root));
        Assert.True(logger.HasMessage("Successfully deleted"));
    }

    [Fact]
    public async Task TryDeleteDirectoryWithRetryAsyncReadOnlyFileLogsAccessDeniedOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = CreateTempRoot();
        var readOnlyFile = Path.Combine(root, "readonly.txt");
        await File.WriteAllTextAsync(readOnlyFile, "data");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
        var logger = new TestLogger();

        try
        {
            await TempFolderCleanupHelper.TryDeleteDirectoryWithRetryAsync(root, 2, 5, logger.Logger);

            Assert.True(logger.HasMessage(LogEventLevel.Warning, "access denied"));
            Assert.True(File.Exists(readOnlyFile));
        }
        finally
        {
            File.SetAttributes(readOnlyFile, FileAttributes.Normal);
        }
    }

    #endregion

    #region CleanupTempFoldersAsync Tests

    [Fact]
    public async Task CleanupTempFoldersAsyncPreCanceledTokenThrowsOperationCanceled()
    {
        var logger = new TestLogger();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TempFolderCleanupHelper.CleanupTempFoldersAsync(logger.Logger, cts.Token));
    }

    [Fact]
    public async Task CleanupTempFoldersAsyncPreCanceledTokenLogsCancellation()
    {
        var logger = new TestLogger();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TempFolderCleanupHelper.CleanupTempFoldersAsync(logger.Logger, cts.Token));

        Assert.True(logger.HasMessage(LogEventLevel.Debug, "Temp folder cleanup was canceled"));
    }

    #endregion
}