using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SimpleRetryTools;

/// <summary>
/// Provides extension methods for registering SimpleRetryTools services.
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

            AddPipelineExecutor(services);
            services.AddKeyedSingleton(serviceKey, options);

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

            AddPipelineExecutor(services);
            services.AddKeyedSingleton(serviceKey, (services, _) =>
            {
                var options = new RetryPolicyOptions();
                configure(services, options);
                return options;
            });

            return services;
        }

        private void AddPipelineExecutor()
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
