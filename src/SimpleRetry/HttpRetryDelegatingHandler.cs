using System.Net;
using System.Net.Http.Headers;

namespace SimpleRetry;

internal sealed class HttpRetryDelegatingHandler(IRetryExecutor executor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The original HttpRequestMessage may contain HttpContent that is read and consumed during the first send.
        // Because the same request can be retried, capture the body bytes and headers before the first attempt so
        // each retry can create a new HttpContent instance instead of reusing content that may already be consumed.
        var requestContent = request.Content is null ? null : await RequestContentSnapshot.CreateAsync(request.Content, cancellationToken).ConfigureAwait(false);

        return await executor.ExecuteAsync(async attemptCancellationToken =>
        {
            // HttpRequestMessage instances are single-use in the HTTP pipeline. Each retry must send a new request
            // instance that preserves the original method, URI, headers, options, version, and a fresh copy of the body.
            using var attemptRequest = CloneRequest(request, requestContent);

            return await base.SendAsync(attemptRequest, attemptCancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether the outcome of an HTTP attempt is transient and can be retried.
    /// </summary>
    /// <remarks>
    /// This is the default <see cref="RetryPolicyOptions.ShouldHandle"/> used by the standard HTTP resilience
    /// handler. Because it is expressed as an outcome predicate, callers can replace it entirely to change both
    /// the handled exceptions and the handled status codes.
    /// </remarks>
    internal static bool ShouldHandle(RetryOutcome outcome) => outcome switch
    {
        { Exception: HttpRequestException or RetryTimeoutException } => true,
        { Result: HttpResponseMessage response } => response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500,
        _ => false
    };

    /// <summary>
    /// Returns the delay requested by the server through the <c>Retry-After</c> response header, if any.
    /// </summary>
    /// <remarks>
    /// This is the default <see cref="RetryPolicyOptions.RetryDelayGenerator"/> used by the standard HTTP
    /// resilience handler. Returning <see langword="null"/> lets the configured backoff decide the delay.
    /// </remarks>
    internal static TimeSpan? GetRetryAfterDelay(RetryOutcome outcome)
    {
        if (outcome.Result is not HttpResponseMessage response)
        {
            return null;
        }

        return response.Headers.RetryAfter switch
        {
            { Delta: TimeSpan delta } => Max(delta, TimeSpan.Zero),
            { Date: DateTimeOffset date } => Max(date - DateTimeOffset.UtcNow, TimeSpan.Zero),
            _ => null
        };

        static TimeSpan Max(TimeSpan value, TimeSpan minimum) => value < minimum ? minimum : value;
    }

    /// <summary>
    /// Releases a response that is being retried and will therefore never be returned to the caller.
    /// </summary>
    /// <remarks>
    /// This is the default <see cref="RetryPolicyOptions.OnResultDiscarded"/> used by the standard HTTP resilience
    /// handler; without it, every retried response would keep its connection and stream alive until collected.
    /// </remarks>
    internal static void DisposeDiscardedResponse(RetryOutcome outcome)
        => (outcome.Result as HttpResponseMessage)?.Dispose();

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request, RequestContentSnapshot? requestContent)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Content = requestContent?.CreateContent(),
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        return clone;
    }

    private sealed class RequestContentSnapshot(byte[] content, HttpContentHeaders headers)
    {
        public static async Task<RequestContentSnapshot> CreateAsync(HttpContent content, CancellationToken cancellationToken)
            => new(await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false), content.Headers);

        public HttpContent CreateContent()
        {
            var clone = new ByteArrayContent(content);

            foreach (var header in headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}
