using Microsoft.Extensions.Logging;

namespace SimpleRetry;

/// <summary>
/// Provides contextual information for an asynchronous retry callback.
/// </summary>
/// <param name="attemptNumber">The current retry attempt number.</param>
/// <param name="maxRetryCount">The maximum number of retry attempts configured for the operation.</param>
/// <param name="retryDelay">The delay before the next retry attempt.</param>
/// <param name="exception">The exception that caused the retry.</param>
/// <param name="serviceProvider">The service provider associated with the retry executor.</param>
/// <param name="loggerFactory">The logger factory available to retry callbacks.</param>
public sealed class OnRetryArguments(int attemptNumber, int maxRetryCount, TimeSpan retryDelay, Exception exception, IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
{
    /// <summary>
    /// Gets the current retry attempt number.
    /// </summary>
    public int AttemptNumber { get; } = attemptNumber;

    /// <summary>
    /// Gets the maximum number of retry attempts configured for the operation.
    /// </summary>
    public int MaxRetryCount { get; } = maxRetryCount;

    /// <summary>
    /// Gets the delay before the next retry attempt.
    /// </summary>
    public TimeSpan RetryDelay { get; } = retryDelay;

    /// <summary>
    /// Gets the exception that caused the retry.
    /// </summary>
    public Exception Exception { get; } = exception;

    /// <summary>
    /// Gets the service provider associated with the retry executor.
    /// </summary>
    public IServiceProvider ServiceProvider { get; } = serviceProvider;

    /// <summary>
    /// Gets the logger factory available to retry callbacks.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;
}
