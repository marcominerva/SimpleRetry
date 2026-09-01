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
        /// <summary>
        /// Adds a standard SimpleRetry delegating handler that retries transient HTTP failures.
        /// </summary>
        /// <returns>The HTTP client builder for chaining additional registrations.</returns>
        public IHttpClientBuilder AddHttpSimpleRetry()
            => builder.AddHttpSimpleRetry(static _ => { });

        /// <summary>
        /// Adds a standard SimpleRetry delegating handler that retries transient HTTP failures.
        /// </summary>
        /// <param name="configure">The callback used to configure the retry policy.</param>
        /// <returns>The HTTP client builder for chaining additional registrations.</returns>
        public IHttpClientBuilder AddHttpSimpleRetry(Action<RetryPolicyOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            return builder.AddHttpSimpleRetry((_, options) => configure(options));
        }

        /// <summary>
        /// Adds a standard SimpleRetry delegating handler that retries transient HTTP failures using configuration that can resolve services from the provider.
        /// </summary>
        /// <param name="configure">The callback used to configure the retry policy.</param>
        /// <returns>The HTTP client builder for chaining additional registrations.</returns>
        public IHttpClientBuilder AddHttpSimpleRetry(Action<IServiceProvider, RetryPolicyOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            builder.AddHttpMessageHandler(services =>
            {
                var options = CreateStandardResilienceOptions();
                configure(services, options);

                var loggerFactory = services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
                return new StandardResilienceHandler(options, services, loggerFactory);
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
        public IServiceCollection AddSimpleRetry(object? serviceKey, Action<IServiceProvider, RetryPolicyOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            services.AddKeyedSingleton(serviceKey, (services, _) =>
            {
                var options = new RetryPolicyOptions();
                configure(services, options);
                return options;
            });

            services.TryAddKeyedSingleton<IRetryExecutor>(KeyedService.AnyKey, (services, key) =>
            {
                var options = services.GetKeyedService<RetryPolicyOptions>(key) ?? new RetryPolicyOptions();
                var loggerFactory = services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
                return new DefaultRetryExecutor(options, services, loggerFactory);
            });

            return services;
        }
    }

    private static RetryPolicyOptions CreateStandardResilienceOptions()
        => new()
        {
            ShouldHandle = StandardResilienceHandler.ShouldHandle
        };
}
