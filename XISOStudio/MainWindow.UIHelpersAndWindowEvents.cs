using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Serilog;
using XISOStudio.Models;
using XISOStudio.Services;

namespace XISOStudio;

/// <summary>Shared UI helpers, status and summary updates, and window event handlers.</summary>
[SuppressMessage("ReSharper", "UnusedMember.Local",
    Justification =
        "XAML event handlers are resolved by the Avalonia markup compiler, which ReSharper does not link across partial class files.")]
[SuppressMessage("ReSharper", "UnusedParameter.Local",
    Justification = "Parameters are required by XAML event handler signatures (sender, event args).")]
public partial class MainWindow
{
    /// <summary>Stops the operation timers and restores the final elapsed-time display.</summary>
    private void FinalizeUiState()
    {
        try
        {
            _processingTimer.Stop();
            _memoryTimer.Stop();
            _diskMonitorService.StopMonitoring();
            _isPerformanceCounterStopped = false;
            StopPerformanceCounter();
            ProgressBar.IsIndeterminate = false;

            var finalElapsedTime = _operationStopwatch.Elapsed;
            ProcessingTimeValue.Text = finalElapsedTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error while finalizing the operation UI state");
        }
    }

    /// <summary>Asks the user how to handle a file that must be downloaded from the cloud.</summary>
    /// <param name="fileName">Name of the cloud-only file.</param>
    /// <returns>The retry, skip, or cancel action chosen by the user.</returns>
    private async Task<CloudRetryResult> HandleCloudRetryRequestAsync(string fileName)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var result = await _messageBoxService.ShowAsync(
                    $"The file '{fileName}' is stored in the cloud and needs to be downloaded.\n\n" +
                    "• Click 'Yes' to Retry.\n" +
                    "• Click 'No' to Skip.\n" +
                    "• Click 'Cancel' to stop the batch.",
                    "Cloud File Required",
                    UiMessageBoxButton.YesNoCancel,
                    UiMessageBoxImage.Information);

