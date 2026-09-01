namespace SimpleRetry;

/// <summary>
/// Represents a timeout produced by the retry executor when an operation exceeds the configured request timeout.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="RetryTimeoutException" /> class.
/// </remarks>
/// <param name="timeout">The configured timeout that was exceeded.</param>
/// <param name="innerException">The exception produced by the underlying timeout operation.</param>
public sealed class RetryTimeoutException(TimeSpan timeout, Exception? innerException = null)
    : TimeoutException($"The operation exceeded the configured retry timeout of {timeout}.", innerException)
{
    /// <summary>
    /// Gets the configured timeout that was exceeded.
    /// </summary>
    public TimeSpan Timeout { get; } = timeout;
}
