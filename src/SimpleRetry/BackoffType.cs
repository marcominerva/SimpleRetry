namespace SimpleRetryTools;

/// <summary>
/// Specifies how the delay between retry attempts is calculated.
/// </summary>
public enum BackoffType
{
    /// <summary>
    /// Uses the configured retry delay for every retry attempt.
    /// </summary>
    Constant,

    /// <summary>
    /// Increases the configured retry delay linearly based on the current retry attempt.
    /// </summary>
    Linear,

    /// <summary>
    /// Increases the configured retry delay exponentially based on the current retry attempt.
    /// </summary>
    Exponential
}
