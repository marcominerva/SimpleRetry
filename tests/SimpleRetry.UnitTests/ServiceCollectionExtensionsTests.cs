using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace SimpleRetry.UnitTests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSimpleRetryWhenConfigureIsNullThenThrowsArgumentNullException()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(() => services.AddSimpleRetry("test", (Action<RetryPolicyOptions>)null!));

        Assert.Equal("configure", exception.ParamName);
    }

    [Fact]
    public void AddSimpleRetryWhenRegisteredThenReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddSimpleRetry("test", static options => options.MaxRetryCount = 1);

        Assert.Same(services, result);
    }

    [Fact]
    public void AddSimpleRetryWhenResolvedThenUsesConfiguredOptions()
    {
        var services = new ServiceCollection();

        services.AddSimpleRetry("test", static options =>
        {
            options.MaxRetryCount = 7;
            options.RetryDelay = TimeSpan.FromSeconds(5);
        });

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredKeyedService<RetryPolicyOptions>("test");

        Assert.Equal(7, options.MaxRetryCount);
        Assert.Equal(TimeSpan.FromSeconds(5), options.RetryDelay);
    }

    [Fact]
    public void AddSimpleRetryWhenConfigureUsesServiceProviderThenProvidesServiceProvider()
    {
        var marker = new MarkerService(9);

        var services = new ServiceCollection();
        services.AddSingleton(marker);

        services.AddSimpleRetry("test", static (serviceProvider, options) =>
        {
            var marker = serviceProvider.GetRequiredService<MarkerService>();
            options.MaxRetryCount = marker.MaxRetryCount;
        });

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredKeyedService<RetryPolicyOptions>("test");

        Assert.Equal(marker.MaxRetryCount, options.MaxRetryCount);
    }

    [Fact]
    public async Task AddSimpleRetryWhenExecutorIsResolvedThenUsesKeyedRetryPolicy()
    {
        var services = new ServiceCollection();
        services.AddSimpleRetry("test", static options =>
        {
            options.MaxRetryCount = 1;
            options.RetryDelay = TimeSpan.Zero;
            options.ShouldHandle = static exception => exception is InvalidOperationException;
        });

        using var serviceProvider = services.BuildServiceProvider();
        var executor = serviceProvider.GetRequiredKeyedService<IRetryExecutor>("test");
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
    public async Task AddHttpSimpleRetryWhenRegisteredThenRetriesTransientStatusCode()
    {
        var handler = new SequenceHttpMessageHandler(static attempt => attempt == 1 ? new(HttpStatusCode.InternalServerError) : new(HttpStatusCode.OK));
        var services = new ServiceCollection();

        var builder = services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var result = builder.AddHttpSimpleRetry(options =>
        {
            options.MaxRetryCount = 1;
            options.RetryDelay = TimeSpan.Zero;
        });

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        using var response = await httpClientFactory.CreateClient("test").GetAsync("https://example.com", TestContext.Current.CancellationToken);

        Assert.Same(builder, result);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task AddHttpSimpleRetryWhenConfigureUsesServiceProviderThenProvidesServiceProvider()
    {
        var marker = new MarkerService(1);
        var handler = new SequenceHttpMessageHandler(static attempt => attempt == 1 ? new(HttpStatusCode.InternalServerError) : new(HttpStatusCode.OK));
        var services = new ServiceCollection();
        services.AddSingleton(marker);

        services.AddHttpClient("test")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddHttpSimpleRetry(static (serviceProvider, options) =>
            {
                var marker = serviceProvider.GetRequiredService<MarkerService>();
                options.MaxRetryCount = marker.MaxRetryCount;
                options.RetryDelay = TimeSpan.Zero;
            });

        await using var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        using var response = await httpClientFactory.CreateClient("test").GetAsync("https://example.com", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public void AddHttpSimpleRetryWhenConfigureIsNullThenThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("test");

        var exception = Assert.Throws<ArgumentNullException>(() => builder.AddHttpSimpleRetry((Action<RetryPolicyOptions>)null!));

        Assert.Equal("configure", exception.ParamName);
    }

    private sealed record MarkerService(int MaxRetryCount);

    private sealed class SequenceHttpMessageHandler(Func<int, HttpResponseMessage> createResponse) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(createResponse(SendCount));
        }
    }
}
