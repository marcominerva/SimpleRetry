using Microsoft.Extensions.Logging;

namespace SimpleRetry;

internal class DefaultRetryExecutor(RetryPolicyOptions options, IServiceProvider serviceProvider, ILoggerFactory loggerFactory) : IRetryExecutor
{
    /// <inheritdoc />
    public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var attempt = 0;

        while (true)
        {
            try
            {
                await operation(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < options.MaxRetryCount && options.ShouldHandle(RetryOutcome.FromException(exception)))
            {
                await WaitForNextAttemptAsync(++attempt, RetryOutcome.FromException(exception), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var attempt = 0;

        while (true)
        {
            RetryOutcome outcome;

            try
            {
                var result = await operation(cancellationToken).ConfigureAwait(false);

                if (attempt >= options.MaxRetryCount)
                {
                    return result;
                }

                outcome = RetryOutcome.FromResult(result);

                if (!options.ShouldHandle(outcome))
                {
                    return result;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < options.MaxRetryCount && options.ShouldHandle(RetryOutcome.FromException(exception)))
            {
                outcome = RetryOutcome.FromException(exception);
            }

            await WaitForNextAttemptAsync(++attempt, outcome, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForNextAttemptAsync(int attempt, RetryOutcome outcome, CancellationToken cancellationToken)
    {
        var retryDelay = GetRetryDelay(attempt);

        if (options.OnRetry is not null)
        {
            await options.OnRetry(new(attempt, options.MaxRetryCount, retryDelay, outcome, serviceProvider, loggerFactory)).ConfigureAwait(false);
        }

        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
    }

    private TimeSpan GetRetryDelay(int attempt) => options.BackoffType switch
    {
        BackoffType.Constant => options.RetryDelay,
        BackoffType.Linear => options.RetryDelay * attempt,
        BackoffType.Exponential => options.RetryDelay * Math.Pow(2, attempt - 1),
        _ => throw new InvalidOperationException($"The retry backoff type '{options.BackoffType}' is not supported.")
    };
}
