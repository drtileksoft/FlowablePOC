using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flowable.ExternalWorkerImplementations.Helpers;

internal static class HttpResponseContentInspector
{
    public static readonly JsonSerializerOptions DefaultSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool IsJsonPayload(string? contentType, string trimmedContent)
        => (!string.IsNullOrEmpty(contentType) && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            || trimmedContent.StartsWith('{')
            || trimmedContent.StartsWith('[');

    public static bool IsXmlPayload(string? contentType, string trimmedContent)
        => (!string.IsNullOrEmpty(contentType) && contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            || trimmedContent.StartsWith('<');

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

    public static Dictionary<string, string[]> ExtractResponseHeaders(HttpResponseMessage response)
    {
        return response.Headers
            .SelectMany(header => header.Value.Select(value => (header.Key, value)))
            .Concat(response.Content.Headers.SelectMany(header => header.Value.Select(value => (header.Key, value))))
            .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(tuple => tuple.Item2).ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }
}


