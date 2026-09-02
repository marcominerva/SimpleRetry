using Microsoft.Extensions.Logging;

namespace SimpleRetry;

internal class DefaultRetryExecutor(RetryPolicyOptions options, IServiceProvider serviceProvider, ILoggerFactory loggerFactory) : IRetryExecutor
{
    /// <inheritdoc />
    public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await ExecuteAsync<object?>(async retryCancellationToken =>
        {
            await operation(retryCancellationToken).ConfigureAwait(false);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var attempt = 0;

        while (true)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < options.MaxRetryCount && options.ShouldHandle(exception))
            {
                attempt++;

                var retryDelay = GetRetryDelay(attempt);
                await OnRetryAsync(attempt, retryDelay, exception).ConfigureAwait(false);

                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Task OnRetryAsync(int attempt, TimeSpan retryDelay, Exception exception)
        => options.OnRetry?.Invoke(new(attempt, options.MaxRetryCount, retryDelay, exception, serviceProvider, loggerFactory)) ?? Task.CompletedTask;

    private TimeSpan GetRetryDelay(int attempt) => options.BackoffType switch
    {
        BackoffType.Constant => options.RetryDelay,
        BackoffType.Linear => options.RetryDelay * attempt,
        BackoffType.Exponential => options.RetryDelay * Math.Pow(2, attempt - 1),
        _ => throw new InvalidOperationException($"The retry backoff type '{options.BackoffType}' is not supported.")
    };
}
