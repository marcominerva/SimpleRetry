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

                if (!ShouldRetryResponse(response, attempt))
                {
                    return response;
                }

                attempt++;

                var retryDelay = GetRetryDelay(attempt, response.Headers.RetryAfter);
                var retryException = new HttpRetryResponseException(response.StatusCode);

                await OnRetryAsync(attempt, retryDelay, retryException).ConfigureAwait(false);

                response.Dispose();
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (ShouldRetryException(exception, attempt))
            {
                attempt++;

                var retryDelay = GetRetryDelay(attempt);
                await OnRetryAsync(attempt, retryDelay, exception).ConfigureAwait(false);

                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static bool ShouldHandle(Exception exception)
        => exception is HttpRequestException or RetryTimeoutException;

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

    private bool ShouldRetryResponse(HttpResponseMessage response, int attempt)
        => attempt < options.MaxRetryCount && IsTransientStatusCode(response.StatusCode);

    private bool ShouldRetryException(Exception exception, int attempt)
        => attempt < options.MaxRetryCount && (options.ShouldHandle?.Invoke(exception) ?? true);

    private Task OnRetryAsync(int attempt, TimeSpan retryDelay, Exception exception)
        => options.OnRetry?.Invoke(new(attempt, options.MaxRetryCount, retryDelay, exception, serviceProvider, loggerFactory)) ?? Task.CompletedTask;

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

    private sealed class HttpRetryResponseException(HttpStatusCode statusCode)
        : HttpRequestException($"The HTTP response status code '{(int)statusCode}' is transient and can be retried.", null, statusCode);

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
