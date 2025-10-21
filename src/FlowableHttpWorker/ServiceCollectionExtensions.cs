using System;
using System.Linq;
using Flowable.ExternalWorker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowableHttpWorker;

public static class ServiceCollectionExtensions
{
    private const string FlowableSectionName = "Flowable";
    private const string WorkersSectionName = "FlowableWorkers";
    private const string FlowableOptionsSectionName = "Flowable";
    private const string HttpOptionsSectionName = "Http";

    public static IServiceCollection AddFlowableHttpWorkers(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddFlowableClient(configuration);

        var retrySection = configuration.GetSection(FlowableSectionName).GetSection("Retry");
        var retry = retrySection.Get<RetryOptions>() ?? new RetryOptions();

        var workersSection = configuration.GetSection(WorkersSectionName);
        if (!workersSection.Exists())
        {
            throw new InvalidOperationException("FlowableWorkers section is required in configuration.");
        }

        var workerSections = workersSection.GetChildren().ToList();
        if (workerSections.Count == 0)
        {
            throw new InvalidOperationException("No workers configured. Please define FlowableWorkers in configuration.");
        }

        foreach (var workerSection in workerSections)
        {
            RegisterHttpWorker(services, workerSection.Key, workerSection, retry);
        }

        return services;
    }

    private static void RegisterHttpWorker(
        IServiceCollection services,
        string? handlerName,
        IConfigurationSection workerSection,
        RetryOptions retry)
    {
        if (string.IsNullOrWhiteSpace(handlerName))
        {
            throw new InvalidOperationException("Worker handler name must be provided in configuration.");
        }

        switch (handlerName)
        {
            case nameof(HttpExternalTaskHandler):
                RegisterHttpWorker<HttpExternalTaskHandler>(services, workerSection, retry);
                break;
            case nameof(HttpExternalTaskHandler2):
                RegisterHttpWorker<HttpExternalTaskHandler2>(services, workerSection, retry);
                break;
            default:
                throw new InvalidOperationException($"Unknown worker handler configured: {handlerName}.");
        }
    }

    private static void RegisterHttpWorker<THandler>(
        IServiceCollection services,
        IConfigurationSection workerSection,
        RetryOptions retry)
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

        flowableOptions.FlowableHttpClientName = string.IsNullOrWhiteSpace(flowableOptions.FlowableHttpClientName)
            ? FlowableWorkerOptions.DefaultFlowableHttpClientName
            : flowableOptions.FlowableHttpClientName;
        flowableOptions.Retry = ResolveRetryOptions(flowableOptionsSection, retry);

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

        httpOptions.HttpClientName = string.IsNullOrWhiteSpace(httpOptions.HttpClientName)
            ? $"srd-{flowableOptions.WorkerId}"
            : httpOptions.HttpClientName;
        var timeout = Math.Max(1, httpOptions.TimeoutSeconds);

        services.AddHttpClient(httpOptions.HttpClientName, client =>
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
        IConfigurationSection flowableOptionsSection,
        RetryOptions fallback)
    {
        var workerRetrySection = flowableOptionsSection.GetSection(nameof(FlowableWorkerOptions.Retry));
        var workerRetry = workerRetrySection.Exists()
            ? workerRetrySection.Get<RetryOptions>() ?? new RetryOptions()
            : fallback;

        return new RetryOptions
        {
            InitialDelaySeconds = workerRetry.InitialDelaySeconds,
            MaxDelaySeconds = workerRetry.MaxDelaySeconds,
            JitterSeconds = workerRetry.JitterSeconds,
            BackoffMultiplier = workerRetry.BackoffMultiplier
        };
    }
}
