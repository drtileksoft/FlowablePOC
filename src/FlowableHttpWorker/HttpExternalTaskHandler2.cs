using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowable.ExternalWorker;
using Microsoft.Extensions.Logging;

namespace FlowableHttpWorker;

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

    /// <summary>
    /// Z jakéhokoliv vstupu (string / JsonElement / JsonNode / cokoliv s JSON v ToString)
    /// udělej JsonNode. Umí „oloupat“ vícenásobně serializovaný JSON string (maxDepth = 3).
    /// </summary>
    private static JsonNode? ParsePossiblyNestedJson(object? input, int maxDepth = 3)
    {
        if (input is null) return null;

        // 1) Získej raw JSON text
        string? jsonText = input switch
        {
            string s => s,
            JsonElement e => e.GetRawText(),
            JsonNode n => n.ToJsonString(),
            _ => input.ToString()
        };

        if (string.IsNullOrWhiteSpace(jsonText))
            return null;

        // 2) Zkus opakovaně parsovat – když je to string uvnitř stringu uvnitř JSONu, postupně loupat
        for (int i = 0; i < maxDepth; i++)
        {
            try
            {
                var node = JsonNode.Parse(jsonText);
                if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var innerString))
                {
                    // JSON je "string s JSONem", oloupej vrstvu a zkus znovu
                    jsonText = innerString;
                    continue;
                }

                // Máme JsonObject/JsonArray → hotovo
                return node;
            }
            catch (JsonException)
            {
                // není validní JSON; skonči
                return null;
            }
        }

        return null;
    }
    
    /// <summary>
    /// Bezpečně se zkusí dostat na cestu vlastností; vrací true/false a out uzel.
    /// Nevyhazuje výjimky, když narazí na ne-objekt.
    /// </summary>
    private static bool TryGetPath(JsonNode? start, out JsonNode? node, params string[] path)
    {
        node = start;
        foreach (var segment in path)
        {
            if (node is null) return false;
            if (node is not JsonObject obj) return false;
            if (!obj.TryGetPropertyValue(segment, out node)) return false;
        }
        return node is not null;
    }

    public async Task<FlowableJobHandlerResult> HandleAsync(FlowableJobContext context, CancellationToken cancellationToken)
    {

        var httpClient = _httpClientFactory.CreateClient();

        context.Variables.TryGetValue("JsonPayload", out var inputPayload);

        _logger.LogInformation($"Processing job {context.Job.Id} worker={_workerId} with payload={inputPayload}");

        _logger.LogInformation($"Job variables: {JsonSerializer.Serialize(context.Variables, HttpExternalTaskHandlerHelper.JsonOptions)}");

        var root = ParsePossiblyNestedJson(inputPayload);
        if (root is null)
        {
            _logger.LogWarning("JsonPayload je prázdný nebo neplatný JSON.");
        }

        TryGetPath(root, out var variablesNode, "payload","inputPayload","data");

        var payload = new HttpExternalTaskPayload(
            _workerId,
            DateTimeOffset.UtcNow.ToString("o"),
            new
            {
                context.Job.Id,
                context.Job.ProcessInstanceId,
                context.Job.ExecutionId,
                //context.Variables,
                variablesNode
            }
            );

        _logger.LogDebug("Payload: {Payload}", JsonSerializer.Serialize(payload, HttpExternalTaskHandlerHelper.JsonOptions));

        using var content = new StringContent(
            JsonSerializer.Serialize(payload, HttpExternalTaskHandlerHelper.JsonOptions),
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

        Dictionary<string, string[]> headersDict = HttpExternalTaskHandlerHelper.GetResponseHeaders(response);

        var variables = new List<FlowableVariable>
        {
            new($"{elementIdentifier}_statusCode", (int)response.StatusCode, "integer"),
            new($"{elementIdentifier}_response_type", responseType, "string"),
            new($"{elementIdentifier}_response", responseValue, responseType == "json" ? "json" : "string"),
            new($"{elementIdentifier}_headers", headersDict, "json"),
            new("JsonResponsePayload", responseValue, responseType == "json" ? "json" : "string"),
        };

        _logger.LogInformation("JsonResponsePayload: {JsonResponsePayload}", JsonSerializer.Serialize(responseValue, HttpExternalTaskHandlerHelper.JsonOptions));

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
