using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using XISOStudio.Interfaces;
using Serilog;

namespace XISOStudio.Services;

/// <summary>
/// Captures a screenshot of the application's active window and saves it as a PNG file in a
/// "Screenshot" folder next to the executable. When the application folder is not writable
/// (for example a read-only Program Files or macOS bundle installation), the per-user
/// application data folder is used instead.
/// </summary>
public class ScreenshotService : IScreenshotService
{
    /// <summary>Name of the folder that receives captured screenshots.</summary>
    internal const string ScreenshotFolderName = "Screenshot";

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScreenshotService"/> class.
    /// </summary>
    /// <param name="logger">Logger used for diagnostics.</param>
    public ScreenshotService(ILogger logger)
    {
        _logger = logger.ForContext<ScreenshotService>();
    }

    /// <summary>
    /// Captures the active application window at its current device scale.
    /// </summary>
    /// <returns>The path of the saved screenshot, or <c>null</c> when no window is available or the capture fails.</returns>
    public async Task<string?> CaptureActiveWindowAsync()
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(CaptureActiveWindow);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error capturing screenshot");
            return null;
        }
    }

    private string? CaptureActiveWindow()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

        // Prefer the window the user is actually looking at (the main window or an open
        // dialog); fall back to the main window when no window reports itself as active.
        var window = lifetime?.Windows.FirstOrDefault(static w => w is { IsActive: true, IsVisible: true })
                     ?? lifetime?.MainWindow;

        if (window?.IsVisible != true)
        {
            _logger.Information("Screenshot: No active window found.");
            return null;
        }

        var width = window.ClientSize.Width;
        var height = window.ClientSize.Height;

        if (width <= 0 || height <= 0)
        {
            _logger.Information("Screenshot: Invalid window dimensions.");
            return null;
        }

        // Render at the window's device scale so the screenshot matches what is on screen.
        var scaling = window.RenderScaling;
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Round(width * scaling)),
            Math.Max(1, (int)Math.Round(height * scaling)));

        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(window);

        // The application folder is the preferred location; a read-only installation
        // (Program Files, macOS bundle, read-only mount) falls back to per-user data.
        var filePath = TrySaveScreenshot(bitmap, Path.Combine(AppContext.BaseDirectory, ScreenshotFolderName))
                       ?? TrySaveScreenshot(bitmap, GetFallbackDirectory());

        if (filePath is null)
        {
            // Both locations failed, which is environmental (permissions, read-only folder);
            // log at Information so it is not auto-uploaded as a bug report.
            _logger.Information("Screenshot could not be saved to the application folder or to {FallbackDirectory}.",
                GetFallbackDirectory());
            return null;
        }

        _logger.Information("Screenshot saved: {FilePath}", filePath);
        return filePath;
    }

    /// <summary>
    /// Returns the per-user directory used when the application folder cannot be written to.
    /// </summary>
    /// <returns>Full path of the fallback screenshot folder.</returns>
    internal static string GetFallbackDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            App.ApplicationName,
            ScreenshotFolderName);
    }

    /// <summary>
    /// Ensures the directory exists and saves the bitmap into it, reporting failure without
    /// throwing so the caller can try the fallback location.
    /// </summary>
    /// <param name="bitmap">Bitmap to save.</param>
    /// <param name="directory">Directory that receives the screenshot.</param>
    /// <returns>The saved file path, or <c>null</c> when the directory or file could not be written.</returns>
    private string? TrySaveScreenshot(RenderTargetBitmap bitmap, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // Milliseconds plus a uniqueness probe: two captures in the same second (or even
            // the same millisecond) must not overwrite each other.
            var filePath = BuildUniqueFilePath(directory, DateTime.Now);
            bitmap.Save(filePath, PngBitmapEncoderOptions.Default);
            return filePath;
        }
        catch (Exception ex)
        {
            // Environmental (read-only folder, permissions, locked file); try the next location.
            _logger.Information(ex, "Could not save screenshot to {Directory}; trying the fallback folder", directory);
            return null;
        }
    }

    /// <summary>
    /// Builds a timestamped PNG file path that does not collide with an existing file.
    /// </summary>
    /// <param name="directory">Directory that receives the screenshot.</param>
    /// <param name="timestamp">Timestamp used in the file name.</param>
    /// <returns>Full path of a screenshot file that does not exist yet.</returns>
    internal static string BuildUniqueFilePath(string directory, DateTime timestamp)
    {
        var stamp = timestamp.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var filePath = Path.Combine(directory, $"Screenshot_{stamp}.png");
        for (var counter = 2; File.Exists(filePath); counter++)
        {
            filePath = Path.Combine(directory,
                $"Screenshot_{stamp}_{counter.ToString(CultureInfo.InvariantCulture)}.png");
        }

        return filePath;
    }
}