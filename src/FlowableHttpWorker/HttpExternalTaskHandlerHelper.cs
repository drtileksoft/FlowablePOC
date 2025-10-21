using System.Text.Json;
using System.Text.Json.Serialization;
using Flowable.ExternalWorker;

namespace FlowableHttpWorker;

internal static class HttpExternalTaskHandlerHelper
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static HttpExternalTaskPayload CreatePayload(
        FlowableJobContext context,
        string workerId,
        object? payload)
        => new(
            workerId,
            DateTimeOffset.UtcNow.ToString("o"),
            context.Job.Id,
            context.Job.ProcessInstanceId,
            context.Job.ExecutionId,
            context.Variables,
            payload);

    public static bool IsJson(string? contentType, string trimmed)
        => (!string.IsNullOrEmpty(contentType) && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            || trimmed.StartsWith('{')
            || trimmed.StartsWith('[');

    public static bool IsXml(string? contentType, string trimmed)
        => (!string.IsNullOrEmpty(contentType) && contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            || trimmed.StartsWith('<');

    public static bool TryParseJson(string input, out JsonElement element)
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

    public static Dictionary<string, string[]> GetResponseHeaders(HttpResponseMessage response)
    {
        return response.Headers
                    .SelectMany(h => h.Value.Select(v => (h.Key, v)))
                    .Concat(response.Content.Headers.SelectMany(h => h.Value.Select(v => (h.Key, v))))
                    .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(t => t.v).ToArray(),
                        StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed record HttpExternalTaskPayload(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("clientTs")] string ClientTs,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("pi")] string ProcessInstanceId,
    [property: JsonPropertyName("exec")] string ExecutionId,
    [property: JsonPropertyName("variables")] IReadOnlyDictionary<string, object?> Variables,
    [property: JsonPropertyName("payload")] object? payload);
