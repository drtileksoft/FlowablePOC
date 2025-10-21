namespace Flowable.ExternalWorker;

public sealed class FlowableClientOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    public string User { get; set; } = string.Empty;

    public string Pass { get; set; } = string.Empty;

    public bool AllowInsecureSsl { get; set; }
        = false;

    public int HttpTimeoutSeconds { get; set; } = 30;
}
