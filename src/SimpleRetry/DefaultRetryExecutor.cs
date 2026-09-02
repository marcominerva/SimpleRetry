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
                await ExecuteOperationAsync(operation, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (ShouldRetry(RetryOutcome.FromException(exception), attempt))
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
                var result = await ExecuteOperationAsync(operation, cancellationToken).ConfigureAwait(false);

                outcome = RetryOutcome.FromResult(result);

                if (!ShouldRetry(outcome, attempt))
                {
                    return result;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (ShouldRetry(RetryOutcome.FromException(exception), attempt))
            {
                outcome = RetryOutcome.FromException(exception);
            }

            await WaitForNextAttemptAsync(++attempt, outcome, cancellationToken).ConfigureAwait(false);
        }
    }

private async Task ExecuteOperationAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
{
    var task = operation(cancellationToken);

        if (options.AttemptTimeout is not TimeSpan attemptTimeout)
        {
            await task.ConfigureAwait(false);
            return;
        }

        try
        {
            await task.WaitAsync(attemptTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception) when (!task.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            throw new RetryTimeoutException(attemptTimeout, exception);
        }
    }

    private async Task<T> ExecuteOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var task = operation(cancellationToken);

        if (options.AttemptTimeout is not TimeSpan attemptTimeout)
        {
            return await task.ConfigureAwait(false);
        }

        try
        {
            return await task.WaitAsync(attemptTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception) when (!task.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            throw new RetryTimeoutException(attemptTimeout, exception);
        }
    }

    // A timeout is produced by the policy itself, so it is always retried regardless of the configured
    // predicate, which would otherwise have to know about an exception type it never throws.
    private bool ShouldRetry(RetryOutcome outcome, int attempt)
        => attempt < options.MaxRetryCount && (outcome.Exception is RetryTimeoutException || options.ShouldHandle(outcome));

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
