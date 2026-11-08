using System.Diagnostics.CodeAnalysis;
using Avalonia.Interactivity;
using Serilog;
using XISOSharp;
using XISOStudio.Models;
using XISOStudio.Services;

namespace XISOStudio;

/// <summary>Event handlers for browsing input/output folders and starting batch conversion or testing.</summary>
[SuppressMessage("ReSharper", "UnusedMember.Local",
    Justification =
        "XAML event handlers are resolved by the Avalonia markup compiler, which ReSharper does not link across partial class files.")]
[SuppressMessage("ReSharper", "UnusedParameter.Local",
    Justification = "Parameters are required by XAML event handler signatures (sender, event args).")]
[SuppressMessage("ReSharper", "AsyncVoidEventHandlerMethod",
    Justification = "XAML event handlers must be async void; every awaited operation is guarded by try/catch.")]
public partial class MainWindow
{
    /// <summary>Prompts for the conversion input folder and refreshes the conversion file list.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void BrowseConversionInputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var inputFolder = await SelectFolderAsync("Select the folder containing ISO or archive files");
            if (string.IsNullOrEmpty(inputFolder)) return;

            if (CheckForTempPath.IsSystemTempPath(inputFolder))
            {
                await _messageBoxService.ShowErrorAsync(
                    "The system's temporary folder or a subfolder within it cannot be selected as an input folder. Please choose a different location.");
                return;
            }

