# SimpleRetry

SimpleRetry is a small .NET library for executing asynchronous operations with a configurable retry policy.

It provides an `IRetryExecutor` service that can retry failed operations, retry returned results, apply a per-attempt timeout, calculate retry delays using different backoff strategies, and run custom logic before each retry.

## Features

- Execute asynchronous operations with retry support.
- Configure the maximum number of retry attempts.
- Configure a delay between retries.
- Choose a backoff strategy:
  - `Constant`
  - `Linear`
  - `Exponential`
- Apply a timeout to each operation attempt.
- Decide which exceptions or returned results should be retried with `ShouldHandle`.
- Override the delay for a specific outcome with `RetryDelayGenerator`.
- Dispose or otherwise release returned results that are discarded before a retry.
- Run custom logic before each retry with `OnRetry`.
- Register multiple keyed retry policies with dependency injection.
- Add retry support to `HttpClient` with `AddHttpSimpleRetry`.

## Configuration

Register retry policies with `AddSimpleRetry`:

```csharp
using SimpleRetry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSimpleRetry("ExternalApi", options =>
{
    options.MaxRetryCount = 3;
    options.RetryDelay = TimeSpan.FromSeconds(2);
    options.AttemptTimeout = TimeSpan.FromSeconds(10);
    options.BackoffType = BackoffType.Linear;
    options.ShouldHandle = outcome => outcome.Exception is HttpRequestException;
    options.OnRetry = arguments =>
    {
        Console.WriteLine($"Retry {arguments.AttemptNumber} of {arguments.MaxRetryCount} after {arguments.RetryDelay} because of {arguments.Outcome.Exception?.Message}");
        return Task.CompletedTask;
    };
});
```

### Options

| Option | Default | Description |
| --- | --- | --- |
| `MaxRetryCount` | `3` | Maximum number of retry attempts after the first failed execution. |
| `RetryDelay` | `TimeSpan.FromSeconds(2)` | Base delay used between retry attempts. |
| `AttemptTimeout` | `null` | Maximum duration allowed for each operation attempt. `null` disables the timeout. |
| `BackoffType` | `BackoffType.Constant` | Strategy used to calculate the delay before the next retry. |
| `ShouldHandle` | `outcome => outcome.IsException` | Predicate that determines whether an exception or returned result should be retried. |
| `OnRetry` | `null` | Optional callback invoked before each retry attempt. |
| `RetryDelayGenerator` | `null` | Optional callback that can override the delay for a specific retry outcome. Return `null` to use `RetryDelay` and `BackoffType`. |
| `OnResultDiscarded` | `null` | Optional callback invoked when a returned result is handled and discarded because a retry is about to start. |

## Retry outcomes

`ShouldHandle` receives a `RetryOutcome`. The outcome can represent either an exception or a returned result.

```csharp
options.ShouldHandle = outcome => outcome switch
{
    { Exception: HttpRequestException or TimeoutException } => true,
    { Result: HttpResponseMessage { IsSuccessStatusCode: false } } => true,
    _ => false
};
```

You can also inspect the result by type:

```csharp
options.ShouldHandle = outcome => outcome.Exception is HttpRequestException
    || (outcome.TryGetResult(out HttpResponseMessage? response) && !response.IsSuccessStatusCode);
```

If an operation returns a handled result and retries are exhausted, the last result is returned to the caller.

## Backoff strategies

Given `RetryDelay = TimeSpan.FromSeconds(2)`, the retry delays are calculated as follows:

| Backoff type | Attempt 1 | Attempt 2 | Attempt 3 |
| --- | --- | --- | --- |
| `Constant` | 2s | 2s | 2s |
| `Linear` | 2s | 4s | 6s |
| `Exponential` | 2s | 4s | 8s |

## Usage

Inject the keyed `IRetryExecutor` that matches the policy you registered:

```csharp
using Microsoft.Extensions.DependencyInjection;
using SimpleRetry;

app.MapGet("/api/weather", async ([FromKeyedServices("ExternalApi")] IRetryExecutor retryExecutor, CancellationToken cancellationToken) =>
{
    await retryExecutor.ExecuteAsync(async token =>
    {
        using var httpClient = new HttpClient();
        using var response = await httpClient.GetAsync("https://example.com/weather", token);
        response.EnsureSuccessStatusCode();
    }, cancellationToken);

    return Results.Ok();
});
```

You can retry based on the returned value:

```csharp
app.MapGet("/api/weather", async ([FromKeyedServices("ExternalApi")] IRetryExecutor retryExecutor, CancellationToken cancellationToken) =>
{
    using var response = await retryExecutor.ExecuteAsync(async token =>
    {
        using var httpClient = new HttpClient();
        return await httpClient.GetAsync("https://example.com/weather", token);
    }, cancellationToken);

    return Results.StatusCode((int)response.StatusCode);
});
```

