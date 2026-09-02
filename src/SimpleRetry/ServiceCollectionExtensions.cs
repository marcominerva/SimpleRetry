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

            var options = new RetryPolicyOptions();
            configure(options);

            services.AddKeyedSingleton(serviceKey, options);
            AddRetryExecutor(services);

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

            AddRetryExecutor(services);

            return services;
        }

        private void AddRetryExecutor()
        {
            services.TryAddKeyedSingleton<IRetryExecutor>(KeyedService.AnyKey, (services, key) =>
            {
                var options = services.GetKeyedService<RetryPolicyOptions>(key) ?? new RetryPolicyOptions();
                var loggerFactory = services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
                return new DefaultRetryExecutor(options, services, loggerFactory);
            });
        }
    }
}
