using Flowable.ExternalWorker;

namespace FlowableHttpWorker;

public sealed class HttpWorkerConfiguration
{
    public string? Topic { get; set; }
        = null;

    public string? WorkerId { get; set; }
        = null;

    public HttpQueueOptions Queue { get; set; } = new();

    public HttpEndpointOptions Endpoint { get; set; } = new();

    public FlowableWorkerTimeWindowOptions Window { get; set; } = new();
}

public sealed class HttpQueueOptions
{
    public string? LockDuration { get; set; }
        = null;

    public int? MaxJobsPerTick { get; set; }
        = null;

    public int? PollPeriodSeconds { get; set; }
        = null;

    public int? MaxDegreeOfParallelism { get; set; }
        = null;

    public int? InitialRetries { get; set; }
        = null;
}

public sealed class HttpEndpointOptions
{
    public string? Url { get; set; }
        = null;

    public int? TimeoutSeconds { get; set; }
        = null;

    public string? HttpClientName { get; set; }
        = null;
}
