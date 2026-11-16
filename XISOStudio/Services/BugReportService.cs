using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using XISOStudio.Interfaces;
using Serilog;

namespace XISOStudio.Services;

/// <summary>
/// Sends bug reports to the configured Bug Report API, enriching each report with
/// environment details and, optionally, the exception that triggered it.
/// </summary>
public class BugReportService : IBugReportService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _applicationName;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BugReportService"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client used to post reports; it is never mutated by this service.</param>
    /// <param name="apiUrl">Absolute URL of the Bug Report API endpoint.</param>
    /// <param name="apiKey">API key sent in the <c>X-API-KEY</c> header.</param>
    /// <param name="applicationName">Application name included in every report.</param>
    /// <param name="logger">Optional logger used to record request failures.</param>
    public BugReportService(HttpClient httpClient, string apiUrl, string apiKey, string applicationName,
        ILogger? logger = null)
    {
        _apiUrl = apiUrl;
        _apiKey = apiKey;
        _applicationName = applicationName;

        // The injected client is never mutated: headers and the timeout are applied per
        // request so a shared or reused client cannot be corrupted by this service.
        _httpClient = httpClient;
        _logger = logger?.ForContext<BugReportService>();
    }

    /// <summary>
    /// Sends a bug report containing the specified message together with environment details.
    /// </summary>
    /// <param name="message">Message describing the problem.</param>
    /// <returns><c>true</c> when the API accepted the report; otherwise <c>false</c>.</returns>
    /// <remarks>Argument failures surface as a faulted task, never as a synchronous throw.</remarks>
    public async Task<bool> SendBugReportAsync(string message)
    {
        var fullMessage = BuildFullMessage(message);
        var version = GetApplicationVersion.GetProgramVersion();
        return await SendToApiAsync(fullMessage, version, null).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a bug report containing the specified error message, environment details, and
    /// the formatted details of the supplied exception.
    /// </summary>
    /// <param name="errorMessage">Message describing the problem.</param>
    /// <param name="exception">Exception whose details are appended to the report.</param>
    /// <returns><c>true</c> when the API accepted the report; otherwise <c>false</c>.</returns>
    /// <remarks>Argument failures surface as a faulted task, never as a synchronous throw.</remarks>
    public async Task<bool> SendBugReportAsync(string errorMessage, Exception exception)
    {
        var sb = new StringBuilder(BuildFullMessage(errorMessage));
        sb.AppendLine();
        sb.AppendLine("=== Exception Details ===");
        ExceptionFormatter.AppendExceptionDetails(sb, exception);
        var version = GetApplicationVersion.GetProgramVersion();
        return await SendToApiAsync(sb.ToString(), version, exception).ConfigureAwait(false);
    }

    /// <summary>
    /// Posts a fully assembled report to the Bug Report API and reports whether the call
    /// succeeded.
    /// </summary>
    /// <param name="fullMessage">Complete report body, including the environment details.</param>
    /// <param name="version">Application version included in the payload.</param>
    /// <param name="exception">Exception that triggered the report, or <c>null</c> when there is none.</param>
    /// <returns><c>true</c> when the API accepted the report; otherwise <c>false</c>.</returns>
    private async Task<bool> SendToApiAsync(string fullMessage, string version, Exception? exception)
    {
        try
        {
            var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                { "message", fullMessage },
                { "applicationName", _applicationName },
                { "version", version },
                { "environment", GetEnvironmentSummary() },
                { "stackTrace", exception?.StackTrace }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl);
            request.Headers.Add("X-API-KEY", _apiKey);
            request.Content = JsonContent.Create(payload);

            using var timeoutCts = new CancellationTokenSource(RequestTimeout);
            // ConfigureAwait(false) is required: TryReportFatal blocks the UI thread with
            // Wait() on this task, and a captured synchronization context would deadlock
            // until the timeout (the report would never be sent).
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return true;

            // Record the failure so "the error was reported" is never silently false.
            _logger?.Information("Bug report API returned {StatusCode}.", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.Information(ex, "Failed to send bug report to {ApiUrl}.", _apiUrl);
            return false;
        }
    }

    /// <summary>
    /// Builds the full report body by appending the message after the environment details
    /// and error header, unless the message already contains environment details.
    /// </summary>
    /// <param name="message">Report message to enrich.</param>
    /// <returns>The complete report body.</returns>
    internal static string BuildFullMessage(string message)
    {
        if (message.Contains("=== Environment Details ===", StringComparison.OrdinalIgnoreCase))
        {
            return message;
        }

        var sb = new StringBuilder();
        AppendEnvironmentDetails(sb);
        sb.AppendLine();
        sb.AppendLine("=== Error Details ===");
        sb.AppendLine(message);

        return sb.ToString();
    }

    /// <summary>
    /// Appends the environment details block to the report builder.
    /// </summary>
    /// <param name="sb">Report builder that receives the environment details.</param>
    private static void AppendEnvironmentDetails(StringBuilder sb)
    {
        sb.AppendLine("=== Environment Details ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application Name: {App.ApplicationName}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Application Version: {GetApplicationVersion.GetProgramVersion()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS Version: {Environment.OSVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Bitness: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"{GetPlatformVersionLabel()}: {GetPlatformVersion()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Processor Count: {Environment.ProcessorCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Base Directory: {AppContext.BaseDirectory}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Temp Path: {Path.GetTempPath()}");
    }

    /// <summary>
    /// Returns the platform-specific label for the operating system version line, so a
    /// report from Linux or macOS is not mislabeled as a Windows version.
    /// </summary>
    /// <returns>Label matching the current operating system.</returns>
    internal static string GetPlatformVersionLabel()
    {
        if (OperatingSystem.IsWindows()) return "Windows Version";
        if (OperatingSystem.IsMacOS()) return "MacOsX Version";
        if (OperatingSystem.IsLinux()) return "Linux Version";
        return "OS Platform Version";
    }

    /// <summary>
    /// Returns the operating system version, falling back to the full OS description when
    /// the reported version is 0.0.0.0.
    /// </summary>
    /// <returns>Platform version string included in the environment details.</returns>
    private static string GetPlatformVersion()
    {
        var version = Environment.OSVersion.Version;

        // Some Unix distributions report 0.0.0.0; fall back to the full OS description so
        // the report still carries a useful platform version.
        return version == new Version(0, 0, 0, 0) ? Environment.OSVersion.ToString() : version.ToString();
    }

    /// <summary>
    /// Builds a short OS and bitness summary, truncated to the maximum length accepted by
    /// the API.
    /// </summary>
    /// <returns>Environment summary no longer than 50 characters.</returns>
    private static string GetEnvironmentSummary()
    {
        var bitness = Environment.Is64BitProcess ? "64-bit" : "32-bit";
        var summary = $"{Environment.OSVersion} {bitness}";
        return summary.Length <= 50 ? summary : summary[..50];
    }
}