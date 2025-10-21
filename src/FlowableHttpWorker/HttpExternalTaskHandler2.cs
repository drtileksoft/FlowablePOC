using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flowable.ExternalWorker;
using Microsoft.Extensions.Logging;

namespace FlowableHttpWorker;

public sealed class HttpExternalTaskHandler2 : IFlowableJobHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpExternalTaskHandler2> _logger;
    private readonly HttpWorkerRuntimeOptions _options;

    public HttpExternalTaskHandler2(
        HttpWorkerRuntimeOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<HttpExternalTaskHandler2> logger)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<FlowableJobHandlerResult> HandleAsync(FlowableJobContext context, CancellationToken cancellationToken)
    {
        var srdClient = _httpClientFactory.CreateClient(_options.HttpClientName);
        var workerId = _options.WorkerId;
        var targetUrl = _options.TargetUrl;

        var payload = new
        {
            id = workerId,
            clientTs = DateTimeOffset.UtcNow.ToString("o"),
            data = new
            {
                note = "external-worker",
                jobId = context.Job.Id,
                pi = context.Job.ProcessInstanceId,
                exec = context.Job.ExecutionId,
                variables = context.Variables,
                forwardedResult = context.Variables.TryGetValue("JsonPayload", out var forwarded)
                    ? forwarded
                    : null
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        _logger.LogInformation("Calling external service {Url}", targetUrl);
        var stopwatch = Stopwatch.StartNew();
        using var response = await srdClient.PostAsync(targetUrl, content, cancellationToken);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "External call failed status={Status} elapsedMs={Elapsed}",
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds);
            throw new FlowableJobRetryException(
                $"Call to {targetUrl} failed with {(int)response.StatusCode}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var elementIdentifier = context.Job.ElementId;
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var trimmed = responseBody.Trim();

        var responseType = "string";
        object responseValue = responseBody;

        if (!string.IsNullOrEmpty(responseBody))
        {
            if (IsJson(contentType, trimmed) && TryParseJson(responseBody, out var jsonElement))
            {
                responseType = "json";
                responseValue = jsonElement;
            }
            else if (IsXml(contentType, trimmed))
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

    private static bool IsJson(string? contentType, string trimmed)
        => (!string.IsNullOrEmpty(contentType) && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            || trimmed.StartsWith('{')
            || trimmed.StartsWith('[');

    private static bool IsXml(string? contentType, string trimmed)
        => (!string.IsNullOrEmpty(contentType) && contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            || trimmed.StartsWith('<');

    private static bool TryParseJson(string input, out JsonElement element)
    {
        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(input);
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }
}