            ConversionInputFolderTextBox.Text = inputFolder;
            _ = RefreshConversionFileListAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseConversionInputButton_ClickAsync");
        }
    }

    /// <summary>Prompts for the conversion output folder.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void BrowseConversionOutputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var outputFolder = await SelectFolderAsync("Select the output folder for converted files");
            if (string.IsNullOrEmpty(outputFolder)) return;

            if (CheckForTempPath.IsSystemTempPath(outputFolder))
            {
                await _messageBoxService.ShowErrorAsync(
                    "The system's temporary folder or a subfolder within it cannot be selected as an output folder. Please choose a different location.");
                return;
            }

            ConversionOutputFolderTextBox.Text = outputFolder;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseConversionOutputButton_ClickAsync");
        }
    }

    /// <summary>Prompts for the integrity-test input folder and refreshes the test file list.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void BrowseTestInputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var inputFolder =
                await SelectFolderAsync("Select the folder containing ISO, CSO, ZAR, or CHD files to test");
            if (string.IsNullOrEmpty(inputFolder)) return;

            if (CheckForTempPath.IsSystemTempPath(inputFolder))
            {
                await _messageBoxService.ShowErrorAsync(
                    "The system's temporary folder or a subfolder within it cannot be selected as an input folder for testing. Please choose a different location.");
                return;
            }

            TestInputFolderTextBox.Text = inputFolder;
            _ = RefreshTestFileListAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in method BrowseTestInputButton_ClickAsync");
        }
    }

    /// <summary>Validates the selection and runs the batch conversion.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void StartConversionButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Guard outside the try: the finally block tears the operation down, so an early
            // return from inside it would finish the operation that is already running.
            if (_isOperationRunning) return;

            var operationStarted = false;
            var operationCanceled = false;
            // Capture the TCS for this operation: a stale operation must never complete
            // the wait of a batch that was started after it.
            var operationCompletedTcs = new TaskCompletionSource();

            try
            {
                _isOperationRunning = true;
                _operationCompletedTcs = operationCompletedTcs;

                // Create the batch token before the cleanup scan starts: Cancel or Exit during
                // the scan must cancel this operation, not the previous (already finished) one.
                var cts = new CancellationTokenSource();
                var oldCts = Interlocked.Exchange(ref _cts, cts);
                try
                {
                    oldCts.Dispose();
                }
                catch (Exception ex)
                {
                    /* Already disposed by CleanupResources */
                    _logger.Debug(ex, "Cancellation token source was already disposed");
                }

                SetControlsState(false);
                ClearLogViewer();
                ResetSummaryStats();

                // Immediate visual feedback while the background thread scans the filesystem
                ProgressBar.IsIndeterminate = true;
                ProgressTextBlock.Text = "Preparing conversion...";

                UpdateStatus("Cleaning up temporary files...");
                await PreOperationCleanupAsync(cts.Token);

                var inputFolder = ConversionInputFolderTextBox.Text;
                var outputFolder = ConversionOutputFolderTextBox.Text;

                if (string.IsNullOrEmpty(inputFolder) || string.IsNullOrEmpty(outputFolder))
                {
                    await _messageBoxService.ShowErrorAsync(
                        "Please select both input and output folders for conversion.");
                    FinalizeUiState();
                    return;
                }

                if (!Directory.Exists(inputFolder))
                {
                    await _messageBoxService.ShowErrorAsync($"The input folder no longer exists:\n{inputFolder}");
                    FinalizeUiState();
                    return;
                }

                if (!Directory.Exists(outputFolder))
                {
                    await _messageBoxService.ShowErrorAsync($"The output folder no longer exists:\n{outputFolder}");
                    FinalizeUiState();
                    return;
                }

                if (!await ValidateInputOutputFoldersAsync(inputFolder, outputFolder))
                {
                    FinalizeUiState();
                    return;
                }

                var selectedFiles = GetSelectedConversionFiles();
                if (selectedFiles.Count == 0)
                {
                    await _messageBoxService.ShowErrorAsync(
                        "No files selected for conversion. Select a source folder and tick at least one file in the list.");
                    FinalizeUiState();
                    return;
                }

                var progress = CreateProgressReporter();

                _operationStopwatch.Restart();
                _processingTimer.Start();
                _memoryTimer.Start();
                UpdateStatus("Starting batch conversion...");

                var outputFormat = OutputFormatXisoRadio.IsChecked == true
                    ? OutputFormat.Xiso
                    : OutputFormatZarRadio.IsChecked == true
                        ? OutputFormat.Zar
                        : OutputFormatCsoRadio.IsChecked == true
                            ? OutputFormat.Cso
                            : OutputFormatCciRadio.IsChecked == true
                                ? OutputFormat.Cci
                                : OutputFormat.Chd;

                operationStarted = true;
                await _orchestratorService.ConvertFilesAsync(
                    selectedFiles, outputFolder,
                    DeleteOriginalsCheckBox.IsChecked ?? false,
                    SkipSystemUpdateCheckBox.IsChecked ?? false,
                    CheckOutputIntegrityCheckBox.IsChecked ?? false,
                    outputFormat,
                    progress, HandleCloudRetryRequestAsync, cts.Token);
            }
            catch (OperationCanceledException ex)
            {
                operationCanceled = true;
                _logger.Information(ex, "Batch conversion canceled by the user");
                UpdateStatus("Operation canceled.");
            }
            catch (Exception ex)
            {
                // Environmental errors (disk space, network, disconnected drives) are not
                // application bugs — the user already gets a clear message from the orchestrator.
                if (PathHelper.IsDiskSpaceError(ex) || PathHelper.IsNetworkError(ex) ||
                    PathHelper.IsDeviceIoError(ex))
                {
                    _logger.Information(ex, "Batch conversion stopped due to an environmental error");
                }
                else
                {
                    // The orchestrator already logged and reported this defect at Error; log
                    // here at Information so the same defect is not reported twice.
                    _logger.Information(ex, "Batch conversion failed; the error was reported by the orchestrator");
                }
            }
            finally
            {
                await FinishOperationAsync("Conversion", operationStarted, operationCanceled, operationCompletedTcs);
                await RefreshConversionFileListAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in method StartConversionButton_ClickAsync");
        }
    }

    /// <summary>Validates the selection and runs the batch integrity test.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void StartTestButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Guard outside the try: the finally block tears the operation down, so an early
            // return from inside it would finish the operation that is already running.
            if (_isOperationRunning) return;

            var operationStarted = false;
            var operationCanceled = false;
            // Capture the TCS for this operation: a stale operation must never complete
            // the wait of a batch that was started after it.
            var operationCompletedTcs = new TaskCompletionSource();

            try
            {
                _isOperationRunning = true;
                _operationCompletedTcs = operationCompletedTcs;

                // Create the batch token before the cleanup scan starts: Cancel or Exit during
                // the scan must cancel this operation, not the previous (already finished) one.
                var cts = new CancellationTokenSource();
                var oldCts = Interlocked.Exchange(ref _cts, cts);
                try
                {
                    oldCts.Dispose();
                }
                catch (Exception ex)
                {
                    /* Already disposed by CleanupResources */
                    _logger.Debug(ex, "Cancellation token source was already disposed");
                }

                SetControlsState(false);
                ClearLogViewer();
                ResetSummaryStats();

                // Immediate visual feedback while the background thread scans the filesystem
                ProgressBar.IsIndeterminate = true;
                ProgressTextBlock.Text = "Preparing integrity test...";

                UpdateStatus("Cleaning up temporary files...");
                await PreOperationCleanupAsync(cts.Token);

                var inputFolder = TestInputFolderTextBox.Text;
                if (string.IsNullOrEmpty(inputFolder))
                {
                    await _messageBoxService.ShowErrorAsync("Please select the input folder for testing.");
                    FinalizeUiState();
                    return;
                }

                if (!Directory.Exists(inputFolder))
                {
                    await _messageBoxService.ShowErrorAsync($"The input folder no longer exists:\n{inputFolder}");
                    FinalizeUiState();
                    return;
                }

                var selectedFiles = GetSelectedTestFiles();
                if (selectedFiles.Count == 0)
                {
                    await _messageBoxService.ShowErrorAsync(
                        "No files selected for testing. Select an image folder and tick at least one file in the list.");
                    FinalizeUiState();
                    return;
                }

                var progress = CreateProgressReporter();

                _operationStopwatch.Restart();
                _processingTimer.Start();
                _memoryTimer.Start();
                UpdateStatus("Starting batch image test...");

                operationStarted = true;
                await _orchestratorService.TestFilesAsync(
                    inputFolder, selectedFiles,
                    MoveSuccessFilesCheckBox.IsChecked == true,
                    MoveFailedFilesCheckBox.IsChecked == true,
                    PerformDeepScanCheckBox.IsChecked ?? false,
                    progress, HandleCloudRetryRequestAsync, cts.Token);
            }
            catch (OperationCanceledException ex)
            {
                operationCanceled = true;
                _logger.Information(ex, "Batch integrity test canceled by the user");
                UpdateStatus("Operation canceled.");
            }
            catch (Exception ex)
            {
                // Environmental errors (disk space, network, disconnected drives) are not
                // application bugs — the user already gets a clear message from the orchestrator.
                if (PathHelper.IsDiskSpaceError(ex) || PathHelper.IsNetworkError(ex) ||
                    PathHelper.IsDeviceIoError(ex))
                {
                    _logger.Information(ex, "Batch test stopped due to an environmental error");
                }
                else
                {
                    // The orchestrator already logged and reported this defect at Error; log
                    // here at Information so the same defect is not reported twice.
                    _logger.Information(ex, "Batch test failed; the error was reported by the orchestrator");
                }
            }
            finally
            {
                await FinishOperationAsync("Test", operationStarted, operationCanceled, operationCompletedTcs);
                await RefreshTestFileListAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in method StartTestButton_ClickAsync");
        }
    }

    /// <summary>Prompts for the decompression input folder and refreshes the file list.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void BrowseDecompressInputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var inputFolder = await SelectFolderAsync("Select the folder containing compressed Xbox images");
            if (string.IsNullOrEmpty(inputFolder)) return;

            if (CheckForTempPath.IsSystemTempPath(inputFolder))
            {
                await _messageBoxService.ShowErrorAsync(
                    "The system's temporary folder or a subfolder within it cannot be selected as an input folder. Please choose a different location.");
                return;
            }

            DecompressInputFolderTextBox.Text = inputFolder;
            _ = RefreshDecompressFileListAsync();
        }
        catch (Exception ex)
        {
            // A picker failure is environmental; it must not generate a bug report.
            _logger.Information(ex, "Decompression input folder picker failed");
        }
    }

    /// <summary>Prompts for the decompression output folder.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void BrowseDecompressOutputButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            var outputFolder = await SelectFolderAsync("Select the output folder for decompressed ISO files");
            if (string.IsNullOrEmpty(outputFolder)) return;

            if (CheckForTempPath.IsSystemTempPath(outputFolder))
            {
                await _messageBoxService.ShowErrorAsync(
                    "The system's temporary folder or a subfolder within it cannot be selected as an output folder. Please choose a different location.");
                return;
            }

            DecompressOutputFolderTextBox.Text = outputFolder;
        }
        catch (Exception ex)
        {
            _logger.Information(ex, "Decompression output folder picker failed");
        }
    }

    /// <summary>Validates the decompression selection and runs the batch.</summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void StartDecompressionButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Guard outside the try: the finally block tears the operation down, so an early
            // return from inside it would finish the operation that is already running.
            if (_isOperationRunning) return;

            var operationStarted = false;
            var operationCanceled = false;
            // Capture the TCS for this operation: a stale operation must never complete
            // the wait of a batch that was started after it.
            var operationCompletedTcs = new TaskCompletionSource();

            try
            {
                _isOperationRunning = true;
                _operationCompletedTcs = operationCompletedTcs;

                var cts = new CancellationTokenSource();
                var oldCts = Interlocked.Exchange(ref _cts, cts);
                try
                {
                    oldCts.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Cancellation token source was already disposed");
                }

                SetControlsState(false);
                ClearLogViewer();
                ResetSummaryStats();

                ProgressBar.IsIndeterminate = true;
                ProgressTextBlock.Text = "Preparing decompression...";

                UpdateStatus("Cleaning up temporary files...");
                await PreOperationCleanupAsync(cts.Token);

                var inputFolder = DecompressInputFolderTextBox.Text;
                if (string.IsNullOrEmpty(inputFolder))
                {
                    await _messageBoxService.ShowErrorAsync(
                        "Please select the input folder containing compressed images.");
                    FinalizeUiState();
                    return;
                }

                if (!Directory.Exists(inputFolder))
                {
                    await _messageBoxService.ShowErrorAsync($"The input folder no longer exists:\n{inputFolder}");
                    FinalizeUiState();
                    return;
                }

                var outputFolder = DecompressOutputFolderTextBox.Text;
                if (string.IsNullOrEmpty(outputFolder))
                {
                    await _messageBoxService.ShowErrorAsync("Please select the output folder for decompressed files.");
                    FinalizeUiState();
                    return;
                }

                if (!await ValidateInputOutputFoldersAsync(inputFolder, outputFolder))
                {
                    FinalizeUiState();
                    return;
                }

                var selectedFiles = GetSelectedDecompressFiles();
                if (selectedFiles.Count == 0)
                {
                    await _messageBoxService.ShowErrorAsync(
                        "No files selected for decompression. Select an image folder and tick at least one file in the list.");
                    FinalizeUiState();
                    return;
                }

                var progress = CreateProgressReporter();

                _operationStopwatch.Restart();
                _processingTimer.Start();
                _memoryTimer.Start();
                UpdateStatus("Starting batch decompression...");

                operationStarted = true;
                await _orchestratorService.DecompressFilesAsync(
                    selectedFiles, outputFolder,
                    DeleteDecompressOriginalsCheckBox.IsChecked == true,
                    CheckDecompressIntegrityCheckBox.IsChecked ?? true,
                    progress, HandleCloudRetryRequestAsync, cts.Token);
            }
            catch (OperationCanceledException ex)
            {
                operationCanceled = true;
                _logger.Information(ex, "Batch decompression canceled by the user");
                UpdateStatus("Operation canceled.");
            }
            catch (Exception ex)
            {
                // Environmental errors (disk space, network, disconnected drives) are not
                // application bugs — the user already gets a clear message from the orchestrator.
                if (PathHelper.IsDiskSpaceError(ex) || PathHelper.IsNetworkError(ex) ||
                    PathHelper.IsDeviceIoError(ex))
                {
                    _logger.Information(ex, "Batch decompression stopped due to an environmental error");
                }
                else
                {
                    _logger.Information(ex, "Batch decompression failed; the error was reported by the orchestrator");
                }
            }
            finally
            {
                await FinishOperationAsync("Decompress", operationStarted, operationCanceled, operationCompletedTcs);
                await RefreshDecompressFileListAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in method StartDecompressionButton_ClickAsync");
        }
    }

    /// <summary>
    /// Repairs the selected plain ISO/XISO images in place. Every image is backed up as
    /// <c>.old</c> first; compressed containers are refused because the library cannot patch
    /// them in place.
    /// </summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void RepairSelectedButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        if (_isOperationRunning) return;

        var selected = GetSelectedTestFiles().Where(SupportedFiles.IsIso).ToList();
        if (selected.Count == 0)
        {
            await _messageBoxService.ShowWarningAsync(
                "Select at least one .iso or .xiso image to repair.\n\n" +
                "Compressed CISO/CCI images cannot be patched in place; decompress them first.",
                "Repair images");
            return;
        }

        var confirmation = await _messageBoxService.ShowAsync(
            $"Repair {selected.Count} selected image(s) in place?\n\n" +
            "Each image is backed up as '<name>.old' before any change. " +
            "Truncated or structurally broken images are reported, never modified.",
            "Repair images", UiMessageBoxButton.YesNo, UiMessageBoxImage.Warning);
        if (confirmation != UiMessageBoxResult.Yes) return;

        RepairSelectedButton.IsEnabled = false;
        var repaired = 0;
        try
        {
            foreach (var imagePath in selected)
            {
                try
                {
                    var result = await _xisoRecoveryService.RepairAsync(imagePath, dryRun: false,
                        CancellationToken.None);
                    _logger.Information("Repair of '{FileName}' {Outcome}.", Path.GetFileName(imagePath),
                        result.Success ? "now passes the audit" : "still reports issues");
                    repaired++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    _logger.Information(ex, "Could not repair '{FileName}': {Reason}", Path.GetFileName(imagePath),
                        ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Repair failed for '{FileName}'", Path.GetFileName(imagePath));
                }
            }
        }
        finally
        {
            RepairSelectedButton.IsEnabled = true;
        }

        UpdateStatus($"Repair finished for {repaired} image(s).");
        await RefreshTestFileListAsync();
    }

    /// <summary>
    /// Rebuilds every reachable entry from the selected damaged images into fresh
    /// <c>.salvaged.iso</c> files in a folder chosen by the user.
    /// </summary>
    /// <param name="sender">The button that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void SalvageSelectedButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        if (_isOperationRunning) return;

        var selected = GetSelectedTestFiles().Where(static path => Path.GetExtension(path).ToLowerInvariant()
            is ".iso" or ".xiso" or ".img" or ".cso" or ".cci").ToList();

        if (selected.Count == 0)
        {
            await _messageBoxService.ShowWarningAsync(
                "Select at least one ISO, CISO or CCI image to salvage.\n\n" +
                "ZAR archives and CHD images store a different structure; extract or decompress them first.",
                "Salvage images");
            return;
        }

        var outputFolder = await SelectFolderAsync("Select the folder that receives the salvaged ISO files");
        if (string.IsNullOrEmpty(outputFolder)) return;

        SalvageSelectedButton.IsEnabled = false;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var salvaged = 0;

        try
        {
            foreach (var imagePath in selected)
            {
                try
                {
                    var stem = XisoImageSource.StripImageSuffix(Path.GetFileName(imagePath));
                    if (string.IsNullOrWhiteSpace(stem)) stem = Path.GetFileNameWithoutExtension(imagePath);

                    var outputPath = Path.Combine(outputFolder, stem + ".salvaged.iso");
                    for (var counter = 2; !usedNames.Add(outputPath); counter++)
                    {
                        outputPath = Path.Combine(outputFolder, $"{stem} ({counter}).salvaged.iso");
                    }

                    var result = await _xisoRecoveryService.SalvageAsync(imagePath, outputPath,
                        CancellationToken.None);
                    _logger.Information(
                        "Salvage of '{FileName}': {CopiedCount} entries carried, {SkippedCount} skipped, output '{OutputPath}'.",
                        Path.GetFileName(imagePath), result.Copied.Count, result.Skipped.Count, result.OutputPath);
                    salvaged++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    _logger.Information(ex, "Could not salvage '{FileName}': {Reason}", Path.GetFileName(imagePath),
                        ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Salvage failed for '{FileName}'", Path.GetFileName(imagePath));
                }
            }
        }
        finally
        {
            SalvageSelectedButton.IsEnabled = true;
        }

        UpdateStatus($"Salvage finished for {salvaged} image(s).");
        await RefreshTestFileListAsync();
    }

    /// <summary>
    /// Creates the batch progress reporter shared by conversion, testing and decompression:
    /// it mirrors log lines, status text, counters and the current drive into the UI.
    /// </summary>
    /// <returns>The progress reporter bound to this window.</returns>
    private Progress<BatchOperationProgress> CreateProgressReporter()
    {
        return new Progress<BatchOperationProgress>(p =>
        {
            try
            {
                if (p.LogMessage != null) _logger.Information("{Message:l}", p.LogMessage);
                if (p.StatusText != null) UpdateStatus(p.StatusText);

                if (p.TotalFiles.HasValue)
                {
                    _uiTotalFiles = p.TotalFiles.Value;
                    UpdateSummaryStatsUi();
                }

                if (p.ProcessedCount.HasValue)
                {
                    UpdateProgressUi(p.ProcessedCount.Value, _uiTotalFiles);
                }

                if (p.SuccessCount.HasValue)
                {
                    _uiSuccessCount += p.SuccessCount.Value;
                    _totalProcessedFiles += p.SuccessCount.Value;
                    UpdateSummaryStatsUi();
                }

                if (p.FailedCount.HasValue)
                {
                    _uiFailedCount += p.FailedCount.Value;
                    _totalProcessedFiles += p.FailedCount.Value;
                    UpdateSummaryStatsUi();
                }

                // Only genuinely invalid images count toward the "not valid Xbox ISOs" warning.
                if (p.InvalidIsoCount.HasValue)
                {
                    _invalidIsoErrorCount += p.InvalidIsoCount.Value;
                }

                if (p.SkippedCount.HasValue)
                {
                    _uiSkippedCount += p.SkippedCount.Value;
                    _totalProcessedFiles += p.SkippedCount.Value;
                    UpdateSummaryStatsUi();
                }

                if (p.CurrentDrive != null) SetCurrentOperationDrive(p.CurrentDrive);
                if (p.FailedPathToAdd != null) _failedFilePaths.Add(p.FailedPathToAdd);

                if (p.TotalFiles.HasValue || p.ProcessedCount.HasValue)
                {
                    ProgressBar.IsIndeterminate = false;
                }
            }
            catch (OperationCanceledException ex)
            {
                // Ignore cancellation exceptions during UI updates
                _logger.Debug(ex, "Ignoring cancellation exception during progress UI update");
            }
        });
    }
}