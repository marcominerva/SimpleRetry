using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace SimpleRetry.UnitTests;

public class HttpRetryDelegatingHandlerTests
{
    [Fact]
    public async Task WhenHttpRetryDelegatingHandlerReceivesTransientStatusThenRetriesRequest()
    {
        var handler = new SequenceHttpMessageHandler(static attempt => attempt == 1 ? new(HttpStatusCode.InternalServerError) : new(HttpStatusCode.OK));

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
