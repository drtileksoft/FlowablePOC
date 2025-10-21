using System.Collections.Generic;
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

        var flowableSection = configuration.GetSection("Flowable");
        if (!flowableSection.Exists())
        {
            throw new InvalidOperationException("Flowable section is required in configuration.");
        }

        var clientSection = flowableSection.GetSection("Client");
        var clientOptions = clientSection.Get<FlowableClientOptions>()
            ?? throw new InvalidOperationException("Flowable:Client section is required in configuration.");
        services.AddFlowableClient(clientOptions);

        var retrySection = flowableSection.GetSection("Retry");
        var retry = retrySection.Get<RetryOptions>() ?? new RetryOptions();

        var workersSection = flowableSection.GetSection("Workers");
        var workerDefinitions = workersSection.Get<List<HttpWorkerConfiguration>>() ?? new List<HttpWorkerConfiguration>();
        if (workerDefinitions.Count == 0)
        {
            throw new InvalidOperationException("No workers configured. Please define Flowable:Workers in configuration.");
        }

        foreach (var definition in workerDefinitions)
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
                FlowableHttpClientName = clientOptions.HttpClientName,
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
            var httpClientName = endpoint.HttpClientName ?? $"srd-{workerId}";
            var timeout = Math.Max(1, endpoint.TimeoutSeconds ?? 10);
            services.AddHttpClient(httpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(timeout);
            });

            var runtimeOptions = new HttpWorkerRuntimeOptions(
                workerId,
                endpoint.Url ?? throw new InvalidOperationException("Endpoint URL must be configured."),
                httpClientName);

            services.AddFlowableExternalWorker<HttpExternalTaskHandler>(
                flowableOptions,
                sp => ActivatorUtilities.CreateInstance<HttpExternalTaskHandler>(sp, runtimeOptions));

            services.AddFlowableExternalWorker<HttpExternalTaskHandler2>(
                flowableOptions,
                sp => ActivatorUtilities.CreateInstance<HttpExternalTaskHandler2>(sp, runtimeOptions));
        }

        return services;
    }
}
