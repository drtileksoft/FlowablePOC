using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flowable.ExternalWorker;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFlowableClient(
        this IServiceCollection services,
        FlowableClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new ArgumentException("BaseUrl must be provided", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.User))
        {
            throw new ArgumentException("User must be provided", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Pass))
        {
            throw new ArgumentException("Pass must be provided", nameof(options));
        }

        services.AddSingleton(options);

        services
            .AddHttpClient(options.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.HttpTimeoutSeconds));
            })
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.User}:{options.Pass}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                if (!options.AllowInsecureSsl)
                {
                    return new HttpClientHandler();
                }

                return new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                };
            });

        return services;
    }

    public static IServiceCollection AddFlowableExternalWorker<THandler>(
        this IServiceCollection services,
        FlowableWorkerOptions options,
        Func<IServiceProvider, THandler> handlerFactory)
        where THandler : class, IFlowableJobHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handlerFactory);

        services.AddSingleton<IHostedService>(sp =>
        {
            var handler = handlerFactory(sp);
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var logger = sp.GetRequiredService<ILogger<FlowableExternalWorkerService<THandler>>>();
            return new FlowableExternalWorkerService<THandler>(httpFactory, logger, options, handler);
        });

        return services;
    }
}
