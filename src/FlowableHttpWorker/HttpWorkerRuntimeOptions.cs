namespace FlowableHttpWorker;

public sealed class HttpWorkerRuntimeOptions
{
    public HttpWorkerRuntimeOptions(string workerId, string targetUrl, string httpClientName)
    {
        WorkerId = string.IsNullOrWhiteSpace(workerId)
            ? throw new ArgumentException("WorkerId must be provided", nameof(workerId))
            : workerId;
        TargetUrl = string.IsNullOrWhiteSpace(targetUrl)
            ? throw new ArgumentException("TargetUrl must be provided", nameof(targetUrl))
            : targetUrl;
        HttpClientName = string.IsNullOrWhiteSpace(httpClientName)
            ? throw new ArgumentException("HttpClientName must be provided", nameof(httpClientName))
            : httpClientName;
    }

    public string WorkerId { get; }

    public string TargetUrl { get; }

    public string HttpClientName { get; }
}
