using System.Globalization;
using XISOStudio.Services;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests that <see cref="UiLogSink"/> renders log events and raises <c>MessageLogged</c> safely.</summary>
public class UiLogSinkTests
{
    [Fact]
    public void LogMessageEventArgsExposesProvidedMessage()
    {
        var args = new UiLogSink.LogMessageEventArgs("hello");

        Assert.Equal("hello", args.Message);
    }

    [Fact]
    public void EmitWithSubscriberRaisesMessageLoggedOnce()
    {
        var (sink, _) = CreateLogger();
        var received = new List<UiLogSink.LogMessageEventArgs>();
        using var subscription = Subscribe((_, e) => received.Add(e));

        sink.Emit(CreateEvent("Single message"));

        Assert.Single(received);
    }

    [Fact]
    public void EmitMessageContainsRenderedText()
    {
        var (sink, _) = CreateLogger();
        var received = new List<UiLogSink.LogMessageEventArgs>();
        using var subscription = Subscribe((_, e) => received.Add(e));

        sink.Emit(CreateEvent("User {Name} logged in", ("Name", "alice")));

        var args = Assert.Single(received);
        Assert.Contains("User alice logged in", args.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitDoesNotQuoteStringPropertiesWithJsonQuotes()
    {
        var (sink, _) = CreateLogger();
        var received = new List<UiLogSink.LogMessageEventArgs>();
        using var subscription = Subscribe((_, e) => received.Add(e));

        sink.Emit(CreateEvent("Successfully converted '{FileName}' to XISO format.",
            ("FileName", "AFL Live.iso")));

        var args = Assert.Single(received);
        Assert.Contains("Successfully converted 'AFL Live.iso' to XISO format.", args.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"AFL Live.iso\"", args.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitMessageHasTimestampPrefix()
    {
        var (sink, _) = CreateLogger();
        var received = new List<UiLogSink.LogMessageEventArgs>();
        using var subscription = Subscribe((_, e) => received.Add(e));

        sink.Emit(CreateEvent("Timestamped"));

        var args = Assert.Single(received);
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\] Timestamped$", args.Message);
    }

    [Fact]
    public void EmitTimestampUsesEventTimestamp()
    {
        var (sink, _) = CreateLogger();
        var received = new List<UiLogSink.LogMessageEventArgs>();
        using var subscription = Subscribe((_, e) => received.Add(e));
        var timestamp = new DateTimeOffset(2026, 9, 29, 17, 4, 9, TimeSpan.Zero);

        sink.Emit(CreateEvent(timestamp, LogEventLevel.Information, "exact time"));

        var args = Assert.Single(received);
        Assert.StartsWith("[17:04:09] exact time", args.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitWithoutSubscribersDoesNotThrow()
    {
        var (sink, _) = CreateLogger();

        var exception = Record.Exception(() => sink.Emit(CreateEvent("orphan message")));

        Assert.Null(exception);
    }

    [Fact]
    public void EmitPassesNullSender()
    {
        var (sink, _) = CreateLogger();
        object? sender = new();
        using var subscription = Subscribe((s, _) => sender = s);

        sink.Emit(CreateEvent("sender check"));

        Assert.Null(sender);
    }

    [Fact]
    public void EmitAfterUnsubscribeDoesNotRaise()
    {
        var (sink, _) = CreateLogger();
        var received = new List<UiLogSink.LogMessageEventArgs>();
        EventHandler<UiLogSink.LogMessageEventArgs> handler = (_, e) => received.Add(e);

        UiLogSink.MessageLogged += handler;
        UiLogSink.MessageLogged -= handler;
        sink.Emit(CreateEvent("no listener"));

        Assert.Empty(received);
    }

    [Fact]
    public void EmitNotifiesAllSubscribers()
    {
        var (sink, _) = CreateLogger();
        var first = new List<string>();
        var second = new List<string>();
        using var firstSubscription = Subscribe((_, e) => first.Add(e.Message));
        using var secondSubscription = Subscribe((_, e) => second.Add(e.Message));

        sink.Emit(CreateEvent("broadcast"));

        Assert.Single(first);
        Assert.Single(second);
    }

    [Fact]
    public void EmitThrowingSubscriberDoesNotPropagate()
    {
        var (sink, _) = CreateLogger();
        var invoked = false;
        using var subscription = Subscribe((_, _) =>
        {
            invoked = true;
            throw new InvalidOperationException("subscriber failure");
        });

        var exception = Record.Exception(() => sink.Emit(CreateEvent("will fail")));

        Assert.Null(exception);
        Assert.True(invoked);
    }

    [Fact]
    public void EmitThrowingSubscriberDoesNotPropagateThroughLogger()
    {
        var (_, logger) = CreateLogger();
        using var subscription = Subscribe((_, _) => throw new InvalidOperationException("subscriber failure"));

        var exception = Record.Exception(() => logger.Warning("through the pipeline"));

        Assert.Null(exception);
    }

    [Fact]
    public void EmitRecursionGuardPreventsInfiniteRecursion()
    {
        var (sink, _) = CreateLogger();
        var logEvent = CreateEvent("recursive event");
        var calls = 0;
        using var subscription = Subscribe((_, _) =>
        {
            calls++;
            if (calls <= 5) sink.Emit(logEvent);
        });

        sink.Emit(logEvent);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void EmitRecursionGuardAppliesThroughLoggerPipeline()
    {
        var (_, logger) = CreateLogger();
        var calls = 0;
        using var subscription = Subscribe((_, _) =>
        {
            calls++;
            if (calls <= 5) logger.Information("recursive {Depth}", calls);
        });

        logger.Information("start");

        Assert.Equal(1, calls);
    }

    [Fact]
    public void EmitRecursionGuardResetsAfterCompletion()
    {
        var (sink, _) = CreateLogger();
        var calls = 0;
        using var subscription = Subscribe((_, _) => calls++);

        sink.Emit(CreateEvent("first"));
        sink.Emit(CreateEvent("second"));

        Assert.Equal(2, calls);
    }

    [Fact]
    public void EmitRendersWithInvariantCulture()
    {
        var (sink, _) = CreateLogger(CultureInfo.InvariantCulture);
        var received = new List<UiLogSink.LogMessageEventArgs>();
        using var subscription = Subscribe((_, e) => received.Add(e));

        sink.Emit(CreateEvent("Value {Value:0.0}", ("Value", 1.5)));

        var args = Assert.Single(received);
        Assert.Contains("Value 1.5", args.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitRendersWithCultureSpecificFormatProvider()
    {
        var (sink, _) = CreateLogger(CultureInfo.GetCultureInfo("fr-FR"));
        var received = new List<UiLogSink.LogMessageEventArgs>();
        using var subscription = Subscribe((_, e) => received.Add(e));

        sink.Emit(CreateEvent("Value {Value:0.0}", ("Value", 1.5)));

        var args = Assert.Single(received);
        Assert.Contains("Value 1,5", args.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitWithoutFormatProviderRendersWithInvariantCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var (sink, _) = CreateLogger();
            var received = new List<UiLogSink.LogMessageEventArgs>();
            using var subscription = Subscribe((_, e) => received.Add(e));

            sink.Emit(CreateEvent("Value {Value:0.0}", ("Value", 1.5)));

            var args = Assert.Single(received);
            Assert.Contains("Value 1.5", args.Message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void EmitRendersStructuredProperties()
    {
        var (sink, _) = CreateLogger();
        var received = new List<UiLogSink.LogMessageEventArgs>();
        using var subscription = Subscribe((_, e) => received.Add(e));

        sink.Emit(CreateEvent("Count {Count} for {Name}", ("Count", 42), ("Name", "app")));

        var args = Assert.Single(received);
        Assert.Contains("Count 42 for app", args.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitRendersTemplateWithEscapedBraces()
    {
        var (sink, _) = CreateLogger();
        var received = new List<UiLogSink.LogMessageEventArgs>();
        using var subscription = Subscribe((_, e) => received.Add(e));

        sink.Emit(CreateEvent("literal {{braces}}"));

        var args = Assert.Single(received);
        Assert.Contains("literal {braces}", args.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmitMultipleEventsRaiseInOrder()
    {
        var (sink, _) = CreateLogger();
        var messages = new List<string>();
        using var subscription = Subscribe((_, e) => messages.Add(e.Message));

        sink.Emit(CreateEvent("first"));
        sink.Emit(CreateEvent("second"));

        Assert.Equal(2, messages.Count);
        Assert.Contains("first", messages[0], StringComparison.Ordinal);
        Assert.Contains("second", messages[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose)]
    [InlineData(LogEventLevel.Debug)]
    [InlineData(LogEventLevel.Information)]
    [InlineData(LogEventLevel.Warning)]
    [InlineData(LogEventLevel.Error)]
    [InlineData(LogEventLevel.Fatal)]
    public void EmitRendersEveryLogEventLevel(LogEventLevel level)
    {
        var (sink, _) = CreateLogger();
        var received = new List<UiLogSink.LogMessageEventArgs>();
        using var subscription = Subscribe((_, e) => received.Add(e));

        sink.Emit(CreateEvent(level, "level {Level} for {Name}", ("Level", level.ToString()), ("Name", "app")));

        var args = Assert.Single(received);
        Assert.Contains($"level {level} for app", args.Message, StringComparison.Ordinal);
    }

    private static LogEvent CreateEvent(string template, params (string Name, object? Value)[] properties)
    {
        return CreateEvent(DateTimeOffset.Now, LogEventLevel.Information, template, properties);
    }

    private static LogEvent CreateEvent(LogEventLevel level, string template,
        params (string Name, object? Value)[] properties)
    {
        return CreateEvent(DateTimeOffset.Now, level, template, properties);
    }

    private static LogEvent CreateEvent(DateTimeOffset timestamp, LogEventLevel level, string template,
        params (string Name, object? Value)[] properties)
    {
        var messageTemplate = new MessageTemplateParser().Parse(template);
        var eventProperties = properties.Select(static p => new LogEventProperty(p.Name, new ScalarValue(p.Value)));
        return new LogEvent(timestamp, level, null, messageTemplate, eventProperties);
    }

    private static (UiLogSink Sink, ILogger Logger) CreateLogger(IFormatProvider? formatProvider = null)
    {
        var sink = new UiLogSink(formatProvider);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (sink, logger);
    }

    private static Subscription Subscribe(EventHandler<UiLogSink.LogMessageEventArgs> handler)
    {
        UiLogSink.MessageLogged += handler;
        return new Subscription(handler);
    }

    private sealed class Subscription(EventHandler<UiLogSink.LogMessageEventArgs> handler) : IDisposable
    {
        public void Dispose()
        {
            UiLogSink.MessageLogged -= handler;
        }
    }
}