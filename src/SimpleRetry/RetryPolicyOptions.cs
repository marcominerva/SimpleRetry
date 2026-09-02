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
    /// Gets or sets the maximum amount of time allowed for each operation attempt. A <see langword="null" /> value disables the timeout.
    /// </summary>
    public TimeSpan? AttemptTimeout { get; set; }

    /// <summary>
    /// Gets or sets the strategy used to calculate the delay between retry attempts.
    /// </summary>
    public BackoffType BackoffType { get; set; } = BackoffType.Constant;

    /// <summary>
    /// Gets or sets the predicate used to determine whether the outcome of an execution attempt should be
    /// handled by the retry policy.
    /// </summary>
    /// <remarks>
    /// The predicate receives both faulted and successful outcomes, so a retry can be triggered by a returned
    /// value as well as by an exception. The default implementation retries every handled exception and never
    /// retries a successful result.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// options.ShouldHandle = outcome => outcome.Exception is HttpRequestException
    ///     || (outcome.TryGetResult(out HttpResponseMessage? response) &amp;&amp; !response.IsSuccessStatusCode);
    /// </code>
    /// </example>
    public Func<RetryOutcome, bool> ShouldHandle { get; set; } = static outcome => outcome.IsException;

    /// <summary>
    /// Gets or sets the asynchronous callback invoked before each retry attempt.
    /// </summary>
    public Func<OnRetryArguments, Task>? OnRetry { get; set; }
}
