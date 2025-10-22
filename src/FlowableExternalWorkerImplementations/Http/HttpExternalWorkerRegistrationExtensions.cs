using System;
using AMCSSZ.NWF.Shared.ExternalFlowableWorker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AMCSSZ.NWF.Shared.ExternalFlowableWorkerImplementations.Http;

public static class HttpExternalWorkerRegistrationExtensions
{
    private const string FlowableOptionsSectionName = "Flowable";
    private const string HttpOptionsSectionName = "Http";

    public static IServiceCollection AddHttpExternalTaskHandlerWorker(
        this IServiceCollection services,
        IConfiguration configuration)
        => AddHttpWorker<HttpExternalTaskHandler>(services, configuration, nameof(HttpExternalTaskHandler));

    public static IServiceCollection AddHttpExternalTaskHandler2Worker(
        this IServiceCollection services,
        IConfiguration configuration)
        => AddHttpWorker<HttpExternalTaskHandler2>(services, configuration, nameof(HttpExternalTaskHandler2));

    private static IServiceCollection AddHttpWorker<THandler>(
        IServiceCollection services,
        IConfiguration configuration,
        string configurationSectionName)
        where THandler : class, IFlowableJobHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddFlowableClient(configuration);

        var workerSection = configuration.GetSection(configurationSectionName);
        if (!workerSection.Exists())
        {
            throw new InvalidOperationException(
                $"Configuration section '{configurationSectionName}' is required.");
        }

        RegisterHttpWorker<THandler>(services, workerSection);

        return services;
    }

    private static void RegisterHttpWorker<THandler>(
        IServiceCollection services,
        IConfigurationSection workerSection)
        where THandler : class, IFlowableJobHandler
    {
        var flowableOptionsSection = workerSection.GetSection(FlowableOptionsSectionName);
        if (!flowableOptionsSection.Exists())
        {
            throw new InvalidOperationException(
                $"Flowable options must be configured for worker '{workerSection.Key}'.");
        }

        var flowableOptions = flowableOptionsSection.Get<FlowableWorkerOptions>() ?? new FlowableWorkerOptions();
        if (string.IsNullOrWhiteSpace(flowableOptions.Topic))
        {
            throw new InvalidOperationException("Worker topic must be configured.");
        }

        if (string.IsNullOrWhiteSpace(flowableOptions.WorkerId))
        {
            throw new InvalidOperationException("WorkerId must be configured.");
        }

        flowableOptions.Retry = ResolveRetryOptions(flowableOptionsSection);

        var httpOptionsSection = workerSection.GetSection(HttpOptionsSectionName);
        if (!httpOptionsSection.Exists())
        {
            throw new InvalidOperationException(
                $"Http options must be configured for worker '{workerSection.Key}'.");
        }

        var httpOptions = httpOptionsSection.Get<HttpTaskEndpointOptions>() ?? new HttpTaskEndpointOptions();
        if (string.IsNullOrWhiteSpace(httpOptions.TargetUrl))
        {
            throw new InvalidOperationException("Endpoint URL must be configured.");
        }

        var timeout = Math.Max(1, httpOptions.TimeoutSeconds);

        services.AddHttpClient($"httpWorker-{flowableOptions.WorkerId}", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(timeout);
        });

        services.AddFlowableExternalWorker<THandler>(
            flowableOptions,
            sp => ActivatorUtilities.CreateInstance<THandler>(
                sp,
                flowableOptions.WorkerId,
                httpOptions));
    }

    private static RetryOptions ResolveRetryOptions(
        IConfigurationSection flowableOptionsSection)
    {
        var workerRetrySection = flowableOptionsSection.GetSection(nameof(FlowableWorkerOptions.Retry));
        var workerRetry = workerRetrySection.Exists()
            ? workerRetrySection.Get<RetryOptions>() ?? new RetryOptions()
            : new RetryOptions();

        return new RetryOptions
        {
            InitialDelaySeconds = workerRetry.InitialDelaySeconds,
            MaxDelaySeconds = workerRetry.MaxDelaySeconds,
            JitterSeconds = workerRetry.JitterSeconds,
            BackoffMultiplier = workerRetry.BackoffMultiplier
        };
    }
}
