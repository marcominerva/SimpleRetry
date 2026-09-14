using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace SimpleRetry.UnitTests;

public class HttpRetryDelegatingHandlerTests
{
    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerReceivesSuccessfulStatusThenSendsRequestOnce()
    {
        var handler = new SequenceHttpMessageHandler(static _ => new(HttpStatusCode.OK));

        var services = new ServiceCollection();

        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpSimpleRetry(options =>
            {
                options.MaxRetryCount = 1;
                options.RetryDelay = TimeSpan.Zero;
            });

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        using var response = await httpClientFactory.CreateClient("test").GetAsync("https://example.com", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerReceivesTransientStatusThenRetriesRequest()
    {
        var handler = new SequenceHttpMessageHandler(static attempt => attempt == 1 ? new(HttpStatusCode.GatewayTimeout) : new(HttpStatusCode.OK));

        var services = new ServiceCollection();

        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpSimpleRetry(options =>
            {
                options.MaxRetryCount = 1;
                options.RetryDelay = TimeSpan.Zero;
            });

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        using var response = await httpClientFactory.CreateClient("test").GetAsync("https://example.com", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerReceivesRequestTimeoutThenRetriesRequest()
    {
        var handler = new SequenceHttpMessageHandler(static attempt => attempt == 1 ? new(HttpStatusCode.RequestTimeout) : new(HttpStatusCode.OK));

        var services = new ServiceCollection();

        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpSimpleRetry(options =>
            {
                options.MaxRetryCount = 1;
                options.RetryDelay = TimeSpan.Zero;
            });

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        using var response = await httpClientFactory.CreateClient("test").GetAsync("https://example.com", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerReceivesTooManyRequestsWithRetryAfterThenUsesRetryAfterDelay()
    {
        var retryDelay = TimeSpan.FromDays(1);
        var retryAfterDelay = TimeSpan.FromSeconds(5);
        var observedDelays = new List<TimeSpan>();

        var handler = new SequenceHttpMessageHandler(attempt =>
        {
            var response = attempt == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : new HttpResponseMessage(HttpStatusCode.OK);

            if (attempt == 1)
            {
                response.Headers.RetryAfter = new(retryAfterDelay);
            }

            return response;
        });
        var services = new ServiceCollection();

        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpSimpleRetry(options =>
            {
                options.MaxRetryCount = 1;
                options.RetryDelay = retryDelay;
                options.OnRetry = arguments =>
                {
                    observedDelays.Add(arguments.RetryDelay);
                    return Task.CompletedTask;
                };
            });

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        using var response = await httpClientFactory.CreateClient("test").GetAsync("https://example.com", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.SendCount);

        var observedDelay = Assert.Single(observedDelays);
        Assert.Equal(retryAfterDelay, observedDelay);
    }

    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerReceivesHttpRequestExceptionThenRetriesRequest()
    {
        var handler = new SequenceHttpMessageHandler(static attempt => attempt == 1 ? throw new HttpRequestException() : new(HttpStatusCode.OK));
        var services = new ServiceCollection();

        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpSimpleRetry(options =>
            {
                options.MaxRetryCount = 1;
                options.RetryDelay = TimeSpan.Zero;
            });

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        using var response = await httpClientFactory.CreateClient("test").GetAsync("https://example.com", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerRetriesRequestThenDisposesDiscardedResponse()
    {
        var discardedContent = new TrackingHttpContent();

        var handler = new SequenceHttpMessageHandler(attempt => attempt == 1 ? new(HttpStatusCode.InternalServerError) { Content = discardedContent }
            : new(HttpStatusCode.OK));

        var services = new ServiceCollection();

        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpSimpleRetry(options =>
            {
                options.MaxRetryCount = 1;
                options.RetryDelay = TimeSpan.Zero;
            });

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        using var response = await httpClientFactory.CreateClient("test").GetAsync("https://example.com", TestContext.Current.CancellationToken);

        Assert.True(discardedContent.IsDisposed);
    }

    [Fact]
    public async Task WhenMultipleHttpClientsAreRegisteredThenEachUsesItsOwnRetryPolicy()
    {
        var retryingHandler = new SequenceHttpMessageHandler(static attempt => attempt == 1 ? new(HttpStatusCode.InternalServerError) : new(HttpStatusCode.OK));
        var nonRetryingHandler = new SequenceHttpMessageHandler(static _ => new(HttpStatusCode.InternalServerError));

        var services = new ServiceCollection();

        services.AddHttpClient("retrying")
            .ConfigurePrimaryHttpMessageHandler(() => retryingHandler)
            .AddHttpSimpleRetry(options =>
            {
                options.MaxRetryCount = 1;
                options.RetryDelay = TimeSpan.Zero;
            });

        services.AddHttpClient("non-retrying")
            .ConfigurePrimaryHttpMessageHandler(() => nonRetryingHandler)
            .AddHttpSimpleRetry(options => options.MaxRetryCount = 0);

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        using var retryingResponse = await httpClientFactory.CreateClient("retrying").GetAsync("https://example.com", TestContext.Current.CancellationToken);
        using var nonRetryingResponse = await httpClientFactory.CreateClient("non-retrying").GetAsync("https://example.com", TestContext.Current.CancellationToken);

        Assert.Equal(2, retryingHandler.SendCount);
        Assert.Equal(1, nonRetryingHandler.SendCount);
    }

    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerAttemptTimeoutExpiresThenRetriesRequest()
    {
        var attemptTimeout = TimeSpan.FromMilliseconds(100);
        var handler = new DelayingHttpMessageHandler(attempt => attempt == 1 ? Timeout.InfiniteTimeSpan : TimeSpan.Zero);

        var services = new ServiceCollection();

        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpSimpleRetry(options =>
            {
                options.MaxRetryCount = 1;
                options.RetryDelay = TimeSpan.Zero;
                options.AttemptTimeout = attemptTimeout;
            });

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        var httpClient = httpClientFactory.CreateClient("test");
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        using var response = await httpClient.GetAsync("https://example.com", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerAttemptTimeoutIsExhaustedThenThrowsRetryTimeoutException()
    {
        var attemptTimeout = TimeSpan.FromMilliseconds(100);
        var handler = new DelayingHttpMessageHandler(static _ => Timeout.InfiniteTimeSpan);

        var services = new ServiceCollection();

        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpSimpleRetry(options =>
            {
                options.MaxRetryCount = 1;
                options.RetryDelay = TimeSpan.Zero;
                options.AttemptTimeout = attemptTimeout;
            });

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        var httpClient = httpClientFactory.CreateClient("test");
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        var exception = await Assert.ThrowsAsync<RetryTimeoutException>(() => httpClient.GetAsync("https://example.com", TestContext.Current.CancellationToken));

        Assert.Equal(attemptTimeout, exception.Timeout);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerReceivesRetryAfterDateThenUsesRetryAfterDelay()
    {
        var retryAfterDate = DateTimeOffset.UtcNow.AddSeconds(30);
        var observedDelays = new List<TimeSpan>();

        var handler = new SequenceHttpMessageHandler(attempt =>
        {
            var response = new HttpResponseMessage(attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);

            if (attempt == 1)
            {
                response.Headers.RetryAfter = new(retryAfterDate);
            }

            return response;
        });

        var services = new ServiceCollection();

        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpSimpleRetry(options =>
            {
                options.MaxRetryCount = 1;
                options.RetryDelay = TimeSpan.FromDays(1);
                options.OnRetry = arguments =>
                {
                    observedDelays.Add(arguments.RetryDelay);
                    return Task.CompletedTask;
                };
            });

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        using var response = await httpClientFactory.CreateClient("test").GetAsync("https://example.com", TestContext.Current.CancellationToken);

        var observedDelay = Assert.Single(observedDelays);
        Assert.InRange(observedDelay, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(BackoffType.Constant, 2, 2, 2)]
    [InlineData(BackoffType.Linear, 2, 4, 6)]
    [InlineData(BackoffType.Exponential, 2, 4, 8)]
    public async Task WhenHttpRetryDelegatingHandlerReceivesNoRetryAfterThenUsesConfiguredBackoff(BackoffType backoffType, int firstDelayMilliseconds, int secondDelayMilliseconds, int thirdDelayMilliseconds)
    {
        var observedDelays = new List<TimeSpan>();
        var handler = new SequenceHttpMessageHandler(static attempt => new(attempt <= 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        var services = new ServiceCollection();

        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpSimpleRetry(options =>
            {
                options.MaxRetryCount = 3;
                options.RetryDelay = TimeSpan.FromMilliseconds(2);
                options.BackoffType = backoffType;
                options.OnRetry = arguments =>
                {
                    observedDelays.Add(arguments.RetryDelay);
                    return Task.CompletedTask;
                };
            });

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        using var response = await httpClientFactory.CreateClient("test").GetAsync("https://example.com", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([
            TimeSpan.FromMilliseconds(firstDelayMilliseconds),
            TimeSpan.FromMilliseconds(secondDelayMilliseconds),
            TimeSpan.FromMilliseconds(thirdDelayMilliseconds)
        ], observedDelays);
    }

    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerReceivesStreamContentWithoutBufferingThenThrowsInvalidOperationException()
    {
        var handler = new SequenceHttpMessageHandler(static _ => new(HttpStatusCode.OK));

        using var client = CreateClient(handler, bufferRequestContent: false);
        using var content = new StreamContent(new MemoryStream("payload"u8.ToArray()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(()
            => client.PostAsync("https://example.com", content, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(StreamContent), exception.Message);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerBuffersStreamContentThenEveryAttemptSendsTheSameBody()
    {
        var bodies = new List<string>();

        var handler = new RecordingBodyHttpMessageHandler(bodies, static attempt => attempt == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);

        using var client = CreateClient(handler, bufferRequestContent: true);
        using var content = new StreamContent(new NonSeekableStream("payload"u8.ToArray()));

        using var response = await client.PostAsync("https://example.com", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["payload", "payload"], bodies);
    }

    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerRetriesRequestThenDoesNotDisposeRequestContent()
    {
        var bodies = new List<string>();

        var handler = new RecordingBodyHttpMessageHandler(bodies, static attempt => attempt == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);

        using var client = CreateClient(handler, bufferRequestContent: false);
        using var content = new StringContent("payload");

        using var response = await client.PostAsync("https://example.com", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["payload", "payload"], bodies);

        // The caller still owns the content, so it must be usable after the retried request completed.
        Assert.Equal("payload", await content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerDoesNotCloneRequestThenEveryAttemptUsesTheSameRequestInstance()
    {
        var requests = new List<HttpRequestMessage>();

        var handler = new RecordingHttpMessageHandler(requests, static attempt => attempt == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);

        using var client = CreateClient(handler, cloneRequest: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Same(request, Assert.Single(requests.Distinct()));
    }

    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerClonesRequestThenInnerHandlerMutationsDoNotLeakIntoNextAttempt()
    {
        var requests = new List<HttpRequestMessage>();

        var handler = new RecordingHttpMessageHandler(requests, static attempt => attempt == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
        {
            OnRequest = static request => request.Headers.Add("X-Attempt", "1")
        };

        using var client = CreateClient(handler, cloneRequest: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, requests.Distinct().Count());
        Assert.All(requests, attemptRequest => Assert.Single(attemptRequest.Headers.GetValues("X-Attempt")));
        Assert.False(request.Headers.Contains("X-Attempt"));
    }

    private static HttpClient CreateClient(HttpMessageHandler innerHandler, bool bufferRequestContent = false, bool cloneRequest = false)
    {
        var options = new RetryPolicyOptions
        {
            MaxRetryCount = 1,
            RetryDelay = TimeSpan.Zero,
            ShouldHandle = HttpRetryDelegatingHandler.ShouldHandle,
            OnResultDiscarded = HttpRetryDelegatingHandler.DisposeDiscardedResponse
        };

        var services = new ServiceCollection().BuildServiceProvider();
        var executor = new DefaultRetryExecutor(options, services, NullLoggerFactory.Instance);

        return new HttpClient(new HttpRetryDelegatingHandler(executor, bufferRequestContent, cloneRequest) { InnerHandler = innerHandler });
    }

    private sealed class SequenceHttpMessageHandler(Func<int, HttpResponseMessage> createResponse) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(createResponse(SendCount));
        }
    }

    private sealed class DelayingHttpMessageHandler(Func<int, TimeSpan> getDelay) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            await Task.Delay(getDelay(SendCount), cancellationToken);

            return new(HttpStatusCode.OK);
        }
    }

    private sealed class RecordingHttpMessageHandler(List<HttpRequestMessage> requests, Func<int, HttpStatusCode> getStatusCode) : HttpMessageHandler
    {
        private int sendCount;

        public Action<HttpRequestMessage>? OnRequest { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sendCount++;
            OnRequest?.Invoke(request);
            requests.Add(request);

            return Task.FromResult(new HttpResponseMessage(getStatusCode(sendCount)));
        }
    }

    private sealed class RecordingBodyHttpMessageHandler(List<string> bodies, Func<int, HttpStatusCode> getStatusCode) : HttpMessageHandler
    {
        private int sendCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sendCount++;
            bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));

            return new(getStatusCode(sendCount));
        }
    }

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }

    private sealed class TrackingHttpContent : HttpContent
    {
        public bool IsDisposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
