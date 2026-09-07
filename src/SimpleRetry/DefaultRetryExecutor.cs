using Microsoft.Extensions.Logging;

namespace SimpleRetry;

internal class DefaultRetryExecutor(RetryPolicyOptions options, IServiceProvider serviceProvider, ILoggerFactory loggerFactory) : IRetryExecutor
{
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

    private Task ExecuteOperationAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
        => ExecuteOperationAsync<object?>(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    private async Task<T> ExecuteOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        // The attempt timeout is applied by cancelling a linked token instead of just giving up on the returned task,
        // so that the operation itself observes the cancellation and can release its resources.
        if (options.AttemptTimeout is not TimeSpan attemptTimeout)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        // The linked source merges the caller cancellation with the attempt timeout into a single token, so the operation
        // is cancelled by whichever happens first, and it can be cancelled without touching the caller token, which is not owned here.
        // It is created per attempt, so every retry starts with a fresh timeout and the registration on the caller token is released by the using.
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(attemptTimeout);

        try
        {
            return await operation(timeoutCancellation.Token).ConfigureAwait(false);
        }
        // Only the linked source being cancelled means the timeout expired; if the caller token is cancelled too,
        // the original exception is propagated as-is, so the cancellation is not turned into a retriable timeout.
        catch (OperationCanceledException exception) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new RetryTimeoutException(attemptTimeout, exception);
        }
    }

    // A timeout is produced by the policy itself, so it is always retried regardless of the configured
    // predicate, which would otherwise have to know about an exception type it never throws.
    private bool ShouldRetry(RetryOutcome outcome, int attempt)
        => attempt < options.MaxRetryCount && (outcome.Exception is RetryTimeoutException || (options.ShouldHandle?.Invoke(outcome) ?? true));

    private async Task WaitForNextAttemptAsync(int attempt, RetryOutcome outcome, CancellationToken cancellationToken)
    {
        var retryDelay = options.RetryDelayGenerator?.Invoke(outcome) ?? GetRetryDelay(attempt);

        if (options.OnRetry is not null)
        {
            await options.OnRetry(new(attempt, options.MaxRetryCount, retryDelay, outcome, serviceProvider, loggerFactory)).ConfigureAwait(false);
        }

        if (!outcome.IsException)
        {
            options.OnResultDiscarded?.Invoke(outcome);
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
