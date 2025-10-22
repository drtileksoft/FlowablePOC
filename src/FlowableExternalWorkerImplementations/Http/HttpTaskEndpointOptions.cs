namespace AMCSSZ.NWF.Shared.ExternalFlowableWorkerImplementations.Http;

public sealed class HttpTaskEndpointOptions
{
    public string TargetUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;

}
