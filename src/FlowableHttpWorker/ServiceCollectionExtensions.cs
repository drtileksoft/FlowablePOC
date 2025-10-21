using System;
using System.Linq;
using Flowable.ExternalWorker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlowableHttpWorker;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFlowableHttpWorkers(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddFlowableClient(configuration);

        var retrySection = configuration.GetSection("Flowable").GetSection("Retry");
        var retry = retrySection.Get<RetryOptions>() ?? new RetryOptions();

        var workersSection = configuration.GetSection("FlowableWorkers");
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
            RegisterHttpWorker(services, workerSection.Key, workerSection.Get<HttpWorkerConfiguration>(), retry);
        }

        return services;
    }

    private static void RegisterHttpWorker(
        IServiceCollection services,
        string? handlerName,
        HttpWorkerConfiguration? definition,
        RetryOptions retry)
    {
        if (string.IsNullOrWhiteSpace(handlerName))
        {
            throw new InvalidOperationException("Worker handler name must be provided in configuration.");
        }

        definition ??= new HttpWorkerConfiguration();

        switch (handlerName)
        {
            case nameof(HttpExternalTaskHandler):
                RegisterHttpWorker<HttpExternalTaskHandler>(services, definition, retry);
                break;
            case nameof(HttpExternalTaskHandler2):
                RegisterHttpWorker<HttpExternalTaskHandler2>(services, definition, retry);
                break;
            default:
                throw new InvalidOperationException($"Unknown worker handler configured: {handlerName}.");
        }
    }

    private static void RegisterHttpWorker<THandler>(
        IServiceCollection services,
        HttpWorkerConfiguration definition,
        RetryOptions retry)
        where THandler : class, IFlowableJobHandler
    {
        var topic = definition.Topic ?? throw new InvalidOperationException("Worker topic must be configured.");
        var workerId = definition.WorkerId ?? throw new InvalidOperationException("WorkerId must be configured.");

        var queue = definition.Queue ?? new HttpQueueOptions();
        var flowableOptions = new FlowableWorkerOptions
        {
            Topic = topic,
            WorkerId = workerId,
            LockDuration = queue.LockDuration ?? "PT30S",
            MaxJobsPerTick = queue.MaxJobsPerTick ?? 5,
            PollPeriodSeconds = queue.PollPeriodSeconds ?? 3,
            MaxDegreeOfParallelism = queue.MaxDegreeOfParallelism ?? 2,
            InitialRetries = queue.InitialRetries ?? 3,
            FlowableHttpClientName = FlowableWorkerOptions.DefaultFlowableHttpClientName,
            TimeWindow = definition.Window ?? new FlowableWorkerTimeWindowOptions(),
            Retry = new RetryOptions
            {
                InitialDelaySeconds = retry.InitialDelaySeconds,
                MaxDelaySeconds = retry.MaxDelaySeconds,
                JitterSeconds = retry.JitterSeconds,
                BackoffMultiplier = retry.BackoffMultiplier
            }
        };

        var endpoint = definition.Endpoint ?? throw new InvalidOperationException("Worker endpoint must be configured.");
        var httpClientName = string.IsNullOrWhiteSpace(endpoint.HttpClientName)
            ? $"srd-{workerId}"
            : endpoint.HttpClientName;
        var timeout = Math.Max(1, endpoint.TimeoutSeconds ?? 10);
        services.AddHttpClient(httpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(timeout);
        });

        var runtimeOptions = new HttpWorkerRuntimeOptions(
            workerId,
            endpoint.Url ?? throw new InvalidOperationException("Endpoint URL must be configured."),
            httpClientName);

        services.AddFlowableExternalWorker<THandler>(
            flowableOptions,
            sp => ActivatorUtilities.CreateInstance<THandler>(sp, runtimeOptions));
    }
}
