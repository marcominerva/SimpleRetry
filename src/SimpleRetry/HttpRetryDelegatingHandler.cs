using System.Net;

namespace SimpleRetry;

/// <summary>
/// Retries transient HTTP failures by replaying the outgoing request through the configured <see cref="IRetryExecutor"/>.
/// </summary>
/// <param name="executor">The executor that applies the retry policy to each send attempt.</param>
/// <param name="bufferRequestContent">
/// <see langword="true"/> to buffer non-replayable request bodies in memory so that they can be sent again on every
/// attempt; <see langword="false"/> to reject such requests up front.
/// </param>
/// <param name="cloneRequest">
/// <see langword="true"/> to send a fresh copy of the request message on every attempt, so that mutations applied
/// by the inner handlers (added headers, rewritten URIs) never leak into the following attempts;
/// <see langword="false"/> to send the very same <see cref="HttpRequestMessage"/> instance every time.
/// </param>
/// <remarks>
/// <para>
/// By default the handler resends the original request message through a plain <c>base.SendAsync(request, …)</c>,
/// exactly like the standard <c>Microsoft.Extensions.Http.Resilience</c> handler: no message is allocated per
/// attempt. The cost is that every mutation applied by the inner handlers
/// accumulates across attempts: headers can end up duplicated or overwritten, and a retry follows the URI that a
/// redirect handler rewrote on the message instead of the original one. Setting <paramref name="cloneRequest"/>
/// trades those extra allocations for a clean request state on every attempt.
/// </para>
/// <para>
/// Either way the caller's <see cref="HttpContent"/> instance is reused instead of being copied, so the body must be
/// replayable: a <see cref="StreamContent"/> consumes its source stream during the first send, so it can only be
/// retried when <paramref name="bufferRequestContent"/> materializes it in memory beforehand.
/// </para>
/// </remarks>
internal sealed class HttpRetryDelegatingHandler(IRetryExecutor executor, bool bufferRequestContent = false, bool cloneRequest = false) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Content is StreamContent content)
        {
            if (!bufferRequestContent)
            {
                throw new InvalidOperationException($"{nameof(StreamContent)} content cannot be cloned, because its source stream is consumed by the first attempt and may not be seekable. Enable request content buffering to retry requests with this kind of content.");
            }

            // Serializing the content into its own internal buffer makes every later send replay the buffered bytes
            // instead of the source stream, so the same HttpContent instance can be reused by all the attempts.
#if NET9_0_OR_GREATER
            await content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
#else
            await content.LoadIntoBufferAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
#endif
        }

        return await executor.ExecuteAsync(async attemptCancellationToken =>
        {
            if (!cloneRequest)
            {
                // Resending the original message allocates nothing, at the cost of carrying over whatever the inner
                // handlers changed on it during the previous attempt.
                return await base.SendAsync(request, attemptCancellationToken).ConfigureAwait(false);
            }

            // Each attempt sends a new request instance that preserves the original method, URI, headers, options,
            // version and body, so that nothing the inner handlers change survives into the following attempts.
            var attemptRequest = CloneRequest(request);

            try
            {
                return await base.SendAsync(attemptRequest, attemptCancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Disposing an HttpRequestMessage disposes its content, which here belongs to the caller and must
                // survive both the remaining attempts and the caller's own usage, so detach it first.
                attemptRequest.Content = null;
                attemptRequest.Dispose();
            }
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

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Content = request.Content,
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
}
