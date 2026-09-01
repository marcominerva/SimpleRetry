namespace SimpleRetryTools;

/// <summary>
/// Defines the configuration exposed by a pipeline executor.
/// </summary>
public interface IRetryExecutor
{
    /// <summary>
    /// Executes the specified asynchronous operation using the configured retry policy.
    /// </summary>
    /// <param name="operation">The asynchronous operation to execute.</param>
    /// <param name="cancellationToken">The token used to cancel the operation or retry delay.</param>
    /// <returns>A task that represents the asynchronous execution.</returns>
    Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the specified asynchronous operation using the configured retry policy and returns its result.
    /// </summary>
    /// <typeparam name="T">The type of the operation result.</typeparam>
    /// <param name="operation">The asynchronous operation to execute.</param>
    /// <param name="cancellationToken">The token used to cancel the operation or retry delay.</param>
    /// <returns>A task that represents the asynchronous execution and contains the operation result.</returns>
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
}