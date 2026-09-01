using SimpleRetryTools;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddSimpleRetry("TestService", options =>
{
    options.MaxRetryCount = 3;
    options.RetryDelay = TimeSpan.FromSeconds(2);
    options.BackoffType = BackoffType.Linear;
    options.ShouldHandle = ex => ex is HttpRequestException; // Only retry on HttpRequestException,
    options.OnRetry = args =>
    {
        // Handle the retry event (e.g., logging)
        Console.WriteLine($"Retry {args.AttemptNumber} of {args.MaxRetryCount} after {args.RetryDelay} due to {args.Exception?.Message}");
        return Task.CompletedTask;
    };

}).AddSimpleRetry("AnotherService", options =>
{
    options.MaxRetryCount = 5;
    options.RetryDelay = TimeSpan.FromSeconds(1);
});

builder.Services.AddHttpClient("test").AddStandardResilienceHandler();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

app.MapOpenApi();
app.MapSwaggerUI(setupAction: options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "My API V1");
});

//ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
//    .AddRetry(new RetryStrategyOptions()
//    {
//        OnRetry = (args) =>
//        {
//            // Handle the retry event (e.g., logging)
//            Console.WriteLine($"Retry {args.Context} after {delay} due to {outcome.Exception?.Message}");
//        }
//    }) // Add retry using the default options
//    .AddTimeout(TimeSpan.FromSeconds(10)) // Add 10 seconds timeout
//    .Build(); // Builds the resilience pipeline

//// Execute the pipeline asynchronously
//await pipeline.ExecuteAsync(

app.MapGet("/api/test", async ([FromKeyedServices("TestService")] IRetryExecutor pipelineExecutor) =>
{
    await pipelineExecutor.ExecuteAsync(async cancellationToken =>
    {
        throw new HttpRequestException();
    }, CancellationToken.None);
});

app.Run();