                return result switch
                {
                    UiMessageBoxResult.Yes => CloudRetryResult.Retry,
                    UiMessageBoxResult.No => CloudRetryResult.Skip,
                    _ => CloudRetryResult.Cancel
                };
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error while asking how to handle cloud file {FileName}", fileName);
            return CloudRetryResult.Cancel;
        }
    }

    /// <summary>Removes stale temporary folders before a batch operation starts.</summary>
    /// <param name="token">Batch cancellation token; Cancel or Exit during the scan stops the cleanup.</param>
    private async Task PreOperationCleanupAsync(CancellationToken token)
    {
        try
        {
            _logger.Information("Performing pre-operation cleanup of temporary folders...");
            await TempFolderCleanupHelper.CleanupTempFoldersAsync(_logger, token);
            _logger.Information("Pre-operation cleanup completed.");
        }
        catch (OperationCanceledException)
        {
            // The user canceled (or asked to exit) while the cleanup scan was running:
            // let the batch start handler finish the operation as canceled.
            _logger.Information("Pre-operation cleanup canceled.");
            throw;
        }
        catch (Exception ex)
        {
            // Cleanup is best-effort; never block the operation because of it.
            _logger.Warning(ex, "Pre-operation cleanup of temporary folders failed");
        }
    }

    /// <summary>Requests cancellation of the running operation.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void CancelButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            // CTS already disposed during shutdown — ignore
            _logger.Debug(ex, "Cancellation token source already disposed during shutdown");
        }

        _logger.Information("Cancellation requested. Finishing current file...");
    }

    /// <summary>Shows the About dialog.</summary>
    /// <param name="sender">The menu item that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void AboutMenuItem_ClickAsync(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var aboutWindow = new AboutWindow(_urlOpener, _messageBoxService, _logger);
            await aboutWindow.ShowDialog(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method AboutMenuItem_ClickAsync");
        }
    }

    /// <summary>
    /// Shows an error message box without awaiting it, swallowing failures so a dialog
    /// failure cannot surface as an unobserved task exception.
    /// </summary>
    /// <param name="message">Error message to display.</param>
    private void ShowErrorSafe(string message)
    {
        _ = ShowErrorSafeAsync(message);
    }

    /// <summary>Shows an error message box, logging (not throwing) when the dialog itself fails.</summary>
    /// <param name="message">Error message to display.</param>
    private async Task ShowErrorSafeAsync(string message)
    {
        try
        {
            await _messageBoxService.ShowErrorAsync(message);
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "Failed to show an error message box");
        }
    }

    /// <summary>Shows a warning message box, logging (not throwing) when the dialog itself fails.</summary>
    /// <param name="message">Warning message to display.</param>
    private async Task ShowWarningSafeAsync(string message)
    {
        try
        {
            await _messageBoxService.ShowWarningAsync(message, "Warning");
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "Failed to show a warning message box");
        }
    }

    /// <summary>Opens the donation page in the default browser.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void DonateButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            _urlOpener.OpenUrl("https://www.purelogiccode.com/donate");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error opening the donation page");
            ShowErrorSafe($"Unable to open the donation page: {ex.Message}");
        }
    }

    /// <summary>Shows the folder picker and returns the selected local path.</summary>
    /// <param name="description">Title shown in the folder picker.</param>
    /// <returns>The selected folder path, or <c>null</c> when the user cancels or the picker fails.</returns>
    private async Task<string?> SelectFolderAsync(string description)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = description,
                AllowMultiple = false
            });

            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }
        catch (ArgumentException ex)
        {
            // Avalonia throws when the picker returns a folder that no longer exists
            // (deleted, disconnected drive, or unavailable network share).
            _logger.Information(ex, "Folder picker returned a folder that no longer exists");
            await ShowWarningSafeAsync(
                "The selected folder is no longer available. It may have been moved or deleted. Please choose another folder.");
            return null;
        }
        catch (Exception ex)
        {
            // A picker failure is environmental, not a defect: keep it below the
            // automatic bug-report threshold.
            _logger.Information(ex, "Folder picker failed");
            await ShowWarningSafeAsync("The folder picker could not be opened. Please try again.");
            return null;
        }
    }

    /// <summary>Ensures the input and output folders differ and are not nested.</summary>
    /// <param name="inputFolder">Folder containing the source files.</param>
    /// <param name="outputFolder">Folder receiving the converted files.</param>
    /// <returns><c>true</c> when the folders are valid; otherwise <c>false</c> after an error dialog.</returns>
    private async Task<bool> ValidateInputOutputFoldersAsync(string inputFolder, string outputFolder)
    {
        try
        {
            // macOS is case-insensitive too (default APFS/HFS+), so the shared policy is
            // used instead of a Windows-only check.
            var comparison = PathHelper.PathComparison;
            var normalizedInput = Path.GetFullPath(inputFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedOutput = Path.GetFullPath(outputFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (normalizedInput.Equals(normalizedOutput, comparison))
            {
                await _messageBoxService.ShowErrorAsync("Input and output folders must be different.");
                return false;
            }

            // Check if output folder is a subfolder of input folder
            if (normalizedOutput.StartsWith(normalizedInput + Path.DirectorySeparatorChar, comparison))
            {
                await _messageBoxService.ShowErrorAsync(
                    "Output folder cannot be a subfolder of the input folder. This would cause recursive processing issues.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not validate the input and output folders");
            await _messageBoxService.ShowErrorAsync("The selected input or output folder is not valid.");
            return false;
        }
    }

    /// <summary>Writes the current summary counters to the stats panel.</summary>
    private void UpdateSummaryStatsUi()
    {
        TotalFilesValue.Text = _uiTotalFiles.ToString(CultureInfo.InvariantCulture);
        SuccessValue.Text = _uiSuccessCount.ToString(CultureInfo.InvariantCulture);
        FailedValue.Text = _uiFailedCount.ToString(CultureInfo.InvariantCulture);
        SkippedValue.Text = _uiSkippedCount.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Updates the progress bar and its text for the processed-file count.</summary>
    /// <param name="current">Number of files processed so far.</param>
    /// <param name="total">Total number of files in the batch.</param>
    private void UpdateProgressUi(int current, int total)
    {
        // Don't update determinate text if we haven't received a total yet
        if (total <= 0) return;

        ProgressBar.Maximum = total;
        ProgressBar.Value = current;

        if (ProgressBar.IsVisible && !ProgressBar.IsIndeterminate)
        {
            var percentage = (double)current / total * 100;
            ProgressTextBlock.Text = $"{current} of {total} ({percentage:F0}%)";
        }
    }

    /// <summary>
    ///     Restores the normal UI state as soon as the batch is over and shows the summary.
    ///     The controls are re-enabled before the summary dialog is shown so that a dialog
    ///     failure can never leave the window permanently disabled.
    /// </summary>
    /// <param name="operationType">Operation name ("Conversion" or "Test").</param>
    /// <param name="operationStarted">Whether the batch actually started.</param>
    /// <param name="operationCanceled">Whether the user canceled the batch.</param>
    /// <param name="operationCompletedTcs">Completion source of the operation being finished.</param>
    private async Task FinishOperationAsync(string operationType, bool operationStarted, bool operationCanceled,
        TaskCompletionSource operationCompletedTcs)
    {
        // The batch reports its results through Progress<T>, which posts every callback to the
        // dispatcher queue. The batch can complete inline on the UI thread before the last
        // queued callback ran, so drain the queue before reading the summary counters —
        // otherwise the last file is missing from the summary (for example 1 success for
        // 2 converted files). The lower-priority no-op runs after every queued report.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

        FinalizeUiState();

        _isOperationRunning = false;
        SetControlsState(true);

        // Release the shutdown wait before the summary dialog: the batch work is done, and
        // the user must be able to keep reading the summary while the app exits.
        operationCompletedTcs.TrySetResult();

        await LogOperationSummaryAsync(operationType, operationStarted, operationCanceled);
    }

    /// <summary>Writes the batch summary to the log and shows the result dialog.</summary>
    /// <param name="operationType">Operation name ("Conversion" or "Test").</param>
    /// <param name="operationStarted">Whether the batch actually started.</param>
    /// <param name="operationCanceled">Whether the user canceled the batch.</param>
    private async Task LogOperationSummaryAsync(string operationType, bool operationStarted, bool operationCanceled)
    {
        try
        {
            _logger.Information("");
            _logger.Information("--- Batch {OperationType} {Outcome}. ---", operationType.ToLowerInvariant(),
                operationCanceled ? "canceled" : "completed");
            _logger.Information("Total files processed: {TotalFiles}", _uiTotalFiles);
            _logger.Information("Successfully {Action}: {SuccessCount} files",
                ConvertToPastTense.GetPastTense(operationType), _uiSuccessCount);
            _logger.Information("Skipped: {SkippedCount} files", _uiSkippedCount);

            if (_uiFailedCount > 0)
            {
                _logger.Information("Failed to {OperationType}: {FailedCount} files",
                    operationType.ToLowerInvariant(), _uiFailedCount);
                _logger.Information("List of files that failed (original names):");
                foreach (var originalPath in _failedFilePaths)
                {
                    _logger.Information("- {FileName}", Path.GetFileName(originalPath));
                }

                _logger.Information("");
            }

            // Validation failed before the batch started: the error dialog was already shown,
            // so there is no "completed" summary to display.
            if (!operationStarted) return;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (_isForceClosing) return;

                var summaryText = $"Total files processed: {_uiTotalFiles}\n" +
                                  $"Successfully {ConvertToPastTense.GetPastTense(operationType)}: {_uiSuccessCount} files\n" +
                                  $"Skipped: {_uiSkippedCount} files\n" +
                                  $"Failed: {_uiFailedCount} files";

                if (operationCanceled)
                {
                    await _messageBoxService.ShowAsync(
                        $"Batch {operationType.ToLowerInvariant()} was canceled.\n\n{summaryText}",
                        $"{operationType} Canceled", UiMessageBoxButton.Ok, UiMessageBoxImage.Warning);
                    return;
                }

                if (_totalProcessedFiles > 5 && (double)_invalidIsoErrorCount / _totalProcessedFiles > 0.5)
                {
                    await _messageBoxService.ShowWarningAsync(
                        $"Many files ({_invalidIsoErrorCount} out of {_totalProcessedFiles}) were not valid Xbox ISOs. " +
                        "Please ensure you are selecting the correct ISO files from Xbox or Xbox 360 games.",
                        "High Rate of Invalid ISOs Detected");
                }

                await _messageBoxService.ShowAsync($"Batch {operationType.ToLowerInvariant()} completed.\n\n" +
                                                   summaryText,
                    $"{operationType} Complete", UiMessageBoxButton.Ok,
                    _uiFailedCount > 0 ? UiMessageBoxImage.Warning : UiMessageBoxImage.Information);
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error while logging the batch operation summary");
        }
    }

    /// <summary>Updates the elapsed time and disk read/write speed display.</summary>
    /// <param name="sender">The timer that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void ProcessingTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var elapsedTime = _operationStopwatch.Elapsed;
            ProcessingTimeValue.Text = elapsedTime.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

            // Update read speed
            ReadSpeedValue?.Text = _diskMonitorService.GetCurrentReadSpeedFormatted();

            ReadSpeedDriveIndicator?.Text = _diskMonitorService.CurrentDriveLetter != null
                ? $"({_diskMonitorService.CurrentDriveLetter})"
                : "";

            // Update write speed
            WriteSpeedValue?.Text = _diskMonitorService.GetCurrentWriteSpeedFormatted();

            WriteSpeedDriveIndicator?.Text = _diskMonitorService.CurrentDriveLetter != null
                ? $"({_diskMonitorService.CurrentDriveLetter})"
                : "";

            // Show status message in status bar if disk speed is unavailable
            var statusMessage = _diskMonitorService.StatusMessage;
            if (!string.IsNullOrEmpty(statusMessage) && StatusTextBlock != null &&
                !statusMessage.Equals(StatusTextBlock.Text, StringComparison.Ordinal))
            {
                StatusTextBlock.Text = statusMessage;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Error while updating the processing timer UI");
        }
    }

    /// <summary>Enables or disables the navigation and settings controls for a running operation.</summary>
    /// <param name="enabled"><c>true</c> to restore the idle UI; <c>false</c> while an operation runs.</param>
    private void SetControlsState(bool enabled)
    {
        // Disable/Enable the navigation buttons in the header
        BtnNavConvert.IsEnabled = enabled;
        BtnNavTest.IsEnabled = enabled;
        BtnNavExplorer.IsEnabled = enabled;

        // Disable/Enable the entire settings area
        ControlsBorder.IsEnabled = enabled;

        // Toggle visibility of progress and cancel
        ProgressAreaGrid.IsVisible = !enabled;
        ProgressBar.IsVisible = !enabled;
        CancelButton.IsVisible = !enabled;

        if (enabled)
        {
            UpdateStatus("Ready.");
        }
    }

    /// <summary>Resets the summary counters, the progress bar, and the failed-file set.</summary>
    private void ResetSummaryStats()
    {
        _uiTotalFiles = _uiSuccessCount = _uiFailedCount = _uiSkippedCount = 0;
        _invalidIsoErrorCount = 0;
        _totalProcessedFiles = 0;
        _failedFilePaths.Clear();

        UpdateSummaryStatsUi();

        // Reset Progress Bar to a clean state
        ProgressBar.Value = 0;
        ProgressBar.Maximum = 1;
        ProgressBar.IsIndeterminate = false;
        ProgressTextBlock.Text = "";
    }

    /// <summary>Switches to the conversion view.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void NavConvert_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ConvertView.IsVisible = true;
        TestView.IsVisible = false;
        ExplorerHeaderView.IsVisible = false;

        ShowLogPanel();
        StatsPanel.IsVisible = true;

        UpdateNavigationButtonStyles(BtnNavConvert);
    }

    /// <summary>Switches to the integrity-test view.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void NavTest_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ConvertView.IsVisible = false;
        TestView.IsVisible = true;
        ExplorerHeaderView.IsVisible = false;

        ShowLogPanel();
        StatsPanel.IsVisible = true;

        UpdateNavigationButtonStyles(BtnNavTest);
    }

    /// <summary>Switches to the explorer view and hides the log panel.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void NavExplorer_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ConvertView.IsVisible = false;
        TestView.IsVisible = false;
        ExplorerHeaderView.IsVisible = true;

        HideLogPanel();
        StatsPanel.IsVisible = false;

        UpdateNavigationButtonStyles(BtnNavExplorer);
    }

    /// <summary>Log column width saved while the explorer hides the log panel.</summary>
    private GridLength _savedLogColumnWidth = new(1, GridUnitType.Star);

    /// <summary>Splitter column width saved while the explorer hides the log panel.</summary>
    private GridLength _savedSplitterColumnWidth = new(10);

    /// <summary>The splitter column of the main content grid (see MainWindow.axaml).</summary>
    private ColumnDefinition SplitterColumn => ContentGrid.ColumnDefinitions[1];

    /// <summary>The log column of the main content grid (see MainWindow.axaml).</summary>
    private ColumnDefinition LogColumn => ContentGrid.ColumnDefinitions[2];

    /// <summary>
    ///     Gives the explorer the full window width by collapsing the log column.
    /// </summary>
    private void HideLogPanel()
    {
        if (LogColumn.Width.Value > 0)
        {
            _savedLogColumnWidth = LogColumn.Width;
            _savedSplitterColumnWidth = SplitterColumn.Width;
        }

        LogColumn.MinWidth = 0;
        LogColumn.Width = new GridLength(0);
        SplitterColumn.Width = new GridLength(0);
        LogBorder.IsVisible = false;
    }

    /// <summary>
    ///     Restores the log column and splitter to their previous widths.
    /// </summary>
    private void ShowLogPanel()
    {
        LogColumn.MinWidth = 300;
        LogColumn.Width = _savedLogColumnWidth;
        SplitterColumn.Width = _savedSplitterColumnWidth;
        LogBorder.IsVisible = true;
    }

    /// <summary>Highlights the active navigation button and resets the others.</summary>
    /// <param name="selectedButton">Button to give the selected style.</param>
    private void UpdateNavigationButtonStyles(Button selectedButton)
    {
        try
        {
            // Reset all navigation buttons to default style
            BtnNavConvert.Theme = (ControlTheme?)this.FindResource("MenuButtonStyle");
            BtnNavTest.Theme = (ControlTheme?)this.FindResource("MenuButtonStyle");
            BtnNavExplorer.Theme = (ControlTheme?)this.FindResource("MenuButtonStyle");

            // Apply selected style to the active button
            selectedButton.Theme = (ControlTheme?)this.FindResource("SelectedMenuButtonStyle");
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Error while updating the navigation button styles");
        }
    }

    /// <summary>Indicates that the disk performance counter has already been stopped.</summary>
    private bool _isPerformanceCounterStopped;

    /// <summary>Stops disk monitoring and clears the speed display.</summary>
    private void StopPerformanceCounter()
    {
        if (_isPerformanceCounterStopped) return;

        _isPerformanceCounterStopped = true;

        try
        {
            _diskMonitorService.StopMonitoring();
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                ReadSpeedValue?.Text = "N/A";

                ReadSpeedDriveIndicator?.Text = "";

                WriteSpeedValue?.Text = "N/A";

                WriteSpeedDriveIndicator?.Text = "";
            });
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Error while stopping the disk performance counter");
        }
    }

    /// <summary>Writes a status message to the status bar.</summary>
    /// <param name="status">Status text to display.</param>
    private void UpdateStatus(string status)
    {
        StatusTextBlock?.Text = status;
    }

    /// <summary>Starts disk monitoring for the drive used by the current operation.</summary>
    /// <param name="driveLetter">Drive letter to monitor, or <c>null</c> when unknown.</param>
    private void SetCurrentOperationDrive(string? driveLetter)
    {
        try
        {
            _diskMonitorService.StartMonitoring(driveLetter);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Error while starting disk monitoring for {DriveLetter}", driveLetter);
        }
    }
}