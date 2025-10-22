using System;
using System.Collections.Generic;
using System.Linq;

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

    public Dictionary<DayOfWeek, FlowableWorkerDailySchedule> DailySchedules { get; set; } = new();
}

public sealed class FlowableWorkerDailySchedule
{
    public bool Enabled { get; set; } = true;

    public List<FlowableWorkerDailyWindow> ActiveWindows { get; set; } = new();

    internal bool IsActiveAt(TimeSpan time)
    {
        if (!Enabled)
        {
            return false;
        }

        if (ActiveWindows is null || ActiveWindows.Count == 0)
        {
            return true;
        }

        var hasValidWindow = false;

        foreach (var window in ActiveWindows)
        {
            if (window is null || !window.IsValid())
            {
                continue;
            }

            hasValidWindow = true;

            if (window.Contains(time))
            {
                return true;
            }
        }

        return !hasValidWindow;
    }
}

public sealed class FlowableWorkerDailyWindow
{
    private static readonly TimeSpan EndOfDay = TimeSpan.FromHours(24);

    public TimeSpan Start { get; set; } = TimeSpan.Zero;

    public TimeSpan End { get; set; } = EndOfDay;

    internal bool Contains(TimeSpan time)
    {
        if (!IsValid())
        {
            return false;
        }

        return time >= Start && time < End;
    }

    internal bool IsValid()
    {
        return Start >= TimeSpan.Zero
            && Start < EndOfDay
            && End > Start
            && End <= EndOfDay;
    }
}

public sealed class RetryOptions
{
    public int InitialDelaySeconds { get; set; } = 60;

    public int MaxDelaySeconds { get; set; } = 900;

    public int JitterSeconds { get; set; } = 5;

    public double BackoffMultiplier { get; set; } = 2.0;
}
