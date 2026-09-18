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
        /// <param name="configure">The callback used to configure the retry policy.</param>
        /// <param name="bufferRequestContent">
        /// <see langword="true"/> to buffer non-replayable request bodies in memory so that they can be sent again on every
        /// attempt; <see langword="false"/> to reject such requests up front.
        /// </param>
        /// <param name="cloneRequest">
        /// <see langword="true"/> to send a fresh copy of the request message on every attempt, so that mutations applied
        /// by the inner handlers (added headers, rewritten URIs) never leak into the following attempts;
        /// <see langword="false"/> to send the very same <see cref="HttpRequestMessage"/> instance every time.
        /// </param>
        /// <returns>The HTTP client builder for chaining additional registrations.</returns>
        /// <remarks>
        /// The retry policy is registered as a keyed service using the HTTP client name, so the very same
        /// <see cref="IRetryExecutor"/> that runs standalone operations also drives the HTTP pipeline.
        /// <para>
        /// By default the handler resends the original request message through a plain <c>base.SendAsync(request, …)</c>,
        /// exactly like the standard <c>Microsoft.Extensions.Http.Resilience</c> handler: no message is allocated per
        /// attempt. The cost is that every mutation applied by the inner handlers
        /// accumulates across attempts: headers can end up duplicated or overwritten, and a retry follows the URI that a
        /// redirect handler rewrote on the message instead of the original one. Setting <paramref name="cloneRequest"/>
        /// trades those extra allocations for a clean request state on every attempt.
        /// </para>
        /// <para>
        /// Either way the caller's <see cref="HttpContent"/> instance is reused instead of being copied, so the body must be
        /// replayable: a <see cref="StreamContent"/> consumes its source stream during the first send, so it can only be
        /// retried when <paramref name="bufferRequestContent"/> materializes it in memory beforehand.
        /// </para>
        /// </remarks>
        public IHttpClientBuilder AddSimpleRetry(Action<RetryPolicyOptions>? configure, bool bufferRequestContent = false, bool cloneRequest = false)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            return builder.AddSimpleRetry((_, options) => configure(options), bufferRequestContent, cloneRequest);
        }

        /// <summary>
        /// Adds a standard SimpleRetry delegating handler that retries transient HTTP failures using configuration that can resolve services from the provider.
        /// </summary>
        /// <param name="configure">The callback used to configure the retry policy.</param>
        /// <param name="bufferRequestContent">
        /// <see langword="true"/> to buffer non-replayable request bodies in memory so that they can be sent again on every
        /// attempt; <see langword="false"/> to reject such requests up front.
        /// </param>
        /// <param name="cloneRequest">
        /// <see langword="true"/> to send a fresh copy of the request message on every attempt, so that mutations applied
        /// by the inner handlers (added headers, rewritten URIs) never leak into the following attempts;
        /// <see langword="false"/> to send the very same <see cref="HttpRequestMessage"/> instance every time.
        /// </param>
        /// <returns>The HTTP client builder for chaining additional registrations.</returns>
        /// <remarks>
        /// The retry policy is registered as a keyed service using the HTTP client name, so the very same
        /// <see cref="IRetryExecutor"/> that runs standalone operations also drives the HTTP pipeline.
        /// <para>
        /// By default the handler resends the original request message through a plain <c>base.SendAsync(request, …)</c>,
        /// exactly like the standard <c>Microsoft.Extensions.Http.Resilience</c> handler: no message is allocated per
        /// attempt. The cost is that every mutation applied by the inner handlers
        /// accumulates across attempts: headers can end up duplicated or overwritten, and a retry follows the URI that a
        /// redirect handler rewrote on the message instead of the original one. Setting <paramref name="cloneRequest"/>
        /// trades those extra allocations for a clean request state on every attempt.
        /// </para>
        /// <para>
        /// Either way the caller's <see cref="HttpContent"/> instance is reused instead of being copied, so the body must be
        /// replayable: a <see cref="StreamContent"/> consumes its source stream during the first send, so it can only be
        /// retried when <paramref name="bufferRequestContent"/> materializes it in memory beforehand.
        /// </para>
        /// </remarks>
        public IHttpClientBuilder AddSimpleRetry(Action<IServiceProvider, RetryPolicyOptions>? configure = null, bool bufferRequestContent = false, bool cloneRequest = false)
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
                var executor = services.GetRequiredKeyedService<IRetryExecutor>(builder.Name);
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
