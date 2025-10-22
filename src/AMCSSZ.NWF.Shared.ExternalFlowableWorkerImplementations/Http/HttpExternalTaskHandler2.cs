using AMCSSZ.NWF.Shared.ExternalFlowableWorker;
using AMCSSZ.NWF.Shared.ExternalFlowableWorkerImplementations.Helpers;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AMCSSZ.NWF.Shared.ExternalFlowableWorkerImplementations.Http;

public sealed class HttpExternalTaskHandler2 : IFlowableJobHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpExternalTaskHandler2> _logger;
    private readonly string _workerId;
    private readonly string _targetUrl;

    public HttpExternalTaskHandler2(
        string workerId,
        HttpTaskEndpointOptions httpOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<HttpExternalTaskHandler2> logger)
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

        context.Variables.TryGetValue("JsonPayload", out var rawPayload);

        _logger.LogInformation($"Processing job {context.Job.Id} worker={_workerId} with payload={rawPayload}");

        _logger.LogInformation(
            "Job variables: {Variables}",
            JsonSerializer.Serialize(context.Variables, HttpResponseContentInspector.DefaultSerializerOptions));

        var value = string.Empty;

        if (JsonContentNavigator.TryNavigatePath(
            rawPayload,
            out var jsonElementByPath,
            _logger,
            HttpResponseContentInspector.DefaultSerializerOptions,
            5,
            "payload", "inputPayload", "data"))
        {
            value = JsonSerializer.Serialize(jsonElementByPath, HttpResponseContentInspector.DefaultSerializerOptions);
            _logger.LogInformation("jsonElementByPath: {Payload}", value);
        }
        else
        {
            _logger.LogWarning("ByPath not found: ...");
        }

        var payload = new HttpExternalTaskRequestPayload(
            _workerId,
            DateTimeOffset.UtcNow.ToString("o"),
            new
            {
                context.Job.Id,
                context.Job.ProcessInstanceId,
                context.Job.ExecutionId,
                //context.Variables,
                value
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

        try
        {
            using var response = await httpClient.PostAsync(_targetUrl, content, cancellationToken);
            stopwatch.Stop();

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var elementIdentifier = context.Job.ElementId;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var trimmed = responseBody.Trim();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "External call failed status={Status} elapsedMs={Elapsed} response={Response}",
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    responseBody);

                if ((int)response.StatusCode >= 500)
                {
                    // Dočasná chyba (např. HTTP 502/504) -> necháme Flowable úlohu retrynout.
                    throw new FlowableJobRetryException(
                        $"Call to {_targetUrl} failed with {(int)response.StatusCode}");
                }

                FlowableFinalFailureAction? finalAction = null;

                if (response.StatusCode == HttpStatusCode.UnprocessableEntity
                    && HttpResponseContentInspector.TryParseJson(responseBody, out var errorJson)
                    && errorJson.ValueKind == JsonValueKind.Object
                    && errorJson.TryGetProperty("businessErrorCode", out var businessCodeElement)
                    && businessCodeElement.ValueKind == JsonValueKind.String)
                {
                    var businessErrorCode = businessCodeElement.GetString() ?? "BUSINESS_ERROR";
                    var businessErrorMessage = errorJson.TryGetProperty("businessErrorMessage", out var businessMessageElement)
                        && businessMessageElement.ValueKind == JsonValueKind.String
                            ? businessMessageElement.GetString()
                            : "Business validation failed.";

                    finalAction = FlowableFinalFailureAction.BpmnError(
                        businessErrorCode,
                        businessErrorMessage,
                        new[]
                        {
                            new FlowableVariable("businessErrorPayload", errorJson, "json")
                        });

                    // Validovaná business chyba -> BPMN error, aby model přešel na boundary event.
                    throw new FlowableJobFinalException(
                        finalAction,
                        $"Business validation failed with code '{businessErrorCode}'.");
                }

                var incidentVariables = new[]
                {
                    new FlowableVariable("httpStatus", (int)response.StatusCode, "integer"),
                    new FlowableVariable("httpResponse", responseBody, "string"),
                };

                finalAction = new FlowableFinalFailureAction(
                    FlowableFinalFailureActionType.Incident,
                    $"HTTP call failed with status {(int)response.StatusCode}",
                    incidentVariables);

                // Neopravitelná technická chyba (např. 401/403) -> incident ukončí retry smyčku.
                throw new FlowableJobFinalException(
                    finalAction,
                    $"Call to {_targetUrl} failed with {(int)response.StatusCode}");
            }

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
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout HTTP klienta -> dočasná chyba, dáme Flowable šanci úlohu zkusit znovu.
            throw new FlowableJobRetryException(
                $"Call to {_targetUrl} timed out after {stopwatch.ElapsedMilliseconds} ms.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            // Síťové výpadky (např. DNS, connection reset) bereme jako dočasné chyby vhodné k retry.
            throw new FlowableJobRetryException(
                $"Call to {_targetUrl} failed: {ex.Message}",
                ex);
        }

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
