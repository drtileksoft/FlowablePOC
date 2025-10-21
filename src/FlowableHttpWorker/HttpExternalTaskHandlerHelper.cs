using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flowable.ExternalWorker;
using Microsoft.Extensions.Logging;

namespace FlowableHttpWorker;

internal static class HttpExternalTaskHandlerHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<FlowableJobHandlerResult> HandleAsync(
        FlowableJobContext context,
        HttpWorkerRuntimeOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        Func<FlowableJobContext, object?>? forwardedResultFactory,
        CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient(options.HttpClientName);
        var payload = CreatePayload(context, options.WorkerId, forwardedResultFactory?.Invoke(context));

        using var content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        logger.LogInformation("Calling external service {Url}", options.TargetUrl);
        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClient.PostAsync(options.TargetUrl, content, cancellationToken);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "External call failed status={Status} elapsedMs={Elapsed}",
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds);
            throw new FlowableJobRetryException(
                $"Call to {options.TargetUrl} failed with {(int)response.StatusCode}");
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

        logger.LogInformation(
            "External call succeeded status={Status} elapsedMs={Elapsed}",
            (int)response.StatusCode,
            stopwatch.ElapsedMilliseconds);

        return new FlowableJobHandlerResult(variables);
    }

    private static HttpExternalTaskPayload CreatePayload(
        FlowableJobContext context,
        string workerId,
        object? forwardedResult)
        => new(
            workerId,
            DateTimeOffset.UtcNow.ToString("o"),
            new HttpExternalTaskPayloadData(
                "external-worker",
                context.Job.Id,
                context.Job.ProcessInstanceId,
                context.Job.ExecutionId,
                context.Variables,
                forwardedResult));

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

internal sealed record HttpExternalTaskPayload(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("clientTs")] string ClientTs,
    [property: JsonPropertyName("data")] HttpExternalTaskPayloadData Data);

internal sealed record HttpExternalTaskPayloadData(
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("pi")] string ProcessInstanceId,
    [property: JsonPropertyName("exec")] string ExecutionId,
    [property: JsonPropertyName("variables")] IReadOnlyDictionary<string, object?> Variables,
    [property: JsonPropertyName("forwardedResult")] object? ForwardedResult);
