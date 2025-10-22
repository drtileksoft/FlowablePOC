namespace AMCSSZ.NWF.Shared.ExternalFlowableWorker;

public sealed class FlowableWorkerOptions
{
    public string Topic { get; set; } = string.Empty;

    public string WorkerId { get; set; } = string.Empty;

    public string LockDuration { get; set; } = "PT30S";

    public int MaxJobsPerTick { get; set; } = 5;

    public int PollPeriodSeconds { get; set; } = 3;

    public int MaxDegreeOfParallelism { get; set; } = 2;

    public int InitialRetries { get; set; } = 3;

    public FlowableWorkerTimeWindowOptions TimeWindow { get; set; } = new();

    public RetryOptions Retry { get; set; } = new();
}

public sealed class FlowableWorkerTimeWindowOptions
{
    public string TimeZoneId { get; set; } = "Europe/Prague";

    public int? PauseFromHour { get; set; }
        = null;

    public int? PauseToHourExclusive { get; set; }
        = null;
}

public sealed class RetryOptions
{
    public int InitialDelaySeconds { get; set; } = 60;

    public int MaxDelaySeconds { get; set; } = 900;

    public int JitterSeconds { get; set; } = 5;

    public double BackoffMultiplier { get; set; } = 2.0;
}
