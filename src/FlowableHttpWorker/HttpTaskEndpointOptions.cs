namespace FlowableHttpWorker;

public sealed class HttpTaskEndpointOptions
{
    public string TargetUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;

    public string? HttpClientName { get; set; }
        = null;
}
