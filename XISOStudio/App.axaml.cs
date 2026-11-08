using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using XISOStudio.Interfaces;
using XISOStudio.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace XISOStudio;

/// <summary>
/// Application entry point: configures the Serilog pipeline, the dependency injection
/// container, and the global unhandled-exception handlers.
/// </summary>
public class App : Application
{
    private const string BugReportApiUrl = "https://www.purelogiccode.com/bugreport/api/send-bug-report";
    private const string BugReportApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";
    private const string StatsApiUrl = "https://www.purelogiccode.com/ApplicationStats/stats";

    /// <summary>Name reported to the bug report and application statistics APIs.</summary>
    public const string ApplicationName = "XISOStudio";

    private static IBugReportService? _bugReportService;
    private IStatsService? _statsService;
    private static IServiceProvider? ServiceProvider { get; set; }
    private IMessageBoxService? _messageBoxService;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private static bool _isLoggingConfigured;

    /// <summary>
    /// Initializes a new instance of the <see cref="App"/> class and bootstraps the
    /// Serilog pipeline before the UI starts.
    /// </summary>
    public App()
    {
        ConfigureLogging();

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    /// <summary>
    /// Bootstraps the Serilog pipeline and routes Avalonia's internal diagnostics through it.
    /// Called from <c>Program.Main</c> before Avalonia initializes its platform subsystems so
    /// framework diagnostics emitted during platform startup are captured as well; the call
    /// from the constructor is a no-op in that case. Warning and above are forwarded to the
    /// Bug Report API by the <see cref="BugReportSink"/>; the service is resolved lazily
    /// because the dependency injection container is built later during startup.
    /// </summary>
    public static void ConfigureLogging()
    {
        if (_isLoggingConfigured) return;

        _isLoggingConfigured = true;

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationName,
            "logs",
            "log-.txt");

        ConfigureSelfLog();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Ui(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.BugReport(() => _bugReportService, LogEventLevel.Warning, CultureInfo.InvariantCulture)
            .WriteTo.File(
                logPath,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14)
            .CreateLogger();

        // Route Avalonia's internal diagnostics through the same Serilog pipeline.
        Avalonia.Logging.Logger.Sink = new AvaloniaSerilogSink(Log.Logger);

        Log.Information("XISOStudio v{Version} starting", GetApplicationVersion.GetProgramVersion());
    }

    /// <summary>
    /// Routes Serilog's SelfLog to a dedicated file. The UI and bug-report sinks promise to
    /// record failures there (they cannot write to the logging pipeline without recursing);
    /// without an enabled listener those diagnostics are silently discarded.
    /// </summary>
    private static void ConfigureSelfLog()
    {
        try
        {
            var selfLogPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationName,
                "logs",
                "selflog.txt");

            var directory = Path.GetDirectoryName(selfLogPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            Serilog.Debugging.SelfLog.Enable(message =>
            {
                try
                {
                    File.AppendAllText(selfLogPath,
                        $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {message}{Environment.NewLine}");
                }
                catch
                {
                    // SelfLog must never throw: failures here have nowhere left to go.
                }
            });
        }
        catch
        {
            // Never let diagnostics setup break application startup.
        }
    }

    /// <summary>
    /// Loads the application XAML resources.
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Starts the desktop lifetime, wires the unhandled-exception handlers, and begins
    /// the asynchronous service startup.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.Exit += OnDesktopExit;
            Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;

            _ = StartAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            ServiceProvider = serviceCollection.BuildServiceProvider();

            _bugReportService = ServiceProvider.GetRequiredService<IBugReportService>();
            _messageBoxService = ServiceProvider.GetRequiredService<IMessageBoxService>();
            _statsService = ServiceProvider.GetRequiredService<IStatsService>();

            _ = _statsService?.SendStatsAsync();

            // Create and show the main window with enhanced error handling
            try
            {
                var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
                desktop.MainWindow = mainWindow;
                mainWindow.Show();

                // Startup cleanup is best-effort and must never delay the window: probing
                // idle drives can block for many seconds, so run it after the window is
                // visible and off the UI thread.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await TempFolderCleanupHelper.CleanupTempFoldersAsync(Log.Logger);
                    }
                    catch (Exception cleanupEx)
                    {
                        Log.Warning(cleanupEx, "Startup temporary folder cleanup failed");
                    }
                });
            }
            catch (SEHException sehEx)
            {
                // Handle font/rendering issues gracefully
                await HandleFontRenderingErrorAsync(sehEx);
            }
            catch (InvalidOperationException opEx) when (opEx.Message.Contains("font",
                                                             StringComparison.OrdinalIgnoreCase) ||
                                                         opEx.Message.Contains("FontFamily",
                                                             StringComparison.OrdinalIgnoreCase))
            {
                // Handle font-related InvalidOperationException
                await HandleFontRenderingErrorAsync(opEx);
            }
            catch (NullReferenceException nullEx)
            {
                // Handle UI initialization failures
                Log.Error(nullEx, "UI initialization error during startup");
                await ReportExceptionAsync(nullEx, "Bug OnStartup - UI Initialization Error");
            }
        }
        catch (SEHException sehEx)
        {
            // Handle SEH exceptions during service setup
            try
            {
                await HandleFontRenderingErrorAsync(sehEx);
            }
            catch (Exception fontEx)
            {
                Log.Fatal(fontEx, "Failed to handle font/rendering error during startup");
            }
        }
        catch (Exception ex)
        {
            try
            {
                await ReportExceptionAsync(ex, "Bug OnStartup", isFatal: true);
            }
            catch (Exception reportEx)
            {
                Log.Fatal(reportEx, "Failed to report startup error");
            }
        }
    }

    private async Task HandleFontRenderingErrorAsync(Exception ex)
    {
        Log.Error(ex, "Font/Rendering error during startup");

        var errorMessage =
            "The application encountered a font or rendering error during startup.\n\n" +
            "This issue commonly occurs when:\n" +
            "- System fonts are missing or corrupted\n" +
            "- The graphics driver or rendering backend is unavailable\n\n" +
            "Recommended solutions:\n" +
            "1. Ensure the standard system fonts are installed\n" +
            "2. On Linux: install a font package (for example 'ttf-dejavu' or 'ttf-mscorefonts-installer')\n" +
            "3. Update your graphics drivers\n" +
            "4. On Windows: Run 'sfc /scannow' to repair system files\n\n" +
            $"Technical details: {ex.GetType().Name}\n" +
            $"Error: {ex.Message}";

        if (_messageBoxService != null)
        {
            await _messageBoxService.ShowErrorAsync(errorMessage);
        }

        // Report this critical error directly as well: the process is about to shut
        // down, so the fire-and-forget sink may not get a chance to deliver it.
        TryReportFatal("Bug OnStartup - FontRenderingError", ex);
        _desktop?.Shutdown(1);
    }

    private static void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (ServiceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        Log.CloseAndFlush();

        // Safety net: if something blocks the UI thread (e.g., a lingering message box
        // or a stuck async operation), force-kill the process after a few seconds so the
        // application does not remain open in the background.
        ThreadPool.QueueUserWorkItem(static _ =>
        {
            Thread.Sleep(5000);
            Environment.Exit(0);
        });
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddHttpClient("BugReport", static client => client.BaseAddress = new Uri(BugReportApiUrl))
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
                { PooledConnectionLifetime = TimeSpan.FromMinutes(10) });
        services.AddHttpClient("Stats", static client => client.BaseAddress = new Uri(StatsApiUrl))
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
                { PooledConnectionLifetime = TimeSpan.FromMinutes(10) });
        services.AddHttpClient("UpdateChecker")
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
                { PooledConnectionLifetime = TimeSpan.FromMinutes(10) });

        // Serilog is the single logging pipeline: services receive Serilog.ILogger and
        // warning-or-higher events are forwarded to the Bug Report API by the sink.
        services.AddSingleton(Log.Logger);

        services.AddSingleton<IBugReportService>(static provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            return new BugReportService(httpClientFactory.CreateClient("BugReport"), BugReportApiUrl, BugReportApiKey,
                ApplicationName, provider.GetRequiredService<ILogger>());
        });
        services.AddSingleton<IStatsService>(static provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            return new StatsService(httpClientFactory.CreateClient("Stats"), StatsApiUrl, BugReportApiKey,
                ApplicationName, provider.GetRequiredService<ILogger>());
        });
        services.AddSingleton<IUpdateChecker>(static provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            return new UpdateChecker(httpClientFactory.CreateClient("UpdateChecker"),
                provider.GetRequiredService<ILogger>());
        });
        services.AddSingleton<IDiskMonitorService, DiskMonitorService>();
        services.AddSingleton<IMessageBoxService, MessageBoxService>();
        services.AddSingleton<IUrlOpener, UrlOpenerService>();
        services.AddSingleton<IScreenshotService, ScreenshotService>();
        services.AddTransient<IFileExtractor>(static provider =>
            new FileExtractorService(provider.GetRequiredService<ILogger>()));
        services.AddTransient<IFileMover>(static provider =>
            new FileMoverService(provider.GetRequiredService<ILogger>(),
                provider.GetRequiredService<IDiskMonitorService>()));
        services.AddTransient<AboutWindow>();
        services.AddSingleton<IConversionValidationService, ConversionValidationService>();
        services.AddSingleton<IImageHashService, ImageHashService>();
        services.AddSingleton<IImageConversionService, ImageConversionService>();
        services.AddSingleton<IXisoRecoveryService, XisoRecoveryService>();
        services.AddSingleton<IXisoSplitService, XisoSplitService>();
        services.AddSingleton<IXisoSharpService>(static provider =>
            new XisoSharpService(provider.GetRequiredService<ILogger>(),
                provider.GetRequiredService<IDiskMonitorService>(),
                provider.GetRequiredService<IConversionValidationService>()));
        services.AddSingleton<IChdService>(static provider =>
            new ChdService(provider.GetRequiredService<ILogger>(),
                provider.GetRequiredService<IXisoSharpService>()));
        services.AddSingleton<IXisoIntegrityService>(static provider =>
            new XisoIntegrityService(provider.GetRequiredService<ILogger>()));
        services.AddSingleton<IOrchestratorService, OrchestratorService>();
        services.AddTransient<MainWindow>();
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Fatal(exception, "AppDomain.UnhandledException");
            TryReportFatal("AppDomain.UnhandledException", exception);
        }
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Application.DispatcherUnhandledException");
        e.Handled = true;

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                if (_messageBoxService != null)
                {
                    await _messageBoxService.ShowErrorAsync(
                        "A critical error occurred and has been reported. The application may need to close.");
                }
            }
            catch (Exception displayEx)
            {
                Log.Error(displayEx, "Failed to show the unhandled-exception message box");
            }
        });
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    private async Task ReportExceptionAsync(Exception exception, string source, bool isFatal = false)
    {
        try
        {
            if (isFatal)
            {
                Log.Fatal(exception, "{Source}", source);
                TryReportFatal(source, exception);
            }
            else
            {
                Log.Error(exception, "{Source}", source);
            }

            if (_messageBoxService != null)
            {
                await _messageBoxService.ShowErrorAsync(
                    "A critical error occurred and has been reported. The application may need to close.");
            }
        }
        catch (Exception displayEx)
        {
            // Showing the dialog failed (for example the window is already gone); record it
            // so the failure is never completely silent.
            Log.Error(displayEx, "Failed to show the error message for {Source}", source);
        }
    }

    /// <summary>
    /// Sends a bug report directly (bypassing the Serilog sink) so that even if the
    /// logging pipeline has already shut down the report is still delivered. Blocks
    /// for at most 5 seconds waiting for the report to be sent.
    /// </summary>
    /// <param name="source">Short label describing where the exception was caught.</param>
    /// <param name="exception">Exception that is being reported.</param>
    private static void TryReportFatal(string source, Exception exception)
    {
        try
        {
            if (_bugReportService == null) return;

            _bugReportService.SendBugReportAsync(source, exception).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception reportEx)
        {
            // Silently ignore any errors in the reporting process, but keep a trace of them.
            Log.Warning(reportEx, "Failed to send a bug report for {Source}", source);
        }
    }
}