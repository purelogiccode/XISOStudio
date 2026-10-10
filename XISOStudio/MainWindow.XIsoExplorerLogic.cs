using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Serilog;
using XISOStudio.Interfaces;
using XISOStudio.Models;
using XISOStudio.Services;

namespace XISOStudio;

/// <summary>Image explorer view: browsing, extracting, and dragging files out of an open image.</summary>
[SuppressMessage("ReSharper", "UnusedMember.Local",
    Justification =
        "XAML event handlers are resolved by the Avalonia markup compiler, which ReSharper does not link across partial class files.")]
[SuppressMessage("ReSharper", "UnusedParameter.Local",
    Justification = "Parameters are required by XAML event handler signatures (sender, event args).")]
public partial class MainWindow
{
    // Drag-drop state tracking
    /// <summary>Pointer position where the current drag gesture started.</summary>
    private Point _dragStartPoint;

    /// <summary>Pointer arguments captured when the drag gesture started.</summary>
    private PointerPressedEventArgs? _dragPointerArgs;

    /// <summary>Indicates that a drag operation is in progress.</summary>
    private bool _isDragging;

    /// <summary>Minimum pointer movement in pixels required to start a drag.</summary>
    private const double MinimumDragDistance = 4;

    /// <summary>Prompts for an Xbox image and opens it in the explorer.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void BrowseExplorerFile_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select an Xbox image to explore",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Xbox images")
                        { Patterns = Options },
                    new FilePickerFileType("Xbox ISO") { Patterns = OptionsArray },
                    new FilePickerFileType("Compressed ISO") { Patterns = OptionsArray0 },
                    new FilePickerFileType("ZAR archive") { Patterns = OptionsArray1 },
                    new FilePickerFileType("CHD image") { Patterns = OptionsArray2 },
                    new FilePickerFileType("All files") { Patterns = OptionsArray3 }
                }
            });

            if (files.Count == 0) return;

            var selectedPath = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(selectedPath)) return;

            ExplorerFilePathTextBox.Text = selectedPath;
            await InitializeExplorerAsync(selectedPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseExplorerFile_ClickAsync");
        }
    }

    /// <summary>Opens the image and loads its root directory, retiring any previous explorer.</summary>
    /// <param name="imagePath">Path of the image or archive to open.</param>
    private async Task InitializeExplorerAsync(string imagePath)
    {
        IImageExplorer? previous;
        lock (_explorerLock)
        {
            previous = _explorer;
            // Clear the reference before opening: if the factory throws, the field must
            // not keep pointing at the explorer that was just disposed.
            _explorer = null;
        }

        // Dispose the previous explorer only after any background copy-out using it finishes.
        RetireExplorer(previous);

        try
        {
            var explorer = ImageExplorerFactory.Open(imagePath, _logger);
            lock (_explorerLock)
            {
                _explorer = explorer;
            }

            await LoadDirectoryAsync("/");
        }
        catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex))
        {
            lock (_explorerLock)
            {
                _explorer = null;
            }

            // Invalid, missing, or unreadable user-selected files are expected conditions,
            // so they are logged below the automatic bug-report threshold.
            _logger.Information(ex, "The selected file is not a supported Xbox image: {Message}", ex.Message);
            ShowErrorSafe(
                "The selected file is not a valid Xbox or Xbox 360 image, or it cannot be read. " +
                $"Please select an Xbox ISO, CSO, ZAR, or CHD file.\n\nDetails: {ex.Message}");

            // The grid would otherwise keep showing the previous (now disposed) image.
            ExplorerDataGrid.ItemsSource = null;
            _currentInternalPath = "/";
            UpdateExplorerUiState();
        }
        catch (Exception ex)
        {
            lock (_explorerLock)
            {
                _explorer = null;
            }

            _logger.Error(ex, "Failed to read image: {Message}", ex.Message);
            ShowErrorSafe($"Failed to read image: {ex.Message}");

            // The grid would otherwise keep showing the previous (now disposed) image.
            ExplorerDataGrid.ItemsSource = null;
            _currentInternalPath = "/";
            UpdateExplorerUiState();
        }
    }

    /// <summary>
    ///     Runs <paramref name="action" /> on the current explorer while holding it in use,
    ///     so opening another image or closing the window cannot dispose it mid-operation.
    ///     The action runs on the thread pool because explorer reads (hunk decompression,
    ///     archive reads) are synchronous and can take seconds. Returns false when no
    ///     explorer is open.
    /// </summary>
    /// <param name="action">Action to run against the open explorer.</param>
    /// <param name="token">Token that cancels the queued work.</param>
    /// <returns><c>true</c> when the action ran; <c>false</c> when no explorer is open.</returns>
    private async Task<bool> UseExplorerAsync(Action<IImageExplorer> action, CancellationToken token)
    {
        await _explorerUseLock.WaitAsync(token);
        try
        {
            IImageExplorer? explorer;
            lock (_explorerLock)
            {
                explorer = _explorer;
            }

            if (explorer == null) return false;

            await Task.Run(() => action(explorer), token);
            return true;
        }
        finally
        {
            _explorerUseLock.Release();
        }
    }

    /// <summary>
    ///     Disposes <paramref name="explorer" /> after any in-flight copy-out that is still
    ///     using it has finished, without blocking the caller.
    /// </summary>
    /// <param name="explorer">Explorer to dispose, or <c>null</c> to do nothing.</param>
    private void RetireExplorer(IImageExplorer? explorer)
    {
        if (explorer == null) return;

        _ = Task.Run(async () =>
        {
            // Retirement must always complete, so it deliberately ignores cancellation.
            await _explorerUseLock.WaitAsync(CancellationToken.None);
            try
            {
                explorer.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Error disposing the image explorer");
            }
            finally
            {
                _explorerUseLock.Release();
            }
        }, CancellationToken.None);
    }

    /// <summary>Lists a directory of the open image and binds it to the explorer grid.</summary>
    /// <param name="internalPath">Directory path within the image (<c>"/"</c> for the root).</param>
    private async Task LoadDirectoryAsync(string internalPath)
    {
        CancellationToken token;
        try
        {
            token = _explorerCts.Token;
        }
        catch (ObjectDisposedException ex)
        {
            // The window is shutting down.
            _logger.Debug(ex, "Skipping directory load because the window is shutting down");
            return;
        }

        IReadOnlyList<ImageEntry> entries;
        try
        {
            // Explorer reads (hunk decompression, archive reads) can take seconds, so run
            // them off the UI thread while holding the explorer in use; opening another
            // image or closing the window cannot dispose it mid-read.
            IReadOnlyList<ImageEntry>? listed = null;
            if (!await UseExplorerAsync(explorer => listed = explorer.ListChildren(internalPath), token))
            {
                return;
            }

            entries = listed ?? [];
        }
        catch (OperationCanceledException ex)
        {
            _logger.Debug(ex, "Loading directory {InternalPath} was canceled", internalPath);
            return;
        }
        catch (Exception ex) when (ImageErrorClassifier.IsUserInputImageError(ex))
        {
            // A corrupt or unreadable image is an expected condition, not an application defect.
            _logger.Information(ex, "The image could not be read while loading {InternalPath}", internalPath);
            ShowErrorSafe($"The image appears to be corrupt or unreadable. Details: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error loading directory: {InternalPath}", internalPath);
            ShowErrorSafe($"Error loading directory: {ex.Message}");
            return;
        }

        var uiItems = entries.Select(static e => new XisoExplorerItem
            {
                Name = e.Name,
                IsDirectory = e.IsDirectory,
                SizeFormatted = e.IsDirectory ? "" : Formatter.FormatBytes(e.Size),
                Entry = e
            }).OrderByDescending(static i => i.IsDirectory)
            .ThenBy(static i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ExplorerDataGrid.ItemsSource = uiItems;
        _currentInternalPath = ImagePaths.Normalize(internalPath);
        UpdateExplorerUiState();
    }

    /// <summary>Updates the explorer path text and the enabled state of the up button.</summary>
    private void UpdateExplorerUiState()
    {
        ExplorerUpButton.IsEnabled = !string.Equals(_currentInternalPath, "/", StringComparison.Ordinal);
        ExplorerPathTextBlock.Text = _currentInternalPath;
    }

    /// <summary>Opens the double-clicked directory or extracts and opens the selected file.</summary>
    /// <param name="sender">The grid that raised the event.</param>
    /// <param name="e">The tap event data.</param>
    private async void ExplorerDataGrid_DoubleTappedAsync(object? sender, TappedEventArgs e)
    {
        try
        {
            if (ExplorerDataGrid.SelectedItem is not XisoExplorerItem item) return;

            if (item.IsDirectory)
            {
                await LoadDirectoryAsync(item.Entry.FullPath);
            }
            else
            {
                // Open the file with the default application
                await OpenFileFromImageAsync(item.Entry, item.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in method ExplorerDataGrid_DoubleTappedAsync");
        }
    }

    /// <summary>Extracts a file to a temporary folder and opens it with the default application.</summary>
    /// <param name="entry">Entry describing the file inside the image.</param>
    /// <param name="fileName">Name used for the extracted temporary file.</param>
    private async Task OpenFileFromImageAsync(ImageEntry entry, string fileName)
    {
        CancellationToken token;
        try
        {
            // Capture the token before queueing the background work: the window may be
            // closed (and the source disposed) before the task actually starts.
            token = _explorerCts.Token;
        }
        catch (ObjectDisposedException ex)
        {
            // The window is shutting down.
            _logger.Debug(ex, "Skipping file extraction because the window is shutting down");
            return;
        }

        await Task.Run(async () =>
        {
            string? tempFolder = null;
            var extracted = false;
            try
            {
                tempFolder = ResolveExplorerTempDirectory(entry.Size, "ImageExplorer");
                Directory.CreateDirectory(tempFolder);
                var tempPath = Path.Combine(tempFolder, fileName);

                // Extract file to temp location while the explorer is held in use.
                if (!await UseExplorerAsync(explorer => explorer.CopyOut(entry.FullPath, tempPath), token))
                {
                    return;
                }

                extracted = true;

                // Open with default application on UI thread
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        using var process = Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to open extracted file: {TempPath}", tempPath);
                        ShowErrorSafe($"Failed to open file: {ex.Message}");
                    }
                });

                // Schedule delayed cleanup of the extracted file after the viewer had a
                // chance to open it.
                ScheduleTempFolderCleanup(tempFolder, TimeSpan.FromSeconds(30));
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("Extracting and opening file from image was canceled: {FileName}", fileName);
            }
            catch (Exception ex) when (ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                // Disk-full, access-denied, and unreadable-path failures are environmental,
                // not defects, so they are logged below the automatic bug-report threshold.
                _logger.Information(ex, "Could not extract and open the file from the image: {FileName}", fileName);
                ShowErrorSafe($"Could not extract and open the file: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to extract and open file from image: {FileName}", fileName);
                ShowErrorSafe($"Failed to extract and open file: {ex.Message}");
            }
            finally
            {
                // A partial or canceled extraction leaves files no other process can use;
                // remove them so they do not accumulate in the temp folder.
                if (!extracted && tempFolder != null)
                {
                    ScheduleTempFolderCleanup(tempFolder, TimeSpan.FromSeconds(5));
                }
            }
        }, token);
    }

    /// <summary>
    ///     Deletes a temporary extraction folder after a delay, so a process that is still
    ///     reading the extracted files (file viewer, drag-and-drop target) is not disturbed.
    ///     Failures are logged and left for the startup cleanup.
    /// </summary>
    /// <param name="tempFolder">Temporary folder to delete.</param>
    /// <param name="delay">Delay before the folder is deleted.</param>
    private void ScheduleTempFolderCleanup(string tempFolder, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not delete explorer temp folder: {TempFolder}", tempFolder);
            }
        }, CancellationToken.None);
    }

    /// <summary>Records the pointer position that may start a drag gesture.</summary>
    /// <param name="sender">The grid that raised the event.</param>
    /// <param name="e">The pointer event data.</param>
    private void ExplorerDataGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragPointerArgs = e;
    }

    /// <summary>Starts a file drag once the pointer moved far enough with the left button held.</summary>
    /// <param name="sender">The grid that raised the event.</param>
    /// <param name="e">The pointer event data.</param>
    private async void ExplorerDataGrid_PointerMovedAsync(object? sender, PointerEventArgs e)
    {
        try
        {
            if (_isDragging || _dragPointerArgs is null) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var currentPosition = e.GetPosition(null);
            var diff = _dragStartPoint - currentPosition;

            // Check if the pointer has moved enough to start a drag operation
            if (Math.Abs(diff.X) < MinimumDragDistance && Math.Abs(diff.Y) < MinimumDragDistance) return;

            // Get selected file items (not directories)
            var selectedItems = ExplorerDataGrid.SelectedItems?
                .Cast<XisoExplorerItem>()
                .Where(static i => !i.IsDirectory)
                .ToList();

            if (selectedItems is not { Count: > 0 }) return;

            CancellationToken token;
            try
            {
                token = _explorerCts.Token;
            }
            catch (ObjectDisposedException ex)
            {
                // The window is shutting down.
                _logger.Debug(ex, "Skipping drag operation because the window is shutting down");
                return;
            }

            string? tempFolder = null;
            var dragStarted = false;
            try
            {
                _isDragging = true;
                // Extract files to temp folder for drag operation
                var totalSize = selectedItems.Sum(static i => i.Entry.Size);
                var folder = ResolveExplorerTempDirectory(totalSize, "ImageExplorer_DragDrop");
                tempFolder = folder;
                Directory.CreateDirectory(folder);

                var tempFiles = new List<string>();

                // Perform extraction while the explorer is held in use.
                var copied = await UseExplorerAsync(explorer =>
                {
                    foreach (var item in selectedItems)
                    {
                        var tempPath = Path.Combine(folder, item.Name);
                        explorer.CopyOut(item.Entry.FullPath, tempPath);
                        tempFiles.Add(tempPath);
                    }
                }, token);

                if (!copied) return;

                // Start drag operation with the file drop list
                var topLevel = GetTopLevel(this);
                if (topLevel is not null && tempFiles.Count > 0)
                {
                    var data = new DataTransfer();
                    var addedCount = 0;
                    foreach (var tempFile in tempFiles)
                    {
                        var storageItem = await topLevel.StorageProvider.TryGetFileFromPathAsync(tempFile);
                        if (storageItem is not null)
                        {
                            data.Add(DataTransferItem.CreateFile(storageItem));
                            addedCount++;
                        }
                    }

                    if (addedCount > 0)
                    {
                        dragStarted = true;
                        await DragDrop.DoDragDropAsync(_dragPointerArgs, data, DragDropEffects.Copy);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("Drag operation canceled");
            }
            catch (Exception ex) when (ImageErrorClassifier.IsEnvironmentalIoError(ex))
            {
                // Disk-full, access-denied, and unreadable-path failures are environmental,
                // not defects, so they are logged below the automatic bug-report threshold.
                _logger.Information(ex, "Could not prepare files for drag operation: {Message}", ex.Message);
                ShowErrorSafe($"Could not prepare files for drag operation: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to prepare files for drag operation");
                ShowErrorSafe($"Failed to prepare files for drag operation: {ex.Message}");
            }
            finally
            {
                // The drop target (for example File Explorer) copies the dropped files
                // asynchronously after the drop returns, so deleting immediately can
                // truncate the copy. Keep the files for a while and let the startup
                // cleanup collect anything left behind after a crash.
                if (tempFolder != null)
                {
                    ScheduleTempFolderCleanup(tempFolder,
                        dragStarted ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));
                }

                _isDragging = false;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Drag operation failed");
            ShowErrorSafe($"Drag operation failed: {ex.Message}");
        }
    }

    /// <summary>File picker patterns matching every file.</summary>
    private static readonly string[] OptionsArray3 = new[] { "*" };

    /// <summary>File picker patterns matching CHD images.</summary>
    private static readonly string[] OptionsArray2 = new[] { "*.chd" };

    /// <summary>File picker patterns matching ZAR archives.</summary>
    private static readonly string[] OptionsArray1 = new[] { "*.zar" };

    /// <summary>File picker patterns matching CSO images.</summary>
    private static readonly string[] OptionsArray0 = new[] { "*.cso" };

    /// <summary>File picker patterns matching ISO images.</summary>
    private static readonly string[] OptionsArray = new[] { "*.iso" };

    /// <summary>File picker patterns matching all supported Xbox images.</summary>
    private static readonly string[] Options = new[] { "*.iso", "*.cso", "*.zar", "*.chd" };

    /// <summary>Navigates to the parent directory of the explorer view.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void ExplorerUpButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (string.Equals(_currentInternalPath, "/", StringComparison.Ordinal)) return;

            await LoadDirectoryAsync(ImagePaths.GetParent(_currentInternalPath));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in method ExplorerUpButton_Click");
        }
    }

    /// <summary>Chooses and creates a temporary folder with enough free space for an extraction.</summary>
    /// <param name="requiredSize">Number of bytes the extraction needs.</param>
    /// <param name="tempSubfolder">Subfolder name used under the chosen drive's temp path.</param>
    /// <returns>Path of the created temporary folder.</returns>
    private string ResolveExplorerTempDirectory(long requiredSize, string tempSubfolder)
    {
        // Reuse the shared resolver: it applies the same safety buffer and drive selection as
        // the batch pipeline, never falls back to a drive known to be full, and creates the
        // folder so an unwritable drive is skipped before the extraction starts.
        return PathHelper.ResolveTempDirectory(requiredSize, tempSubfolder, _diskMonitorService);
    }
}