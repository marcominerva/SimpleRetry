namespace SimpleRetry;

/// <summary>
/// Represents the outcome of a single execution attempt, which is either a result or an exception.
/// </summary>
/// <remarks>
/// This type mirrors the outcome-based model used by reactive resilience strategies, so that a retry can be
/// triggered not only by a thrown exception but also by a result that the caller considers a failure.
/// </remarks>
/// <seealso cref="RetryPolicyOptions.ShouldHandle"/>
public readonly struct RetryOutcome
{
    private RetryOutcome(object? result, Exception? exception)
    {
        Result = result;
        Exception = exception;
    }

    /// <summary>
    /// Gets the exception thrown by the operation, or <see langword="null"/> if the operation completed successfully.
    /// </summary>
    public Exception? Exception { get; }

    /// <summary>
    /// Gets the value returned by the operation, or <see langword="null"/> if the operation threw an exception
    /// or returned no value.
    /// </summary>
    public object? Result { get; }

    /// <summary>
    /// Gets a value indicating whether the operation failed with an exception.
    /// </summary>
    public bool IsException => Exception is not null;

    /// <summary>
    /// Creates an outcome that represents a failed execution attempt.
    /// </summary>
    /// <param name="exception">The exception thrown by the operation.</param>
    /// <returns>An outcome holding <paramref name="exception"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    public static RetryOutcome FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new(null, exception);
    }

    /// <summary>
    /// Creates an outcome that represents a completed execution attempt.
    /// </summary>
    /// <param name="result">The value returned by the operation.</param>
    /// <returns>An outcome holding <paramref name="result"/>.</returns>
    public static RetryOutcome FromResult(object? result) => new(result, null);

    /// <summary>
    /// Attempts to get the result of the operation as the specified type.
    /// </summary>
    /// <typeparam name="T">The expected type of the result.</typeparam>
    /// <param name="result">When this method returns, contains the typed result, if available.</param>
    /// <returns>
    /// <see langword="true"/> if the outcome holds a result of type <typeparamref name="T"/>; otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// This helper keeps <see cref="RetryPolicyOptions.ShouldHandle"/> predicates readable, because the outcome
    /// itself is not generic and therefore exposes <see cref="Result"/> as <see cref="object"/>.
    /// </remarks>
    public bool TryGetResult<T>(out T result)
    {
        if (Exception is null && Result is T typedResult)
        {
            result = typedResult;
            return true;
        }

        result = default!;
        return false;
    }
}
