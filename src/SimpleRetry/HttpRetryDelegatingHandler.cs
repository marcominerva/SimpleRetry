using System.Net;
using System.Net.Http.Headers;

namespace SimpleRetry;

internal sealed class HttpRetryDelegatingHandler(IRetryExecutor executor, bool bufferRequestContent = false,
    bool cloneRequest = false) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Content is StreamContent content)
        {
            if (!bufferRequestContent)
            {
                throw new InvalidOperationException("Cannot retry requests with non-buffered stream content. Enable buffering or use a different content type.");
            }

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
                return await base.SendAsync(request, attemptCancellationToken).ConfigureAwait(false);
            }

            // HttpRequestMessage instances are single-use in the HTTP pipeline. Each retry must send a new request
            // instance that preserves the original method, URI, headers, options, version, and a fresh copy of the body.
            var attemptRequest = CloneRequest(request);

            try
            { 
                return await base.SendAsync(attemptRequest, attemptCancellationToken).ConfigureAwait(false);
            }
            finally
            {
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
        { Result: HttpResponseMessage response } => response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError,
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