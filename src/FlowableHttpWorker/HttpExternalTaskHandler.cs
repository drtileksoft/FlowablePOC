using System.Net.Http;
using Flowable.ExternalWorker;
using Microsoft.Extensions.Logging;

namespace FlowableHttpWorker;

public sealed class HttpExternalTaskHandler : IFlowableJobHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpExternalTaskHandler> _logger;
    private readonly HttpWorkerRuntimeOptions _options;

    public HttpExternalTaskHandler(
        HttpWorkerRuntimeOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<HttpExternalTaskHandler> logger)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<FlowableJobHandlerResult> HandleAsync(FlowableJobContext context, CancellationToken cancellationToken)
        => HttpExternalTaskHandlerHelper.HandleAsync(
            context,
            _options,
            _httpClientFactory,
            _logger,
            null,
            cancellationToken);

    public Task HandleFinalFailureAsync(
        FlowableJobContext context,
        Exception exception,
        FlowableFinalFailureAction action,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Final failure for job {JobId} with action {Action}", context.Job.Id, action.ActionType);
        return Task.CompletedTask;
    }
}
