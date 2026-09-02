using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace SimpleRetry;

internal sealed class StandardResilienceHandler(RetryPolicyOptions options, IServiceProvider serviceProvider, ILoggerFactory loggerFactory) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The original HttpRequestMessage may contain HttpContent that is read and consumed during the first send.
        // Because the same request can be retried, capture the body bytes and headers before the first attempt so
        // each retry can create a new HttpContent instance instead of reusing content that may already be consumed.
        var requestContent = request.Content is null ? null : await RequestContentSnapshot.CreateAsync(request.Content, cancellationToken).ConfigureAwait(false);

        var attempt = 0;

        while (true)
        {
            // HttpRequestMessage instances are single-use in the HTTP pipeline. Each retry must send a new request
            // instance that preserves the original method, URI, headers, options, version, and a fresh copy of the body.
            using var attemptRequest = CloneRequest(request, requestContent);

            try
            {
                var response = await SendAttemptAsync(attemptRequest, cancellationToken).ConfigureAwait(false);

                var responseOutcome = RetryOutcome.FromResult(response);

                if (!ShouldRetry(responseOutcome, attempt))
                {
                    return response;
                }

                attempt++;

                var retryDelay = GetRetryDelay(attempt, response.Headers.RetryAfter);
                await OnRetryAsync(attempt, retryDelay, responseOutcome).ConfigureAwait(false);

                response.Dispose();
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (ShouldRetry(RetryOutcome.FromException(exception), attempt))
            {
                attempt++;

                var retryDelay = GetRetryDelay(attempt);
                await OnRetryAsync(attempt, retryDelay, RetryOutcome.FromException(exception)).ConfigureAwait(false);

                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
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
        { Result: HttpResponseMessage response } => IsTransientStatusCode(response.StatusCode),
        _ => false
    };

    private async Task<HttpResponseMessage> SendAttemptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (options.AttemptTimeout is not TimeSpan attemptTimeout)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(attemptTimeout);

        try
        {
            return await base.SendAsync(request, timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
        {
            throw new RetryTimeoutException(attemptTimeout, exception);
        }
    }

    // A timeout is produced by the policy itself, so it is always retried regardless of the configured
    // predicate, which would otherwise have to know about an exception type it never throws.
    private bool ShouldRetry(RetryOutcome outcome, int attempt)
        => attempt < options.MaxRetryCount && (outcome.Exception is RetryTimeoutException || options.ShouldHandle(outcome));

    private Task OnRetryAsync(int attempt, TimeSpan retryDelay, RetryOutcome outcome)
        => options.OnRetry?.Invoke(new(attempt, options.MaxRetryCount, retryDelay, outcome, serviceProvider, loggerFactory)) ?? Task.CompletedTask;

    private TimeSpan GetRetryDelay(int attempt, RetryConditionHeaderValue? retryAfter = null)
    {
        if (retryAfter?.Delta is TimeSpan delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is DateTimeOffset date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return options.BackoffType switch
        {
            BackoffType.Constant => options.RetryDelay,
            BackoffType.Linear => options.RetryDelay * attempt,
            BackoffType.Exponential => options.RetryDelay * Math.Pow(2, attempt - 1),
            _ => throw new InvalidOperationException($"The retry backoff type '{options.BackoffType}' is not supported.")
        };
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

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
