using SimpleRetry;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();

builder.Services.AddSimpleRetry("TestService", options =>
{
    options.MaxRetryCount = 3;
    options.RetryDelay = TimeSpan.FromSeconds(2);
    options.BackoffType = BackoffType.Linear;
    options.ShouldHandle = outcome => outcome switch
    {
        { Exception: HttpRequestException or TaskCanceledException } => true,
        //{ Exception: TaskCanceledException { InnerException: TimeoutException } } => true,
        { Result: HttpResponseMessage { IsSuccessStatusCode: false } } => true,
        _ => false
    };
    //options.ShouldHandle = outcome => outcome.Exception is HttpRequestException
    //    || (outcome.TryGetResult(out HttpResponseMessage? response) && response?.IsSuccessStatusCode == false);
    options.OnRetry = args =>
    {
        // Handle the retry event (e.g., logging)
        Console.WriteLine($"Retry {args.AttemptNumber} of {args.MaxRetryCount} after {args.RetryDelay} due to {args.Outcome.Exception?.Message}");
        return Task.CompletedTask;
    };

}).AddSimpleRetry("AnotherService", options =>
{
    options.MaxRetryCount = 5;
    options.RetryDelay = TimeSpan.FromSeconds(1);
});

builder.Services.AddHttpClient("test").AddHttpSimpleRetry();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

app.MapOpenApi();
app.MapSwaggerUI(setupAction: options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "My API V1");
});

app.MapGet("/api/test", async ([FromKeyedServices("TestService")] IRetryExecutor pipelineExecutor) =>
{
    await pipelineExecutor.ExecuteAsync(async cancellationToken =>
    {
        throw new HttpRequestException();
    }, CancellationToken.None);
});

app.MapGet("/api/test2", async ([FromKeyedServices("AnotherService")] IRetryExecutor pipelineExecutor) =>
{
    var result = await pipelineExecutor.ExecuteAsync(async cancellationToken =>
    {
        return TypedResults.Ok();
    }, CancellationToken.None);

    return result;
});

app.Run();

