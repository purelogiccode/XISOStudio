using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using XISOStudio.Interfaces;
using XISOStudio.Services;
using Moq;
using Serilog;
using Serilog.Events;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests the Serilog configuration extensions that register the UI and bug report sinks.</summary>
[SuppressMessage("ReSharper", "AccessToDisposedClosure")]
public class LoggingSinkExtensionsTests
{
    private static readonly Regex TimestampedHello = new(
        @"^\[\d{2}:\d{2}:\d{2}\] Hello world$", RegexOptions.NonBacktracking);

    [Fact]
    public void UiReturnsSameLoggerConfigurationForChaining()
    {
        var configuration = new LoggerConfiguration();

        var result = configuration.WriteTo.Ui();

        Assert.Same(configuration, result);
    }

    [Fact]
    public void UiForwardsMessagesToMessageLogged()
    {
        var messages = new List<string>();
        using var subscription = Subscribe((_, e) => messages.Add(e.Message));
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Ui()
            .CreateLogger();

        logger.Information("Hello {Name}", "world");

        Assert.Contains(messages, static m => m.Contains("Hello world", StringComparison.Ordinal));
    }

    [Fact]
    public void UiRaisesTimestampedLine()
    {
        var messages = new List<string>();
        using var subscription = Subscribe((_, e) => messages.Add(e.Message));
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Ui()
            .CreateLogger();

        logger.Information("Hello {Name}", "world");

        var message = Assert.Single(messages);
        Assert.Matches(TimestampedHello, message);
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose)]
    [InlineData(LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Information)]
    [InlineData(LogEventLevel.Warning)]
    [InlineData(LogEventLevel.Error)]
    [InlineData(LogEventLevel.Fatal)]
    public void UiForwardsEveryLevelWhenUnrestricted(LogEventLevel level)
    {
        var messages = new List<string>();
        using var subscription = Subscribe((_, e) => messages.Add(e.Message));
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Ui()
            .CreateLogger();

        logger.Write(level, "level {Level}", level);

        Assert.Single(messages);
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose)]
    [InlineData(LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Information)]
    public void UiRestrictedToWarningSuppressesLowerLevels(LogEventLevel level)
    {
        var messages = new List<string>();
        using var subscription = Subscribe((_, e) => messages.Add(e.Message));
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Ui(LogEventLevel.Warning)
            .CreateLogger();

        logger.Write(level, "suppressed");

        Assert.Empty(messages);
    }

    [Fact]
    public void UiRestrictedToWarningForwardsWarning()
    {
        var messages = new List<string>();
        using var subscription = Subscribe((_, e) => messages.Add(e.Message));
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Ui(LogEventLevel.Warning)
            .CreateLogger();

        logger.Warning("forwarded warning");

        Assert.Contains(messages, static m => m.Contains("forwarded warning", StringComparison.Ordinal));
    }

    [Fact]
    public void UiRestrictedToInformationSuppressesDebug()
    {
        var messages = new List<string>();
        using var subscription = Subscribe((_, e) => messages.Add(e.Message));
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Ui(LogEventLevel.Information)
            .CreateLogger();

        logger.Debug("suppressed debug");

        Assert.Empty(messages);
    }

    [Fact]
    public void UiRestrictedToInformationForwardsInformation()
    {
        var messages = new List<string>();
        using var subscription = Subscribe((_, e) => messages.Add(e.Message));
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Ui(LogEventLevel.Information)
            .CreateLogger();

        logger.Information("forwarded information");

        Assert.Contains(messages, static m => m.Contains("forwarded information", StringComparison.Ordinal));
    }

    [Fact]
    public void UiHonorsFormatProvider()
    {
        var messages = new List<string>();
        using var subscription = Subscribe((_, e) => messages.Add(e.Message));
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Ui(formatProvider: CultureInfo.GetCultureInfo("fr-FR"))
            .CreateLogger();

        logger.Information("Value {Value:0.0}", 1.5);

        Assert.Contains(messages, static m => m.Contains("Value 1,5", StringComparison.Ordinal));
    }

    [Fact]
    public void UiLogsMultipleEventsInOrder()
    {
        var messages = new List<string>();
        using var subscription = Subscribe((_, e) => messages.Add(e.Message));
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Ui()
            .CreateLogger();

        logger.Information("first");
        logger.Information("second");

        Assert.Equal(2, messages.Count);
        Assert.Contains("first", messages[0], StringComparison.Ordinal);
        Assert.Contains("second", messages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void BugReportReturnsSameLoggerConfigurationForChaining()
    {
        var configuration = new LoggerConfiguration();

        var result = configuration.WriteTo.BugReport(static () => null);

        Assert.Same(configuration, result);
    }

    [Fact]
    public async Task BugReportForwardsWarningByDefault()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bugReport = new Mock<IBugReportService>();
        bugReport.Setup(static b => b.SendBugReportAsync(It.IsAny<string>()))
            .Callback(() => tcs.TrySetResult(true))
            .ReturnsAsync(true);
        await using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.BugReport(() => bugReport.Object)
            .CreateLogger();

        logger.Warning("Forwarded warning");

        Assert.True(await WaitForAsync(tcs), "The warning was not forwarded to the bug report service");
        bugReport.Verify(
            static b => b.SendBugReportAsync(It.Is<string>(m => m.Contains("Forwarded warning"))),
            Times.Once);
    }

    [Fact]
    public async Task BugReportForwardsError()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bugReport = new Mock<IBugReportService>();
        bugReport.Setup(static b => b.SendBugReportAsync(It.IsAny<string>()))
            .Callback(() => tcs.TrySetResult(true))
            .ReturnsAsync(true);
        await using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.BugReport(() => bugReport.Object)
            .CreateLogger();

        logger.Error("Forwarded error");

        Assert.True(await WaitForAsync(tcs), "The error was not forwarded to the bug report service");
        bugReport.Verify(
            static b => b.SendBugReportAsync(It.Is<string>(m => m.Contains("Forwarded error"))),
            Times.Once);
    }

    [Fact]
    public async Task BugReportForwardsErrorWithException()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bugReport = new Mock<IBugReportService>();
        bugReport.Setup(static b => b.SendBugReportAsync(It.IsAny<string>(), It.IsAny<Exception>()))
            .Callback(() => tcs.TrySetResult(true))
            .ReturnsAsync(true);
        await using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.BugReport(() => bugReport.Object)
            .CreateLogger();

        logger.Error(new InvalidOperationException("boom"), "Forwarded error with exception");

        Assert.True(await WaitForAsync(tcs), "The error was not forwarded to the bug report service");
        bugReport.Verify(
            static b => b.SendBugReportAsync(
                It.Is<string>(m => m.Contains("Forwarded error with exception")),
                It.Is<Exception>(e => e.Message == "boom")),
            Times.Once);
    }

    [Fact]
    public void BugReportIgnoresInformation()
    {
        var bugReport = new Mock<IBugReportService>();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.BugReport(() => bugReport.Object)
            .CreateLogger();

        logger.Information("Just a progress message");

        bugReport.Verify(static b => b.SendBugReportAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void BugReportIgnoresDebug()
    {
        var bugReport = new Mock<IBugReportService>();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.BugReport(() => bugReport.Object)
            .CreateLogger();

        logger.Debug("Just a diagnostic message");

        bugReport.Verify(static b => b.SendBugReportAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void BugReportWithNullAccessorDoesNotThrow()
    {
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.BugReport(static () => null)
            .CreateLogger();

        var exception = Record.Exception(() => logger.Warning("No service available yet"));

        Assert.Null(exception);
    }

    [Fact]
    public void BugReportRestrictedToErrorSuppressesWarning()
    {
        var bugReport = new Mock<IBugReportService>();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.BugReport(() => bugReport.Object, LogEventLevel.Error)
            .CreateLogger();

        logger.Warning("Suppressed warning");

        bugReport.Verify(static b => b.SendBugReportAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BugReportRestrictedToErrorForwardsError()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bugReport = new Mock<IBugReportService>();
        bugReport.Setup(static b => b.SendBugReportAsync(It.IsAny<string>()))
            .Callback(() => tcs.TrySetResult(true))
            .ReturnsAsync(true);
        await using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.BugReport(() => bugReport.Object, LogEventLevel.Error)
            .CreateLogger();

        logger.Error("Forwarded error");

        Assert.True(await WaitForAsync(tcs), "The error was not forwarded to the bug report service");
    }

    [Fact]
    public async Task BugReportHonorsFormatProvider()
    {
        var received = new List<string>();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bugReport = new Mock<IBugReportService>();
        bugReport.Setup(static b => b.SendBugReportAsync(It.IsAny<string>()))
            .Callback<string>(m =>
            {
                received.Add(m);
                tcs.TrySetResult(true);
            })
            .ReturnsAsync(true);
        await using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.BugReport(() => bugReport.Object, formatProvider: CultureInfo.GetCultureInfo("fr-FR"))
            .CreateLogger();

        logger.Warning("Temperature {Value:0.0}", 1.5);

        Assert.True(await WaitForAsync(tcs), "The warning was not forwarded to the bug report service");
        Assert.Contains(received, static m => m.Contains("Temperature 1,5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BugReportResolvesAccessorLazily()
    {
        IBugReportService? service = null;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bugReport = new Mock<IBugReportService>();
        bugReport.Setup(static b => b.SendBugReportAsync(It.IsAny<string>()))
            .Callback(() => tcs.TrySetResult(true))
            .ReturnsAsync(true);
        await using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            // ReSharper disable once AccessToModifiedClosure
            .WriteTo.BugReport(() => service)
            .CreateLogger();

        logger.Warning("Before the service is available");
        service = bugReport.Object;
        logger.Warning("After the service is available");

        Assert.True(await WaitForAsync(tcs), "The error was not forwarded to the bug report service");
        bugReport.Verify(
            static b => b.SendBugReportAsync(It.Is<string>(m => m.Contains("Before the service"))),
            Times.Never);
        bugReport.Verify(
            static b => b.SendBugReportAsync(It.Is<string>(m => m.Contains("After the service"))),
            Times.Once);
    }

    [Fact]
    public async Task BugReportServiceFailureDoesNotThrow()
    {
        var invoked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bugReport = new Mock<IBugReportService>();
        bugReport.Setup(static b => b.SendBugReportAsync(It.IsAny<string>()))
            .Callback(() => invoked.TrySetResult(true))
            .ThrowsAsync(new HttpRequestException("network down"));
        await using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.BugReport(() => bugReport.Object)
            .CreateLogger();

        var exception = Record.Exception(() => logger.Warning("This will fail to send"));

        Assert.Null(exception);
        Assert.True(await WaitForAsync(invoked), "The sink never invoked the bug report service");
    }

    private static Subscription Subscribe(EventHandler<UiLogSink.LogMessageEventArgs> handler)
    {
        UiLogSink.MessageLogged += handler;
        return new Subscription(handler);
    }

    private static async Task<bool> WaitForAsync(TaskCompletionSource<bool> tcs)
    {
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        return completed == tcs.Task;
    }

    private sealed class Subscription(EventHandler<UiLogSink.LogMessageEventArgs> handler) : IDisposable
    {
        public void Dispose()
        {
            UiLogSink.MessageLogged -= handler;
        }
    }
}