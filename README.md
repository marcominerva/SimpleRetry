# SimpleRetry

SimpleRetry is a small .NET library for executing asynchronous operations with a configurable retry policy.

It provides an `IRetryExecutor` service that can retry failed operations, apply a per-attempt timeout, calculate retry delays using different backoff strategies, and run custom logic before each retry.

## Features

- Execute asynchronous operations with retry support.
- Configure the maximum number of retry attempts.
- Configure a delay between retries.
- Choose a backoff strategy:
  - `Constant`
  - `Linear`
  - `Exponential`
- Apply a timeout to each operation attempt.
- Decide which exceptions should be retried with `ShouldHandle`.
- Run custom logic before each retry with `OnRetry`.
- Register multiple keyed retry policies with dependency injection.

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
    options.ShouldHandle = exception => exception is HttpRequestException;
    options.OnRetry = arguments =>
    {
        Console.WriteLine($"Retry {arguments.AttemptNumber} of {arguments.MaxRetryCount} after {arguments.RetryDelay} because of {arguments.Exception.Message}");
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
| `ShouldHandle` | `_ => true` | Predicate that determines whether an exception should be retried. If set to `null`, all exceptions are treated as retryable. |
| `OnRetry` | `null` | Optional callback invoked before each retry attempt. |

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
        options.ShouldHandle = exception => exception is HttpRequestException;
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

## Cancellation

Pass a `CancellationToken` to stop the current operation or the delay before the next retry:

```csharp
await retryExecutor.ExecuteAsync(async cancellationToken =>
{
    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
}, cancellationToken);
```

When cancellation is requested, `OperationCanceledException` is propagated immediately and no further retry is attempted.
