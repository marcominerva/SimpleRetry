using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SimpleRetry;

/// <summary>
/// Provides extension methods for registering SimpleRetry services.
/// </summary>
public static class ServiceCollectionExtensions
{
    extension(IHttpClientBuilder builder)
    {
        public IHttpClientBuilder AddSimpleRetry(Action<RetryPolicyOptions>? configure, bool bufferRequestContent = false, bool cloneRequest = false)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            return builder.AddSimpleRetry((_, options) => configure(options), bufferRequestContent, cloneRequest);
        }

        public IHttpClientBuilder AddSimpleRetry(Action<IServiceProvider, RetryPolicyOptions>? configure = null,
            bool bufferRequestContent = false, bool cloneRequest = false)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Services.AddKeyedSingleton(builder.Name, (services, _) =>
            {
                var options = new RetryPolicyOptions
                {
                    MaxRetryCount = 3,
                    AttemptTimeout = TimeSpan.FromSeconds(10),
                    BackoffType = BackoffType.Exponential,
                    RetryDelay = TimeSpan.FromSeconds(2),
                    RetryDelayGenerator = HttpRetryDelegatingHandler.GetRetryAfterDelay,
                    ShouldHandle = HttpRetryDelegatingHandler.ShouldHandle,
                    OnResultDiscarded = HttpRetryDelegatingHandler.DisposeDiscardedResponse
                };

                configure?.Invoke(services, options);
                return options;
            });

            AddRetryExecutor(builder.Services);

            builder.AddHttpMessageHandler(services =>
            {
                var executor = services.GetKeyedService<IRetryExecutor>(builder.Name) ?? throw new InvalidOperationException($"No retry executor registered for key '{builder.Name}'.");
                return new HttpRetryDelegatingHandler(executor, bufferRequestContent, cloneRequest);
            });

            return builder;
        }
    }

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers a keyed retry policy using configuration that does not require a service provider.
        /// </summary>
        /// <param name="serviceKey">The key used to identify the registered retry policy.</param>
        /// <param name="configure">The callback used to configure the retry policy.</param>
        /// <returns>The service collection for chaining additional registrations.</returns>
        public IServiceCollection AddSimpleRetry(object? serviceKey, Action<RetryPolicyOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            services.AddSimpleRetry(serviceKey, (_, options) => configure(options));

            return services;
        }

        /// <summary>
        /// Registers a keyed retry policy using configuration that can resolve services from the provider.
        /// </summary>
        /// <param name="serviceKey">The key used to identify the registered retry policy.</param>
        /// <param name="configure">The callback used to configure the retry policy.</param>
        /// <returns>The service collection for chaining additional registrations.</returns>
        public IServiceCollection AddSimpleRetry(object? serviceKey, Action<IServiceProvider, RetryPolicyOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            services.AddKeyedSingleton(serviceKey, (services, _) =>
            {
                var options = new RetryPolicyOptions();
                configure?.Invoke(services, options);

                return options;
            });

            AddRetryExecutor(services);

            return services;
        }
    }

    private static void AddRetryExecutor(IServiceCollection services)
    {
        services.TryAddKeyedSingleton<IRetryExecutor>(KeyedService.AnyKey, (services, key) =>
        {
            var options = services.GetKeyedService<RetryPolicyOptions>(key) ?? new RetryPolicyOptions();
            var loggerFactory = services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            return new DefaultRetryExecutor(options, services, loggerFactory);
        });
    }
}
