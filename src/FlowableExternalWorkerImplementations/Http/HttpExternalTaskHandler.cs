using Flowable.ExternalWorker;
using Flowable.ExternalWorkerImplementations.Helpers;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flowable.ExternalWorkerImplementations.Http;

public sealed class HttpExternalTaskHandler : IFlowableJobHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpExternalTaskHandler> _logger;
    private readonly string _workerId;
    private readonly string _targetUrl;

    public HttpExternalTaskHandler(
        string workerId,
        HttpTaskEndpointOptions httpOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<HttpExternalTaskHandler> logger)
    {
        _workerId = string.IsNullOrWhiteSpace(workerId)
            ? throw new ArgumentException("WorkerId must be provided", nameof(workerId))
            : workerId;
        ArgumentNullException.ThrowIfNull(httpOptions);
        _targetUrl = string.IsNullOrWhiteSpace(httpOptions.TargetUrl)
            ? throw new ArgumentException("TargetUrl must be provided", nameof(httpOptions))
            : httpOptions.TargetUrl;
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal sealed record HttpExternalTaskRequestPayload(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("clientTs")] string ClientTimestamp,
        [property: JsonPropertyName("data")] object? Data);

    public async Task<FlowableJobHandlerResult> HandleAsync(FlowableJobContext context, CancellationToken cancellationToken)
    {

        var httpClient = _httpClientFactory.CreateClient();

        context.Variables.TryGetValue("JsonPayload", out var inputPayload);

        _logger.LogInformation($"Processing job {context.Job.Id} worker={_workerId} with payload={inputPayload}");

        _logger.LogInformation(
            "Job variables: {Variables}",
            JsonSerializer.Serialize(context.Variables, HttpResponseContentInspector.DefaultSerializerOptions));

        var payload = new HttpExternalTaskRequestPayload(
            _workerId,
            DateTimeOffset.UtcNow.ToString("o"),
            new
            {
                context.Job.Id,
                context.Job.ProcessInstanceId,
                context.Job.ExecutionId,
                //context.Variables,
                inputPayload
            }
            );

        _logger.LogDebug(
            "Payload: {Payload}",
            JsonSerializer.Serialize(payload, HttpResponseContentInspector.DefaultSerializerOptions));

        using var content = new StringContent(
            JsonSerializer.Serialize(payload, HttpResponseContentInspector.DefaultSerializerOptions),
            Encoding.UTF8,
            "application/json");

        _logger.LogInformation("Calling external service {Url}", _targetUrl);
        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClient.PostAsync(_targetUrl, content, cancellationToken);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "External call failed status={Status} elapsedMs={Elapsed}",
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds);
            throw new FlowableJobRetryException(
                $"Call to {_targetUrl} failed with {(int)response.StatusCode}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var elementIdentifier = context.Job.ElementId;
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var trimmed = responseBody.Trim();

        var responseType = "string";
        object responseValue = responseBody;

        if (!string.IsNullOrEmpty(responseBody))
        {
            if (HttpResponseContentInspector.IsJsonPayload(contentType, trimmed)
                && HttpResponseContentInspector.TryParseJson(responseBody, out var jsonElement))
            {
                responseType = "json";
                responseValue = jsonElement;
            }
            else if (HttpResponseContentInspector.IsXmlPayload(contentType, trimmed))
            {
                responseType = "xml";
            }
        }

        var headers = HttpResponseContentInspector.ExtractResponseHeaders(response);

        var variables = new List<FlowableVariable>
        {
            new($"{elementIdentifier}_statusCode", (int)response.StatusCode, "integer"),
            new($"{elementIdentifier}_response_type", responseType, "string"),
            new($"{elementIdentifier}_response", responseValue, responseType == "json" ? "json" : "string"),
            new($"{elementIdentifier}_headers", headers, "json"),
            new("JsonResponsePayload", responseValue, responseType == "json" ? "json" : "string"),
        };

        _logger.LogInformation(
            "JsonResponsePayload: {JsonResponsePayload}",
            JsonSerializer.Serialize(responseValue, HttpResponseContentInspector.DefaultSerializerOptions));

        _logger.LogInformation(
            "External call succeeded status={Status} elapsedMs={Elapsed} worker={WorkerId}",
            (int)response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            _workerId);

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
