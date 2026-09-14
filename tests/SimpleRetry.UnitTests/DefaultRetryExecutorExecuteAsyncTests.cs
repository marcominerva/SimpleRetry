using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace SimpleRetry.UnitTests;

public partial class DefaultRetryExecutorExecuteAsyncTests
{
    [Fact]
    public void RetryPolicyOptionsWhenCreatedThenUsesExpectedDefaults()
    {
        var options = new RetryPolicyOptions();

        Assert.Equal(3, options.MaxRetryCount);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryDelay);
        Assert.Null(options.AttemptTimeout);
        Assert.Equal(BackoffType.Constant, options.BackoffType);
        Assert.True(options.ShouldHandle(RetryOutcome.FromException(new InvalidOperationException())));
        Assert.False(options.ShouldHandle(RetryOutcome.FromResult("result")));
        Assert.Null(options.OnRetry);
    }

    [Fact]
    public async Task WhenOperationSucceedsThenRunsOnce()
    {
        var executor = CreateExecutor(new());
        var attempts = 0;

        await executor.ExecuteAsync(_ =>
        {
            attempts++;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WhenHandledExceptionIsThrownThenRetriesOperation()
    {
        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 3,
            RetryDelay = TimeSpan.Zero,
            ShouldHandle = outcome => outcome.Exception is InvalidOperationException
        });

        var attempts = 0;

        await executor.ExecuteAsync(_ =>
        {
            attempts++;

            if (attempts == 1)
            {
                throw new InvalidOperationException();
            }

            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task WhenAttemptTimeoutExpiresThenRetriesOperation()
    {
        var handledExceptions = new List<Exception?>();
        var retryExceptions = new List<Exception?>();
        var attemptTimeout = TimeSpan.FromSeconds(3);
        var operationDuration = TimeSpan.FromSeconds(5);

        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 1,
            RetryDelay = TimeSpan.Zero,
            AttemptTimeout = attemptTimeout,
            ShouldHandle = outcome =>
            {
                handledExceptions.Add(outcome.Exception!);
                return false;
            },
            OnRetry = arguments =>
            {
                retryExceptions.Add(arguments.Outcome.Exception);
                return Task.CompletedTask;
            }
        });

        var attempts = 0;

        var exception = await Assert.ThrowsAsync<RetryTimeoutException>(() => executor.ExecuteAsync(cancellationToken =>
        {
            attempts++;
            return Task.Delay(operationDuration, cancellationToken);
        }, TestContext.Current.CancellationToken));

        Assert.Equal(attemptTimeout, exception.Timeout);

        Assert.Equal(2, attempts);
        Assert.Empty(handledExceptions);

        var retryException = Assert.Single(retryExceptions);
        Assert.IsType<RetryTimeoutException>(retryException);
    }

    [Fact]
    public async Task WhenAttemptTimeoutExpiresThenCancelsRunningOperation()
    {
        var observedCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 0,
            RetryDelay = TimeSpan.Zero,
            AttemptTimeout = TimeSpan.FromMilliseconds(50)
        });

        await Assert.ThrowsAsync<RetryTimeoutException>(() => executor.ExecuteAsync(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(() => observedCancellation.TrySetResult(true));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }, TestContext.Current.CancellationToken));

        Assert.True(await observedCancellation.Task);
    }

    [Fact]
    public async Task WhenCallerCancellationIsSignaledDuringAttemptTimeoutThenDoesNotThrowRetryTimeoutException()
    {
        using var cancellationTokenSource = new CancellationTokenSource();

        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 3,
            RetryDelay = TimeSpan.Zero,
            AttemptTimeout = TimeSpan.FromMinutes(1)
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(async cancellationToken =>
        {
            cancellationTokenSource.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }, cancellationTokenSource.Token));
    }

    [Fact]
    public async Task WhenMaxRetryCountIsZeroThenDoesNotRetry()
    {
        var retryCalled = false;

        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 0,
            RetryDelay = TimeSpan.Zero,
            ShouldHandle = outcome => outcome.Exception is InvalidOperationException,
            OnRetry = _ =>
            {
                retryCalled = true;
                return Task.CompletedTask;
            }
        });

        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(_ =>
        {
            attempts++;
            throw new InvalidOperationException();
        }, TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        Assert.False(retryCalled);
    }

    [Fact]
    public async Task WhenAttemptTimeoutIsNullThenDoesNotApplyTimeout()
    {
        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 1,
            RetryDelay = TimeSpan.Zero,
            AttemptTimeout = null
        });

        var attempts = 0;

        await executor.ExecuteAsync(async cancellationToken =>
        {
            attempts++;
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WhenOperationThrowsTimeoutExceptionThenUsesShouldHandle()
    {
        var handledExceptions = new List<Exception>();

        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 1,
            RetryDelay = TimeSpan.Zero,
            AttemptTimeout = TimeSpan.FromSeconds(1),
            ShouldHandle = outcome =>
            {
                handledExceptions.Add(outcome.Exception!);
                return false;
            }
        });

        var attempts = 0;

        await Assert.ThrowsAsync<TimeoutException>(() => executor.ExecuteAsync(_ =>
        {
            attempts++;
            throw new TimeoutException();
        }, TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);

        var handledException = Assert.Single(handledExceptions);
        Assert.IsType<TimeoutException>(handledException);
    }

    [Fact]
    public async Task WhenShouldHandleThrowsThenPropagatesOriginalException()
    {
        var operationException = new InvalidOperationException();

        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 1,
            RetryDelay = TimeSpan.Zero,
            ShouldHandle = _ => throw new ApplicationException()
        });

        var attempts = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(_ =>
        {
            attempts++;
            throw operationException;
        }, TestContext.Current.CancellationToken));

        Assert.Same(operationException, exception);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WhenShouldHandleIsNotConfiguredThenRetriesOperation()
    {
        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 1,
            RetryDelay = TimeSpan.Zero
        });

        var attempts = 0;

        await executor.ExecuteAsync(_ =>
        {
            attempts++;

            if (attempts == 1)
            {
                throw new InvalidOperationException();
            }

            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task WhenHandledAndUnhandledExceptionsAreThrownThenRetriesOnlyHandledExceptions()
    {
        var retryExceptions = new List<Exception?>();

        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 3,
            RetryDelay = TimeSpan.Zero,
            ShouldHandle = outcome => outcome.Exception is InvalidOperationException,
            OnRetry = arguments =>
            {
                retryExceptions.Add(arguments.Outcome.Exception);
                return Task.CompletedTask;
            }
        });

        var attempts = 0;

        await Assert.ThrowsAsync<NotSupportedException>(() => executor.ExecuteAsync(_ =>
        {
            attempts++;

            if (attempts < 3)
            {
                throw new InvalidOperationException();
            }

            throw new NotSupportedException();
        }, TestContext.Current.CancellationToken));

        Assert.Equal(3, attempts);
        Assert.All(retryExceptions, exception => Assert.IsType<InvalidOperationException>(exception));
    }

    [Theory]
    [InlineData(BackoffType.Constant, 2, 2, 2)]
    [InlineData(BackoffType.Linear, 2, 4, 6)]
    [InlineData(BackoffType.Exponential, 2, 4, 8)]
    public async Task WhenBackoffTypeVariesThenReportsExpectedRetryDelays(BackoffType backoffType, int firstDelayMilliseconds, int secondDelayMilliseconds, int thirdDelayMilliseconds)
    {
        var retryDelays = new List<TimeSpan>();

        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 3,
            RetryDelay = TimeSpan.FromMilliseconds(2),
            BackoffType = backoffType,
            ShouldHandle = outcome => outcome.Exception is InvalidOperationException,
            OnRetry = arguments =>
            {
                retryDelays.Add(arguments.RetryDelay);
                return Task.CompletedTask;
            }
        });

        var attempts = 0;

        await executor.ExecuteAsync(_ =>
        {
            attempts++;

            if (attempts <= 3)
            {
                throw new InvalidOperationException();
            }

            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(4, attempts);
        Assert.Equal([
            TimeSpan.FromMilliseconds(firstDelayMilliseconds),
            TimeSpan.FromMilliseconds(secondDelayMilliseconds),
            TimeSpan.FromMilliseconds(thirdDelayMilliseconds)
        ], retryDelays);
    }

    [Fact]
    public async Task WhenMaxRetryCountIsGreaterThanOneThenRetriesUntilOperationSucceeds()
    {
        var retryAttempts = new List<int>();

        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 2,
            RetryDelay = TimeSpan.Zero,
            ShouldHandle = outcome => outcome.Exception is InvalidOperationException,
            OnRetry = arguments =>
            {
                retryAttempts.Add(arguments.AttemptNumber);
                return Task.CompletedTask;
            }
        });

        var attempts = 0;

        await executor.ExecuteAsync(_ =>
        {
            attempts++;

            if (attempts <= 2)
            {
                throw new InvalidOperationException();
            }

            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(3, attempts);
        Assert.Equal([1, 2], retryAttempts);
    }

    [Fact]
    public async Task WhenRetryIsAttemptedThenPassesExpectedOnRetryArguments()
    {
        var retryArguments = new List<OnRetryArguments>();
        var exceptionToHandle = new InvalidOperationException();

        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 2,
            RetryDelay = TimeSpan.FromMilliseconds(25),
            ShouldHandle = outcome => ReferenceEquals(outcome.Exception, exceptionToHandle),
            OnRetry = arguments =>
            {
                retryArguments.Add(arguments);
                return Task.CompletedTask;
            }
        });

        var attempts = 0;

        await executor.ExecuteAsync(_ =>
        {
            attempts++;

            if (attempts == 1)
            {
                throw exceptionToHandle;
            }

            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        var arguments = Assert.Single(retryArguments);

        Assert.Equal(1, arguments.AttemptNumber);
        Assert.Equal(2, arguments.MaxRetryCount);
        Assert.Equal(TimeSpan.FromMilliseconds(25), arguments.RetryDelay);
        Assert.Same(exceptionToHandle, arguments.Outcome.Exception);
        Assert.Same(NullServiceProvider.Instance, arguments.ServiceProvider);
        Assert.Same(NullLoggerFactory.Instance, arguments.LoggerFactory);
    }

    [Fact]
    public async Task WhenCancellationTokenIsSignaledDuringRetryDelayThenThrowsImmediately()
    {
        using var cancellationTokenSource = new CancellationTokenSource();

        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 3,
            RetryDelay = TimeSpan.FromMinutes(1),
            ShouldHandle = outcome => outcome.Exception is InvalidOperationException,
            OnRetry = async _ => await cancellationTokenSource.CancelAsync()
        });

        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(_ =>
        {
            attempts++;
            throw new InvalidOperationException();
        }, cancellationTokenSource.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WhenCancellationTokenIsSignaledThenThrowsImmediately()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        var retryCalled = false;
        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 3,
            RetryDelay = TimeSpan.Zero,
            ShouldHandle = _ => true,
            OnRetry = _ =>
            {
                retryCalled = true;
                return Task.CompletedTask;
            }
        });

        var attempts = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() => executor.ExecuteAsync(cancellationToken =>
        {
            attempts++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, cancellationTokenSource.Token));

        Assert.Equal(1, attempts);
        Assert.False(retryCalled);
    }

    [Fact]
    public async Task WhenOperationSucceedsThenReturnsResult()
    {
        var executor = CreateExecutor(new());

        var result = await executor.ExecuteAsync(_ => Task.FromResult(42), TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task WhenHandledExceptionIsThrownThenRetriesOperationAndReturnsResult()
    {
        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 1,
            RetryDelay = TimeSpan.Zero,
            ShouldHandle = outcome => outcome.Exception is InvalidOperationException
        });

        var attempts = 0;

        var result = await executor.ExecuteAsync(_ =>
        {
            attempts++;

            if (attempts == 1)
            {
                throw new InvalidOperationException();
            }

            return Task.FromResult(42);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task WhenHandledResultIsReturnedThenRetriesOperationAndReturnsResult()
    {
        var outcomes = new List<RetryOutcome>();

        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 1,
            RetryDelay = TimeSpan.Zero,
            ShouldHandle = outcome => outcome is { Result: HttpStatusCode.ServiceUnavailable },
            OnRetry = arguments =>
            {
                outcomes.Add(arguments.Outcome);
                return Task.CompletedTask;
            }
        });

        var attempts = 0;

        var result = await executor.ExecuteAsync(_ =>
        {
            attempts++;
            return Task.FromResult(attempts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, result);
        Assert.Equal(2, attempts);

        var outcome = Assert.Single(outcomes);
        Assert.Null(outcome.Exception);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, outcome.Result);
    }

    [Fact]
    public async Task WhenHandledResultIsReturnedAndRetriesAreExhaustedThenReturnsLastResult()
    {
        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 2,
            RetryDelay = TimeSpan.Zero,
            ShouldHandle = outcome => outcome is { Result: HttpStatusCode.ServiceUnavailable }
        });

        var attempts = 0;

        var result = await executor.ExecuteAsync(_ =>
        {
            attempts++;
            return Task.FromResult(HttpStatusCode.ServiceUnavailable);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task WhenUnhandledExceptionIsThrownThenDoesNotRetry()
    {
        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 1,
            RetryDelay = TimeSpan.Zero,
            ShouldHandle = outcome => outcome.Exception is InvalidOperationException
        });

        var attempts = 0;

        await Assert.ThrowsAsync<NotSupportedException>(() => executor.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw new NotSupportedException();
        }, TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
    }

    private static DefaultRetryExecutor CreateExecutor(RetryPolicyOptions options)
        => new(options, NullServiceProvider.Instance, NullLoggerFactory.Instance);

    private sealed class NullServiceProvider : IServiceProvider
    {
        public static NullServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
