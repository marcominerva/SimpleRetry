using System.Net;
using SimpleRetry;
using TinyHelpers.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddSimpleRetry("MyPolicy1", options =>
{
    options.MaxRetryCount = 2;
    options.RetryDelay = TimeSpan.FromSeconds(1);
    options.ShouldHandle = outcome =>
    {
        if (outcome.TryGetResult<HttpResponseMessage>(out var result))
        {
            return result.StatusCode == HttpStatusCode.TooManyRequests;
        }

        return outcome.Exception is HttpRequestException;
    };
})
.AddSimpleRetry("MyPolicy2", (services, options) =>
{
    options.MaxRetryCount = 5;
    options.RetryDelay = TimeSpan.FromSeconds(1);
    options.BackoffType = BackoffType.Exponential;
    options.OnRetry = async args =>
    {
        var logger = args.LoggerFactory.CreateLogger<Program>();
        logger.LogWarning("Retry attempt {AttemptNumber}/{MaxRetryCount} after {Delay} due to exception: {Exception}",
            args.AttemptNumber, args.MaxRetryCount, args.RetryDelay, args.Outcome.Exception?.Message);

        await Task.CompletedTask;
    };
});

builder.Services.AddDefaultProblemDetails();
builder.Services.AddDefaultExceptionHandler();

builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

app.UseStatusCodePages();
app.UseExceptionHandler();

app.MapOpenApi();
app.MapSwaggerUI(setupAction: options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "My API V1");
});

app.MapPost("/api/retry1", async ([FromKeyedServices("MyPolicy1")] IRetryExecutor retryExecutor, CancellationToken cancellationToken) =>
{
    await retryExecutor.ExecuteAsync(async ct =>
    {
        // Simulate some work that may fail
        await Task.Delay(500, ct);
        var crash = Random.Shared.Next(0, 5);

        if (crash < 2)
        {
            throw new ApplicationException("Simulated failure");
        }
        else if (crash == 3)
        {
            throw new Exception("Simulated failure");
        }
    }, cancellationToken);
});

app.MapPost("/api/retry2", async ([FromKeyedServices("MyPolicy2")] IRetryExecutor retryExecutor, CancellationToken cancellationToken) =>
{
    await retryExecutor.ExecuteAsync(async ct =>
    {
        // Simulate some work that may fail
        throw new Exception("Simulated failure");
    }, cancellationToken);
});

app.Run();

