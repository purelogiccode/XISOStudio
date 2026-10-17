using System.Net;
using System.Text.Json;
using XISOStudio.Services;
using Moq;
using Moq.Protected;
using Serilog.Events;
using Xunit;

namespace XISOStudio.Tests.Services;

/// <summary>Tests anonymous usage stats submission behavior of <c>StatsService</c>.</summary>
public class StatsServiceTests
{
    private const string StatsUrl = "https://api.example.com/stats";

    private readonly TestLogger _logger = new();

    private static HttpClient CreateHttpClient(HttpStatusCode statusCode, string content = "")
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });
        return new HttpClient(handlerMock.Object);
    }

    private static HttpClient CreateHttpClientThatThrows(Exception exception)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(exception);
        return new HttpClient(handlerMock.Object);
    }

    [Fact]
    public async Task SendStatsAsyncSuccessDoesNotThrow()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.OK);
        var service = new StatsService(httpClient, "https://api.example.com/stats", "test-key", "TestApp",
            _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);
        Assert.Null(exception);
    }

    [Fact]
    public async Task SendStatsAsyncPassesCancellableTimeoutTokenToHandler()
    {
        var capturedToken = CancellationToken.None;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((_, token) => capturedToken = token)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{}")
            });
        var service = new StatsService(new HttpClient(handlerMock.Object), StatsUrl, "test-key", "TestApp",
            _logger.Logger);

        await service.SendStatsAsync();

        // A hung stats endpoint must not hold up startup: the request carries a timeout token.
        Assert.True(capturedToken.CanBeCanceled);
    }

    [Fact]
    public void ConstructorDoesNotMutateInjectedHttpClient()
    {
        using var httpClient = new HttpClient();

        var service = new StatsService(httpClient, "https://api.example.com/stats", "test-key", "TestApp",
            _logger.Logger);

        Assert.NotNull(service);
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task SendStatsAsyncServerErrorDoesNotThrow()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.InternalServerError);
        var service = new StatsService(httpClient, "https://api.example.com/stats", "test-key", "TestApp",
            _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);
        Assert.Null(exception);
    }

    [Fact]
    public async Task SendStatsAsyncNetworkErrorDoesNotThrow()
    {
        var httpClient = CreateHttpClientThatThrows(new HttpRequestException("Network unreachable"));
        var service = new StatsService(httpClient, "https://api.example.com/stats", "test-key", "TestApp",
            _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);
        Assert.Null(exception);
    }

    [Fact]
    public async Task SendStatsAsyncTimeoutDoesNotThrow()
    {
        var httpClient = CreateHttpClientThatThrows(new TaskCanceledException("Timeout"));
        var service = new StatsService(httpClient, "https://api.example.com/stats", "test-key", "TestApp",
            _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);
        Assert.Null(exception);
    }

    [Fact]
    public async Task SendStatsAsyncSendsPostRequest()
    {
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
        var httpClient = new HttpClient(handlerMock.Object);

        var service = new StatsService(httpClient, "https://api.example.com/stats", "my-api-key", "TestApp",
            _logger.Logger);
        await service.SendStatsAsync();

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal(new Uri("https://api.example.com/stats"), capturedRequest.RequestUri);
    }

    [Fact]
    public async Task SendStatsAsyncSetsAuthorizationHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
        var httpClient = new HttpClient(handlerMock.Object);

        var service = new StatsService(httpClient, "https://api.example.com/stats", "my-secret-key", "TestApp",
            _logger.Logger);
        await service.SendStatsAsync();

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Authorization is not null);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization.Scheme);
        Assert.Equal("my-secret-key", capturedRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task SendStatsAsyncSendsJsonContentType()
    {
        HttpRequestMessage? capturedRequest = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
        var httpClient = new HttpClient(handlerMock.Object);

        var service = new StatsService(httpClient, "https://api.example.com/stats", "key", "MyApp", _logger.Logger);
        await service.SendStatsAsync();

        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Content);
        Assert.Equal("application/json", capturedRequest.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task SendStatsAsyncSendsPayloadWithApplicationIdAndVersion()
    {
        string? capturedBody = null;
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
#pragma warning disable MA0147
            .Callback<HttpRequestMessage, CancellationToken>(async void (req, t) =>
            {
                try
                {
                    if (req.Content != null)
                    {
                        capturedBody = await req.Content.ReadAsStringAsync(t);
                    }
                }
#pragma warning disable RCS1075
                catch (Exception)
#pragma warning restore RCS1075
                {
                    // Ignore
                }
            })
#pragma warning restore MA0147
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
        var httpClient = new HttpClient(handlerMock.Object);

        var service = new StatsService(httpClient, "https://api.example.com/stats", "key", "XISOStudio",
            _logger.Logger);
        await service.SendStatsAsync();

        Assert.NotNull(capturedBody);
        var json = JsonDocument.Parse(capturedBody);
        Assert.True(json.RootElement.TryGetProperty("applicationId", out var appId));
        Assert.Equal("XISOStudio", appId.GetString());
        Assert.True(json.RootElement.TryGetProperty("version", out _));
    }

    [Fact]
    public async Task SendStatsAsyncBadRequestDoesNotThrow()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.BadRequest, "Bad request");
        var service = new StatsService(httpClient, "https://api.example.com/stats", "key", "TestApp", _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);
        Assert.Null(exception);
    }

    [Fact]
    public async Task SendStatsAsyncForbiddenDoesNotThrow()
    {
        var httpClient = CreateHttpClient(HttpStatusCode.Forbidden, "Forbidden");
        var service = new StatsService(httpClient, "https://api.example.com/stats", "key", "TestApp", _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);
        Assert.Null(exception);
    }

    [Fact]
    public async Task SendStatsAsyncSuccessBodyWithoutErrorLogsInformation()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"status\":\"ok\"}");
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.True(_logger.HasMessage(LogEventLevel.Information, "Application statistics sent"));
    }

    [Fact]
    public async Task SendStatsAsyncSuccessBodyWithoutErrorReturnsNormally()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"status\":\"ok\"}");
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);

        Assert.Null(exception);
    }

    [Fact]
    public async Task SendStatsAsyncSuccessDoesNotLogFailure()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.False(_logger.HasMessage(LogEventLevel.Information, "Failed to send startup statistics"));
    }

    [Theory]
    [InlineData("{\"error\":\"Unauthorized\"}")]
    [InlineData("{\"ERROR\":\"Unauthorized\"}")]
    [InlineData("{\"error\":\"Application ID not recognized\"}")]
    public async Task SendStatsAsyncQuotedErrorBodyIsLoggedAsNotRecorded(string body)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, body);
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);

        Assert.Null(exception);
        Assert.True(_logger.HasMessage(LogEventLevel.Information, "Application statistics were not recorded"));
    }

    [Fact]
    public async Task SendStatsAsyncQuotedErrorBodyDoesNotLogSuccess()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"error\":\"Unauthorized\"}");
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.False(_logger.HasMessage(LogEventLevel.Information, "Application statistics sent"));
    }

    [Fact]
    public async Task SendStatsAsyncUnquotedErrorWordIsNotTreatedAsFailure()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "an error occurred but no json error field");
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.True(_logger.HasMessage(LogEventLevel.Information, "Application statistics sent"));
    }

    [Fact]
    public async Task SendStatsAsyncRateLimitResponseIsLoggedAtInformation()
    {
        var handler = new RecordingHandler(HttpStatusCode.TooManyRequests, "Too Many Requests");
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);

        Assert.Null(exception);
        Assert.True(_logger.HasMessage(LogEventLevel.Information, "Failed to send startup statistics"));
    }

    [Fact]
    public async Task SendStatsAsyncRateLimitResponseDoesNotLogSuccess()
    {
        var handler = new RecordingHandler(HttpStatusCode.TooManyRequests, "Too Many Requests");
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.False(_logger.HasMessage(LogEventLevel.Information, "Application statistics sent"));
    }

    [Fact]
    public async Task SendStatsAsyncInternalServerErrorIsLoggedAtInformation()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError, "server exploded");
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);

        Assert.Null(exception);
        Assert.True(_logger.HasMessage(LogEventLevel.Information, "Failed to send startup statistics"));
    }

    [Fact]
    public async Task SendStatsAsyncInternalServerErrorDoesNotLogSuccess()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError, "server exploded");
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.False(_logger.HasMessage(LogEventLevel.Information, "Application statistics sent"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public async Task SendStatsAsyncEmptyOrWhitespaceBodyIsLoggedAsSuccess(string body)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, body);
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);

        Assert.Null(exception);
        Assert.True(_logger.HasMessage(LogEventLevel.Information, "Application statistics sent"));
    }

    [Fact]
    public async Task SendStatsAsyncPayloadContainsExactlyApplicationIdAndVersionFields()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.NotNull(handler.LastBody);
        using var document = JsonDocument.Parse(handler.LastBody);
        var propertyNames = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(2, propertyNames.Length);
        Assert.Contains("applicationId", propertyNames, StringComparer.Ordinal);
        Assert.Contains("version", propertyNames, StringComparer.Ordinal);
    }

    [Fact]
    public async Task SendStatsAsyncPayloadSerializesExactJsonFields()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        var expected =
            $"{{\"applicationId\":\"XISOStudio\",\"version\":\"{GetApplicationVersion.GetProgramVersion()}\"}}";
        Assert.Equal(expected, handler.LastBody);
    }

    [Fact]
    public async Task SendStatsAsyncPayloadVersionComesFromGetApplicationVersion()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.NotNull(handler.LastBody);
        using var document = JsonDocument.Parse(handler.LastBody);
        Assert.Equal(GetApplicationVersion.GetProgramVersion(),
            document.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task SendStatsAsyncAuthorizationHeaderIsBearerWithConfiguredKey()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "super-secret", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.Equal("Bearer super-secret", handler.LastAuthorization);
    }

    [Fact]
    public async Task SendStatsAsyncPostsToConfiguredUrl()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        const string url = "https://stats.example.org/v2/collect";
        var service = new StatsService(httpClient, url, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal(new Uri(url), handler.LastRequestUri);
    }

    [Fact]
    public async Task SendStatsAsyncSendsUtf8JsonContentType()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.Equal("application/json", handler.LastMediaType);
        Assert.Equal("utf-8", handler.LastCharset);
    }

    [Fact]
    public async Task SendStatsAsyncTaskCanceledIsLoggedAtInformation()
    {
        var httpClient = CreateHttpClientThatThrows(new TaskCanceledException("The request timed out"));
        var service = new StatsService(httpClient, StatsUrl, "key", "TestApp", _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);

        Assert.Null(exception);
        Assert.True(_logger.HasMessage(LogEventLevel.Information, "Failed to send startup statistics"));
    }

    [Fact]
    public async Task SendStatsAsyncOperationCanceledIsLoggedAtInformation()
    {
        var httpClient = CreateHttpClientThatThrows(new OperationCanceledException("The caller canceled"));
        var service = new StatsService(httpClient, StatsUrl, "key", "TestApp", _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);

        Assert.Null(exception);
        Assert.True(_logger.HasMessage(LogEventLevel.Information, "Failed to send startup statistics"));
    }

    [Fact]
    public async Task SendStatsAsyncNetworkFailureIsLoggedAtInformation()
    {
        var httpClient = CreateHttpClientThatThrows(new HttpRequestException("Network unreachable"));
        var service = new StatsService(httpClient, StatsUrl, "key", "TestApp", _logger.Logger);

        await service.SendStatsAsync();

        Assert.True(_logger.HasMessage(LogEventLevel.Information, "Failed to send startup statistics"));
    }

    [Fact]
    public async Task SendStatsAsyncRealHttpTimeoutIsLoggedAtInformation()
    {
        using var httpClient = new HttpClient(new WaitingHandler());
        httpClient.Timeout = TimeSpan.FromMilliseconds(100);
        var service = new StatsService(httpClient, StatsUrl, "key", "TestApp", _logger.Logger);

        var exception = await Record.ExceptionAsync(service.SendStatsAsync);

        Assert.Null(exception);
        Assert.True(_logger.HasMessage(LogEventLevel.Information, "Failed to send startup statistics"));
    }

    [Fact]
    public async Task SendStatsAsyncSuccessLogsApplicationIdAndVersion()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.True(_logger.HasMessage(LogEventLevel.Information,
            $"Application statistics sent for \"XISOStudio\" v\"{GetApplicationVersion.GetProgramVersion()}\""));
    }

    [Fact]
    public async Task SendStatsAsyncSuccessDoesNotLogAtWarningOrHigher()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.DoesNotContain(_logger.Events, e => e.Level >= LogEventLevel.Warning);
    }

    [Fact]
    public async Task SendStatsAsyncSendsExactlyOneRequestPerCall()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        var service = new StatsService(httpClient, StatsUrl, "key", "XISOStudio", _logger.Logger);

        await service.SendStatsAsync();

        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>HTTP handler double that records request details and returns a fixed response.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        internal RecordingHandler(HttpStatusCode statusCode, string content = "")
        {
            _statusCode = statusCode;
            _content = content;
        }

        public int CallCount { get; private set; }

        public HttpMethod? LastMethod { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public string? LastAuthorization { get; private set; }

        public string? LastMediaType { get; private set; }

        public string? LastCharset { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastMethod = request.Method;
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastMediaType = request.Content?.Headers.ContentType?.MediaType;
            LastCharset = request.Content?.Headers.ContentType?.CharSet;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage
            {
                StatusCode = _statusCode,
                Content = new StringContent(_content)
            };
        }
    }

    /// <summary>HTTP handler double that waits until the caller cancels the request.</summary>
    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}