namespace SimpleRetry;

/// <summary>
/// Represents the configuration for retry operations.
/// </summary>
public class RetryPolicyOptions
{
    /// <summary>
    /// Gets or sets the maximum number of retry attempts.
    /// </summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// Gets or sets the delay between retry attempts.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the strategy used to calculate the delay between retry attempts.
    /// </summary>
    public BackoffType BackoffType { get; set; } = BackoffType.Constant;

    /// <summary>
    /// Gets or sets the predicate used to determine whether an exception should be handled by the retry policy.
    /// </summary>
    public Func<Exception, bool> ShouldHandle { get; set; } = _ => true;

    /// <summary>
    /// Gets or sets the asynchronous callback invoked before each retry attempt.
    /// </summary>
    public Func<OnRetryArguments, Task>? OnRetry { get; set; }
}