Use the generic overload when the operation returns a value:

```csharp
app.MapGet("/api/value", async ([FromKeyedServices("ExternalApi")] IRetryExecutor retryExecutor, CancellationToken cancellationToken) =>
{
    var value = await retryExecutor.ExecuteAsync(async token =>
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), token);
        return 42;
    }, cancellationToken);

    return Results.Ok(value);
});
```

## Multiple policies

You can register more than one retry policy by using different service keys:

```csharp
builder.Services
    .AddSimpleRetry("ExternalApi", options =>
    {
        options.MaxRetryCount = 3;
        options.RetryDelay = TimeSpan.FromSeconds(2);
        options.BackoffType = BackoffType.Exponential;
        options.ShouldHandle = outcome => outcome.Exception is HttpRequestException;
    })
    .AddSimpleRetry("Database", options =>
    {
        options.MaxRetryCount = 5;
        options.RetryDelay = TimeSpan.FromMilliseconds(200);
        options.BackoffType = BackoffType.Linear;
    });
```

Then resolve the policy you need:

```csharp
public sealed class MyService([FromKeyedServices("Database")] IRetryExecutor retryExecutor)
{
    public Task SaveAsync(CancellationToken cancellationToken)
        => retryExecutor.ExecuteAsync(async token =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), token);
        }, cancellationToken);
}
```

## Per-attempt timeout

`AttemptTimeout` limits each individual execution attempt. It is not a total timeout for the whole retry operation.

For example, with `MaxRetryCount = 2` and `AttemptTimeout = TimeSpan.FromSeconds(3)`, the operation may be attempted up to three times, and each attempt can run for up to three seconds.

When an attempt exceeds the configured timeout, SimpleRetry throws a `RetryTimeoutException`. Timeout failures caused by `AttemptTimeout` are retryable even when `ShouldHandle` returns `false`.

The timeout is cooperative: SimpleRetry creates a linked cancellation token for each attempt and cancels it when the attempt timeout expires. The operation receives that token and can release its resources normally.

Caller cancellation is still treated differently. If the caller's `CancellationToken` is canceled, `OperationCanceledException` is propagated immediately and no further retry is attempted.

## Custom retry delays

Use `RetryDelayGenerator` when the delay depends on the failure or result:

```csharp
options.RetryDelayGenerator = outcome =>
{
    if (outcome.Result is HttpResponseMessage response && response.Headers.RetryAfter?.Delta is TimeSpan retryAfter)
    {
        return retryAfter;
    }

    return null;
};
```

Returning `null` uses the normal `RetryDelay` and `BackoffType` calculation.

## Discarded results

When a returned result is handled by the policy, SimpleRetry retries the operation and discards that result. Use `OnResultDiscarded` to release resources owned by discarded values:

```csharp
options.OnResultDiscarded = outcome =>
{
    if (outcome.Result is IDisposable disposable)
    {
        disposable.Dispose();
    }
};
```

The callback is invoked only for handled results. Exceptions are not reported through `OnResultDiscarded`.

## HTTP retries

Use `AddHttpSimpleRetry` to add the built-in HTTP retry handler to an `HttpClient`:

```csharp
builder.Services.AddHttpClient("ExternalApi", client =>
{
    client.BaseAddress = new("https://example.com");
})
.AddHttpSimpleRetry(options =>
{
    options.MaxRetryCount = 3;
});
```

The HTTP policy is registered as a keyed retry policy using the HTTP client name. For example, the previous registration stores its `RetryPolicyOptions` and `IRetryExecutor` under the `"ExternalApi"` key.

The default HTTP policy:

- Retries `HttpRequestException`.
- Retries `RetryTimeoutException` caused by `AttemptTimeout`.
- Retries HTTP `408 Request Timeout`, `429 Too Many Requests`, and `5xx` responses.
- Uses `AttemptTimeout = TimeSpan.FromSeconds(10)`.
- Uses `RetryDelay = TimeSpan.FromSeconds(2)`.
- Uses `BackoffType.Exponential`.
- Honors `Retry-After` when the response contains either a delta or a date.
- Disposes handled `HttpResponseMessage` instances that are discarded before retrying.

If a response does not contain `Retry-After`, the handler falls back to the configured `RetryDelay` and `BackoffType`.

Each HTTP attempt uses a cloned `HttpRequestMessage`. If the original request has content, the content is buffered before the first attempt so each retry can send a fresh request body.

## Cancellation

Pass a `CancellationToken` to stop the current operation or the delay before the next retry:

```csharp
await retryExecutor.ExecuteAsync(async cancellationToken =>
{
    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
}, cancellationToken);
```

When cancellation is requested, `OperationCanceledException` is propagated immediately and no further retry is attempted.
