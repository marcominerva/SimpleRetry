using Microsoft.Extensions.Logging;
using NSubstitute;

namespace SimpleRetry.UnitTests;

public class DefaultRetryExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_WhenOperationSucceeds_ExecutesOnce()
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
    public async Task ExecuteAsyncOfT_WhenResultIsNotHandled_ReturnsResult()
    {
        var executor = CreateExecutor(new());

        var result = await executor.ExecuteAsync(_ => Task.FromResult(42), TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOperationIsNull_ThrowsArgumentNullException()
    {
        var executor = CreateExecutor(new());

        await Assert.ThrowsAsync<ArgumentNullException>(() => executor.ExecuteAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => executor.ExecuteAsync<int>(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteAsync_WhenHandledExceptionOccurs_RetriesUpToLimitAndRethrows()
    {
        var exception = new InvalidOperationException("failure");
        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 2,
            RetryDelay = TimeSpan.Zero,
            ShouldHandle = outcome => outcome.Exception is InvalidOperationException
        });
        var attempts = 0;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(_ =>
        {
            attempts++;
            return Task.FromException(exception);
        }, TestContext.Current.CancellationToken));

        Assert.Same(exception, thrown);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_WhenExceptionIsNotHandled_DoesNotRetry()
    {
        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 3,
            ShouldHandle = _ => false
        });
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(_ =>
        {
            attempts++;
            throw new InvalidOperationException();
        }, TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsyncOfT_WhenResultIsHandled_DiscardsResultAndRetries()
    {
        var discarded = new List<int>();
        var attempts = 0;
        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 1,
            RetryDelay = TimeSpan.Zero,
            ShouldHandle = outcome => outcome.Result is 503,
            OnResultDiscarded = outcome => discarded.Add(Assert.IsType<int>(outcome.Result))
        });

        var result = await executor.ExecuteAsync(_ => Task.FromResult(++attempts == 1 ? 503 : 200), TestContext.Current.CancellationToken);

        Assert.Equal(200, result);
        Assert.Equal([503], discarded);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRetrying_ProvidesCallbackContextAndGeneratedDelay()
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        OnRetryArguments? callbackArguments = null;
        var exception = new InvalidOperationException();
        var options = new RetryPolicyOptions
        {
            MaxRetryCount = 1,
            RetryDelay = TimeSpan.FromDays(1),
            ShouldHandle = _ => true,
            RetryDelayGenerator = outcome =>
            {
                Assert.Same(exception, outcome.Exception);
                return TimeSpan.Zero;
            },
            OnRetry = arguments =>
            {
                callbackArguments = arguments;
                return Task.CompletedTask;
            }
        };
        var executor = new DefaultRetryExecutor(options, serviceProvider, loggerFactory);
        var attempts = 0;

        await executor.ExecuteAsync(_ => ++attempts == 1 ? Task.FromException(exception) : Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.NotNull(callbackArguments);
        Assert.Equal(1, callbackArguments.AttemptNumber);
        Assert.Equal(1, callbackArguments.MaxRetryCount);
        Assert.Equal(TimeSpan.Zero, callbackArguments.RetryDelay);
        Assert.Same(exception, callbackArguments.Outcome.Exception);
        Assert.Same(serviceProvider, callbackArguments.ServiceProvider);
        Assert.Same(loggerFactory, callbackArguments.LoggerFactory);
    }

    [Theory]
    [InlineData(BackoffType.Constant, 1)]
    [InlineData(BackoffType.Linear, 2)]
    [InlineData(BackoffType.Exponential, 2)]
    public async Task ExecuteAsync_WhenUsingBackoff_ComputesDelayForSecondRetry(BackoffType backoffType, int expectedMultiplier)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var configuredDelay = TimeSpan.FromMilliseconds(10);
        var observedDelays = new List<TimeSpan>();
        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 2,
            RetryDelay = configuredDelay,
            BackoffType = backoffType,
            OnRetry = arguments =>
            {
                observedDelays.Add(arguments.RetryDelay);
                if (arguments.AttemptNumber == 2)
                {
                    cancellation.Cancel();
                }

                return Task.CompletedTask;
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync(_ => Task.FromException(new InvalidOperationException()), cancellation.Token));

        Assert.Equal([configuredDelay, configuredDelay * expectedMultiplier], observedDelays);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBackoffTypeIsInvalid_ThrowsInvalidOperationException()
    {
        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 1,
            BackoffType = (BackoffType)int.MaxValue
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(_ => Task.FromException(new InvalidOperationException()), TestContext.Current.CancellationToken));

        Assert.Contains(int.MaxValue.ToString(), exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerCancels_PropagatesCancellationWithoutRetry()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var attempts = 0;
        var executor = CreateExecutor(new() { MaxRetryCount = 3, RetryDelay = TimeSpan.Zero });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(async token =>
        {
            attempts++;
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, cancellation.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAttemptTimesOut_RetriesRegardlessOfPredicate()
    {
        var attempts = 0;
        RetryOutcome? retryOutcome = null;
        var timeout = TimeSpan.FromMilliseconds(25);
        var executor = CreateExecutor(new()
        {
            MaxRetryCount = 1,
            RetryDelay = TimeSpan.Zero,
            AttemptTimeout = timeout,
            ShouldHandle = _ => false,
            OnRetry = arguments =>
            {
                retryOutcome = arguments.Outcome;
                return Task.CompletedTask;
            }
        });

        var result = await executor.ExecuteAsync(async token =>
        {
            if (++attempts == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return "completed";
        }, TestContext.Current.CancellationToken);

        Assert.Equal("completed", result);
        var timeoutException = Assert.IsType<RetryTimeoutException>(retryOutcome?.Exception);
        Assert.Equal(timeout, timeoutException.Timeout);
        Assert.IsAssignableFrom<OperationCanceledException>(timeoutException.InnerException);
    }

    private static DefaultRetryExecutor CreateExecutor(RetryPolicyOptions options)
        => new(options, Substitute.For<IServiceProvider>(), Substitute.For<ILoggerFactory>());
}
