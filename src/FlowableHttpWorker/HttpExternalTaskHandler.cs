using Flowable.ExternalWorker;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

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

    public async Task<FlowableJobHandlerResult> HandleAsync(FlowableJobContext context, CancellationToken cancellationToken) {

        var httpClient = _httpClientFactory.CreateClient(_options.HttpClientName);

        var input = context.Variables.TryGetValue("JsonPayload", out var forwarded) ? forwarded : null;

        var payload = HttpExternalTaskHandlerHelper.CreatePayload(context, _options.WorkerId, input);

        using var content = new StringContent(
            JsonSerializer.Serialize(payload, HttpExternalTaskHandlerHelper.JsonOptions),
            Encoding.UTF8,
            "application/json");

        _logger.LogInformation("Calling external service {Url}", _options.TargetUrl);
        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClient.PostAsync(_options.TargetUrl, content, cancellationToken);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "External call failed status={Status} elapsedMs={Elapsed}",
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds);
            throw new FlowableJobRetryException(
                $"Call to {_options.TargetUrl} failed with {(int)response.StatusCode}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var elementIdentifier = context.Job.ElementId;
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var trimmed = responseBody.Trim();

        var responseType = "string";
        object responseValue = responseBody;

        if (!string.IsNullOrEmpty(responseBody))
        {
            if (HttpExternalTaskHandlerHelper.IsJson(contentType, trimmed) && HttpExternalTaskHandlerHelper.TryParseJson(responseBody, out var jsonElement))
            {
                responseType = "json";
                responseValue = jsonElement;
            }
            else if (HttpExternalTaskHandlerHelper.IsXml(contentType, trimmed))
            {
                responseType = "xml";
            }
        }

        var headersDict = response.Headers
            .SelectMany(h => h.Value.Select(v => (h.Key, v)))
            .Concat(response.Content.Headers.SelectMany(h => h.Value.Select(v => (h.Key, v))))
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(t => t.v).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var variables = new List<FlowableVariable>
        {
            new($"{elementIdentifier}_statusCode", (int)response.StatusCode, "integer"),
            new($"{elementIdentifier}_response_type", responseType, "string"),
            new($"{elementIdentifier}_response", responseValue, responseType == "json" ? "json" : "string"),
            new($"{elementIdentifier}_headers", headersDict, "json"),
            new("JsonResponsePayload", responseValue, responseType == "json" ? "json" : "string"),
        };

        _logger.LogInformation(
            "External call succeeded status={Status} elapsedMs={Elapsed}",
            (int)response.StatusCode,
            stopwatch.ElapsedMilliseconds);

        return new FlowableJobHandlerResult(variables);
    }

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
