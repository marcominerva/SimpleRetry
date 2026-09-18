using System.Net;
using System.Net.Http.Headers;
using NSubstitute;

namespace SimpleRetry.UnitTests;

public class HttpRetryDelegatingHandlerTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public void ShouldHandle_WhenOutcomeContainsResponse_ReturnsExpected(HttpStatusCode statusCode, bool expected)
    {
        using var response = new HttpResponseMessage(statusCode);
        Assert.Equal(expected, HttpRetryDelegatingHandler.ShouldHandle(RetryOutcome.FromResult(response)));
    }

    [Fact]
    public void ShouldHandle_WhenOutcomeContainsException_HandlesOnlyTransientExceptions()
    {
        Assert.True(HttpRetryDelegatingHandler.ShouldHandle(RetryOutcome.FromException(new HttpRequestException())));
        Assert.True(HttpRetryDelegatingHandler.ShouldHandle(RetryOutcome.FromException(new RetryTimeoutException(TimeSpan.Zero))));
        Assert.False(HttpRetryDelegatingHandler.ShouldHandle(RetryOutcome.FromException(new InvalidOperationException())));
        Assert.False(HttpRetryDelegatingHandler.ShouldHandle(RetryOutcome.FromResult("not an HTTP response")));
    }

    [Fact]
    public void GetRetryAfterDelay_WhenDeltaIsPresent_ReturnsNonNegativeDelta()
    {
        using var futureResponse = new HttpResponseMessage();
        futureResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
        using var pastResponse = new HttpResponseMessage();
        pastResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(-1));
        Assert.Equal(TimeSpan.FromSeconds(12), HttpRetryDelegatingHandler.GetRetryAfterDelay(RetryOutcome.FromResult(futureResponse)));
        Assert.Equal(TimeSpan.Zero, HttpRetryDelegatingHandler.GetRetryAfterDelay(RetryOutcome.FromResult(pastResponse)));
    }

    [Fact]
    public void GetRetryAfterDelay_WhenHeaderOrResponseIsMissing_ReturnsNull()
    {
        using var response = new HttpResponseMessage();
        Assert.Null(HttpRetryDelegatingHandler.GetRetryAfterDelay(RetryOutcome.FromResult(response)));
        Assert.Null(HttpRetryDelegatingHandler.GetRetryAfterDelay(RetryOutcome.FromException(new HttpRequestException())));
    }

    [Fact]
    public void GetRetryAfterDelay_WhenDateIsInPast_ReturnsZero()
    {
        using var response = new HttpResponseMessage();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddDays(-1));
        Assert.Equal(TimeSpan.Zero, HttpRetryDelegatingHandler.GetRetryAfterDelay(RetryOutcome.FromResult(response)));
    }

    [Fact]
    public void DisposeDiscardedResponse_WhenOutcomeContainsResponse_DisposesIt()
    {
        var content = new TrackingContent();
        var response = new HttpResponseMessage { Content = content };
        HttpRetryDelegatingHandler.DisposeDiscardedResponse(RetryOutcome.FromResult(response));
        HttpRetryDelegatingHandler.DisposeDiscardedResponse(RetryOutcome.FromException(new HttpRequestException()));
        Assert.True(content.IsDisposed);
    }

    [Fact]
    public async Task SendAsync_WhenCloneIsDisabled_ForwardsOriginalRequest()
    {
        var primaryHandler = new RecordingHandler();
        using var retryHandler = new HttpRetryDelegatingHandler(CreatePassThroughExecutor()) { InnerHandler = primaryHandler };
        using var client = new HttpClient(retryHandler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/resource");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Same(request, Assert.Single(primaryHandler.Requests));
        Assert.True(primaryHandler.CancellationTokens.Single().CanBeCanceled);
    }

    [Fact]
    public async Task SendAsync_WhenCloningRequest_PreservesRequestDataAndContent()
    {
        var primaryHandler = new RecordingHandler();
        using var retryHandler = new HttpRetryDelegatingHandler(CreatePassThroughExecutor(), cloneRequest: true) { InnerHandler = primaryHandler };
        using var client = new HttpClient(retryHandler);
        using var content = new StringContent("payload");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/custom");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/resource")
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        request.Headers.Add("X-Test", "value");
        request.Options.Set(new HttpRequestOptionsKey<string>("option"), "option-value");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var forwarded = Assert.Single(primaryHandler.Requests);
        Assert.NotSame(request, forwarded);
        Assert.Equal(request.Method, forwarded.Method);
        Assert.Equal(request.RequestUri, forwarded.RequestUri);
        Assert.Equal(request.Version, forwarded.Version);
        Assert.Equal(request.VersionPolicy, forwarded.VersionPolicy);
        Assert.Equal("value", Assert.Single(forwarded.Headers.GetValues("X-Test")));
        Assert.True(forwarded.Options.TryGetValue(new HttpRequestOptionsKey<string>("option"), out var option));
        Assert.Equal("option-value", option);
        Assert.Equal("payload", primaryHandler.Bodies.Single());
        Assert.Equal("application/custom", primaryHandler.ContentTypes.Single());
    }

    [Fact]
    public async Task SendAsync_WhenStreamContentIsNotBuffered_ThrowsBeforeExecuting()
    {
        var executor = Substitute.For<IRetryExecutor>();
        using var retryHandler = new HttpRetryDelegatingHandler(executor) { InnerHandler = new RecordingHandler() };
        using var client = new HttpClient(retryHandler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test")
        {
            Content = new StreamContent(new MemoryStream([1, 2, 3]))
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("Enable buffering", exception.Message);
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync<HttpResponseMessage>(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SendAsync_WhenStreamBufferingIsEnabled_BuffersAndSendsContent()
    {
        var primaryHandler = new RecordingHandler();
        using var retryHandler = new HttpRetryDelegatingHandler(CreatePassThroughExecutor(), bufferRequestContent: true) { InnerHandler = primaryHandler };
        using var client = new HttpClient(retryHandler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test")
        {
            Content = new StreamContent(new MemoryStream([1, 2, 3]))
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], primaryHandler.BodyBytes.Single());
    }

    private static IRetryExecutor CreatePassThroughExecutor()
    {
        var executor = Substitute.For<IRetryExecutor>();
        executor.ExecuteAsync(Arg.Any<Func<CancellationToken, Task<HttpResponseMessage>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<HttpResponseMessage>>>()(call.ArgAt<CancellationToken>(1)));
        return executor;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];
        public List<string> Bodies { get; } = [];
        public List<byte[]> BodyBytes { get; } = [];
        public List<string?> ContentTypes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            CancellationTokens.Add(cancellationToken);
            if (request.Content is not null)
            {
                BodyBytes.Add(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
                ContentTypes.Add(request.Content.Headers.ContentType?.MediaType);
            }

            return new(HttpStatusCode.OK);
        }
    }

    private sealed class TrackingContent : ByteArrayContent
    {
        public TrackingContent() : base([]) { }
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
