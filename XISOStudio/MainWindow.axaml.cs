using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using XISOStudio.Interfaces;
using XISOStudio.Models;
using XISOStudio.Services;
using Serilog;

namespace XISOStudio;

/// <summary>
/// Main application window: batch conversion and integrity testing, the on-screen log
/// viewer, and the XISO/ZAR/CHD image explorer. The class is split across several
/// partial files by feature.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Maximum number of log lines appended per UI flush; larger bursts are split into several flushes.</summary>
    private const int MaxLogLinesPerFlush = 500;

    /// <summary>How often pending log lines are pushed to the viewer; at most ~10 text updates per second.</summary>
    private static readonly TimeSpan LogFlushInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Bounded buffer of rendered log lines waiting for/held by the on-screen viewer.</summary>
    private readonly LogViewBuffer _logBuffer = new();

    /// <summary>Throttles viewer updates so a logging burst cannot flood the dispatcher.</summary>
    private readonly DispatcherTimer _logFlushTimer = new() { Interval = LogFlushInterval };

    /// <summary>Coordinates batch conversion and integrity testing.</summary>
    private readonly IOrchestratorService _orchestratorService = null!;

    /// <summary>Reports disk read/write speed and free space while an operation runs.</summary>
    private readonly IDiskMonitorService _diskMonitorService = null!;

    /// <summary>Cancels the current batch operation.</summary>
    private CancellationTokenSource _cts = new();

    /// <summary>
    ///     Cancels explorer extractions. Kept separate from <see cref="_cts"/> so a canceled
    ///     batch does not leave the explorer's token canceled for later open/drag operations.
    /// </summary>
    private readonly CancellationTokenSource _explorerCts = new();

    /// <summary>Signals completion of the current batch operation to the shutdown flow.</summary>
    private TaskCompletionSource _operationCompletedTcs = new();

    /// <summary>Checks GitHub for a newer release at startup.</summary>
    private readonly IUpdateChecker _updateChecker = null!;

    /// <summary>Logger scoped to this window.</summary>
    private readonly ILogger _logger = null!;

    /// <summary>Shows modal dialogs such as confirmations and errors.</summary>
    private readonly IMessageBoxService _messageBoxService = null!;

    /// <summary>Opens links in the default browser.</summary>
    private readonly IUrlOpener _urlOpener = null!;

    /// <summary>Captures the active window for the F8 screenshot shortcut.</summary>
    private readonly IScreenshotService _screenshotService = null!;

    // Summary Stats
    /// <summary>Measures the elapsed time of the current operation.</summary>
    private readonly Stopwatch _operationStopwatch = new();

    /// <summary>Updates the elapsed-time and disk-speed display every second.</summary>
    private readonly DispatcherTimer _processingTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Updates the managed-memory display every two seconds.</summary>
    private readonly DispatcherTimer _memoryTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>Total number of files reported for the current operation.</summary>
    private int _uiTotalFiles;

    /// <summary>Number of files processed successfully in the current operation.</summary>
    private int _uiSuccessCount;

    /// <summary>Number of files that failed in the current operation.</summary>
    private int _uiFailedCount;

    /// <summary>Number of files skipped in the current operation.</summary>
    private int _uiSkippedCount;

    /// <summary>Indicates whether a batch operation is currently running.</summary>
    private bool _isOperationRunning;

    /// <summary>Indicates that the window is closing without further confirmation.</summary>
    private bool _isForceClosing;

    /// <summary>Indicates that a close negotiation is already in progress.</summary>
    private bool _isClosingInProgress;

    /// <summary>Number of processed files that were not valid Xbox ISOs.</summary>
    private int _invalidIsoErrorCount;

    /// <summary>Total number of files processed in the current operation.</summary>
    private int _totalProcessedFiles;

    /// <summary>Paths of the files that failed in the current operation.</summary>
    private readonly HashSet<string> _failedFilePaths = new(StringComparer.OrdinalIgnoreCase);

    // Image Explorer State
    /// <summary>Explorer for the image currently open in the explorer view.</summary>
    private IImageExplorer? _explorer;

    /// <summary>Guards access to <see cref="_explorer"/>.</summary>
    private readonly Lock _explorerLock = new();

    // Serializes CopyOut with explorer disposal: an explorer is only disposed once any
    // background copy-out still using it has finished.
    /// <summary>Serializes explorer use with explorer retirement.</summary>
    private readonly SemaphoreSlim _explorerUseLock = new(1, 1);

    /// <summary>Directory currently shown in the explorer view.</summary>
    private string _currentInternalPath = "/";

    /// <summary>Set once the constructor finished so XAML-driven events can be ignored during load.</summary>
    private readonly bool _isUiInitialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class for the XAML designer;
    /// use the dependency-injection constructor at runtime.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class with all of its
    /// runtime dependencies.
    /// </summary>
    /// <param name="updateChecker">Checks GitHub for a newer release.</param>
    /// <param name="logger">Serilog logger for the window and its services.</param>
    /// <param name="messageBoxService">Service used to show modal dialogs.</param>
    /// <param name="urlOpener">Service used to open links in the default browser.</param>
    /// <param name="screenshotService">Service that captures the active window (F8).</param>
    /// <param name="orchestratorService">Batch conversion and integrity-test orchestrator.</param>
    /// <param name="diskMonitorService">Live disk read/write speed monitor.</param>
    public MainWindow(IUpdateChecker updateChecker, ILogger logger,
        IMessageBoxService messageBoxService, IUrlOpener urlOpener, IScreenshotService screenshotService,
        IOrchestratorService orchestratorService, IDiskMonitorService diskMonitorService)
        : this()
    {
        _updateChecker = updateChecker;
        _logger = logger.ForContext<MainWindow>();
        _messageBoxService = messageBoxService;
        _urlOpener = urlOpener;
        _screenshotService = screenshotService;
        _orchestratorService = orchestratorService;
        _diskMonitorService = diskMonitorService;

        InitializeFileLists();

        // Display every Serilog event in the on-screen log viewer.
        UiLogSink.MessageLogged += OnLogMessage;
        Closed += MainWindow_Closed;

        _processingTimer.Tick += ProcessingTimer_Tick;
        _memoryTimer.Tick += MemoryTimer_Tick;
        _logFlushTimer.Tick += LogFlushTimer_Tick;
        _logFlushTimer.Start();

        ResetSummaryStats();
        DisplayInstructions.Initialize(_logger);
        DisplayInstructions.DisplayInitialInstructions();

        _isUiInitialized = true;
    }

    /// <summary>Detaches the log-viewer subscription when the window closes.</summary>
    /// <param name="sender">The window that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        UiLogSink.MessageLogged -= OnLogMessage;
        _logFlushTimer.Stop();
    }

    /// <summary>
    /// Called by <see cref="UiLogSink"/> for every log event. The rendered line is queued in a
    /// bounded buffer; the UI is updated by <see cref="_logFlushTimer"/> in throttled batches,
    /// so a logging burst can neither queue one dispatcher operation per line nor freeze the
    /// window with an ever-growing text control.
    /// </summary>
    /// <param name="sender">The log sink that raised the event.</param>
    /// <param name="e">The pre-formatted log message.</param>
    private void OnLogMessage(object? sender, UiLogSink.LogMessageEventArgs e)
    {
        _logBuffer.Enqueue(e.Message);
    }

    /// <summary>
    /// Appends a bounded batch of pending lines to the viewer and drops the oldest lines
    /// beyond the display limit. One timer tick updates the viewer once instead of once per
    /// line, and only a limited number of lines is drained per tick, so the UI thread always
    /// stays free for input, rendering, and the Cancel button.
    /// </summary>
    /// <param name="sender">The timer that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void LogFlushTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var drained = _logBuffer.Drain(MaxLogLinesPerFlush);
            if (drained.Count == 0) return;

            if (_logBuffer.Commit(drained))
            {
                // Lines were dropped: rebuild the viewer once from the bounded buffer.
                LogViewer.Text = string.Join(Environment.NewLine, _logBuffer.Snapshot()) + Environment.NewLine;
            }
            else
            {
                LogViewer.Text = (LogViewer.Text ?? string.Empty) +
                                 string.Join(Environment.NewLine, drained) + Environment.NewLine;
            }

            // Keep the caret at the end so the view scrolls to the newest line.
            LogViewer.CaretIndex = LogViewer.Text?.Length ?? 0;
        }
        catch (Exception ex)
        {
            // Never log this through Serilog: the UI sink is part of the same pipeline,
            // so a persistent viewer failure would feed back into this method forever.
            Serilog.Debugging.SelfLog.WriteLine(
                "Failed to append messages to the on-screen log viewer: {0}", ex);
        }
    }

    /// <summary>Clears the viewer and the bounded line buffer (for example when a batch starts).</summary>
    private void ClearLogViewer()
    {
        _logBuffer.Clear();
        LogViewer.Text = string.Empty;
    }

    /// <summary>Sets the initial navigation style and starts the update check once the window is loaded.</summary>
    /// <param name="sender">The window that raised the event.</param>
    /// <param name="e">The event data.</param>
    private async void Window_LoadedAsync(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Set initial navigation button style
            UpdateNavigationButtonStyles(BtnNavConvert);

            try
            {
                await CheckForUpdatesAsync();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error checking for updates");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error setting initial navigation button style");
        }
    }

    /// <summary>Negotiates the close asynchronously instead of closing synchronously.</summary>
    /// <param name="sender">The window that raised the event.</param>
    /// <param name="e">The closing event data whose cancellation flag is set.</param>
    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        try
        {
            if (_isForceClosing) return;

            // A close is already being negotiated (for example the Exit button was clicked
            // while the confirmation dialog is open): keep vetoing without starting a second flow.
            if (_isClosingInProgress)
            {
                e.Cancel = true;
                return;
            }

            // Never close synchronously: when an operation is running this handler needs
            // an asynchronous confirmation, and Avalonia does not pump a nested loop.
            e.Cancel = true;
            _isClosingInProgress = true;
            _ = HandleClosingAsync();
        }
        catch (Exception ex)
        {
            _isClosingInProgress = false;
            _logger.Error(ex, "Error while closing the main window");
        }
    }

    /// <summary>Asks for confirmation when an operation is running, then closes the window.</summary>
    private async Task HandleClosingAsync()
    {
        try
        {
            if (_isOperationRunning)
            {
                var result = await _messageBoxService.ShowAsync("An operation is still running. Exit anyway?",
                    "Warning",
                    UiMessageBoxButton.YesNo, UiMessageBoxImage.Warning);
                if (result != UiMessageBoxResult.Yes)
                {
                    // The user chose to keep working: allow a future close attempt.
                    _isClosingInProgress = false;
                    return;
                }

                await WaitForOperationAndCloseAsync();
                return;
            }

            // No operation running, safe to close immediately
            CleanupResources();
            _isForceClosing = true;
            Close();
        }
        catch (Exception ex)
        {
            _isClosingInProgress = false;
            _logger.Error(ex, "Error while handling window close");
        }
    }

    /// <summary>Cancels the running operation and waits briefly for it before closing the window.</summary>
    private async Task WaitForOperationAndCloseAsync()
    {
        try
        {
            _logger.Information("Waiting for current operation to cancel before exiting...");

            // Signal the operation to stop
            _cts.Cancel();

            // Wait up to 10 seconds for the operation to complete
            var completedTask = await Task.WhenAny(_operationCompletedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));

            var timedOut = completedTask != _operationCompletedTcs.Task;
            if (timedOut)
            {
                // Closing during a long operation is a normal exit path, not a defect:
                // keep it below the automatic bug-report threshold.
                _logger.Information("Operation did not complete within timeout. Closing anyway.");
            }
            else
            {
                _logger.Information("Operation completed. Closing application...");
            }

            // Now perform cleanup and close on the UI thread
            if (timedOut)
            {
                // Arm the process-level exit before awaiting the dispatcher: if the UI
                // thread is blocked by a modal dialog, the continuation after InvokeAsync
                // never runs, so the watchdog must already be scheduled.
                var dispatcherCompleted = 0;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(5000);
                    if (Volatile.Read(ref dispatcherCompleted) == 0)
                    {
                        Environment.Exit(0);
                    }
                });

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _isForceClosing = true;
                    CleanupResources();
                    Close();
                    Volatile.Write(ref dispatcherCompleted, 1);
                });
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _isForceClosing = true;
                CleanupResources();
                Close();
            });
        }
        catch (Exception ex)
        {
            // The window did not close: allow a future close attempt.
            _isClosingInProgress = false;
            _logger.Error(ex, "Error while waiting for the current operation to cancel");
        }
    }

    /// <summary>Stops the timers and disk monitoring and disposes the explorer and cancellation token source.</summary>
    private void CleanupResources()
    {
        try
        {
            IImageExplorer? explorer;
            lock (_explorerLock)
            {
                explorer = _explorer;
                _explorer = null;
            }

            // Deferred: a background copy-out may still be reading from the explorer.
            RetireExplorer(explorer);

            _processingTimer.Stop();
            _memoryTimer.Stop();
            StopPerformanceCounter();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error during resource cleanup");
        }

        try
        {
            // Cancel only, never dispose: a timed-out operation may still be registering
            // on these tokens, and disposal would make it throw ObjectDisposedException.
            // The process is exiting, so leaving the sources undisposed is harmless.
            _cts.Cancel();
            _explorerCts.Cancel();
        }
        catch (Exception ex)
        {
            // Ignore cancellation errors during shutdown
            _logger.Debug(ex, "Ignoring cancellation token error during shutdown");
        }
    }

    /// <summary>Updates the memory usage display.</summary>
    /// <param name="sender">The timer that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void MemoryTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var memoryMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
            MemoryTextBlock.Text = $"Memory: {memoryMb:F1} MB";
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Error while updating the memory timer UI");
        }
    }

    /// <summary>Closes the window so the normal closing negotiation runs.</summary>
    /// <param name="sender">The menu item that raised the event.</param>
    /// <param name="e">The event data.</param>
    private void ExitMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        // Close the window instead of calling desktop.Shutdown(): Shutdown() forces the
        // window closed even when Window_Closing cancels, which bypasses the
        // "operation still running" confirmation. Close() lets the handler veto.
        Close();
    }

    /// <summary>Handles the F8 screenshot shortcut.</summary>
    /// <param name="sender">The window that raised the event.</param>
    /// <param name="e">The key event data.</param>
    private async void Window_KeyDownAsync(object? sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.F8)
            {
                e.Handled = true;
                var filePath = await _screenshotService.CaptureActiveWindowAsync();
                if (filePath is not null)
                {
                    _logger.Information("Screenshot captured: {FilePath}", filePath);
                }
                else
                {
                    // The service already logged why; tell the user instead of failing silently.
                    _logger.Information("Screenshot could not be captured.");
                    await _messageBoxService.ShowErrorAsync(
                        "The screenshot could not be saved. Please check that the application folder or your " +
                        "user data folder is writable and try again.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in method Window_KeyDownAsync");
        }
    }
}