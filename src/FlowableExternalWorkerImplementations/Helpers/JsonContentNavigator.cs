using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AMCSSZ.NWF.Shared.ExternalFlowableWorkerImplementations.Helpers;

public static class JsonContentNavigator
{
    public static readonly JsonSerializerOptions DefaultSerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false
    };

    public static bool TryParseElement(string? json, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryDecodeBase64ToString(string? value, out string? decoded)
    {
        decoded = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if ((value!.Length & 3) != 0)
        {
            return false;
        }

        try
        {
            var buffer = Convert.FromBase64String(value);
            decoded = Encoding.UTF8.GetString(buffer);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static JsonElement UnwrapEmbeddedJson(JsonElement element, int maxDepth = 8)
    {
        var current = element;
        for (var i = 0; i < maxDepth; i++)
        {
            if (current.ValueKind != JsonValueKind.String)
            {
                break;
            }

            var stringValue = current.GetString();

            if (TryDecodeBase64ToString(stringValue, out var decodedFromBase64)
                && TryParseElement(decodedFromBase64, out var base64Element))
            {
                current = base64Element;
                continue;
            }

            if (TryParseElement(stringValue, out var parsedElement))
            {
                current = parsedElement;
                continue;
            }

            break;
        }

        return current;
    }

    public static bool TryConvertToElement(
        object? value,
        out JsonElement element,
        ILogger? logger = null,
        JsonSerializerOptions? serializerOptions = null,
        int unwrapDepth = 8)
    {
        element = default;
        serializerOptions ??= DefaultSerializerOptions;

        switch (value)
        {
            case null:
                logger?.LogWarning("Value is null.");
                return false;

            case JsonElement jsonElement:
                element = UnwrapEmbeddedJson(jsonElement, unwrapDepth);
                return true;

            case string stringValue:
                if (TryParseElement(stringValue, out element))
                {
                    element = UnwrapEmbeddedJson(element, unwrapDepth);
                    return true;
                }

                if (TryDecodeBase64ToString(stringValue, out var decoded) && TryParseElement(decoded, out element))
                {
                    element = UnwrapEmbeddedJson(element, unwrapDepth);
                    return true;
                }

                logger?.LogWarning("String value is not a valid JSON.");
                return false;

            case byte[] bytes:
                try
                {
                    var textValue = Encoding.UTF8.GetString(bytes);
                    if (!TryParseElement(textValue, out element))
                    {
                        logger?.LogWarning("byte[] value is not a valid UTF8 JSON.");
                        return false;
                    }

                    element = UnwrapEmbeddedJson(element, unwrapDepth);
                    return true;
                }
                catch (Exception exception)
                {
                    logger?.LogWarning(exception, "Failed to read byte[] as UTF8 JSON.");
                    return false;
                }

            case IDictionary dictionary:
                try
                {
                    var json = JsonSerializer.Serialize(dictionary, serializerOptions);
                    using var document = JsonDocument.Parse(json);
                    element = document.RootElement.Clone();
                    return true;
                }
                catch (Exception exception)
                {
                    logger?.LogWarning(exception, "Failed to serialize IDictionary to JSON.");
                    return false;
                }

            default:
                var text = value.ToString();
                if (!TryParseElement(text, out element))
                {
                    logger?.LogWarning(
                        "Unsupported value type {Type} and ToString() is not valid JSON.",
                        value.GetType().FullName);
                    return false;
                }

                element = UnwrapEmbeddedJson(element, unwrapDepth);
                return true;
        }
    }

    public static bool TryFindPropertyRecursive(
        object? value,
        string propertyName,
        out JsonElement property,
        ILogger? logger = null,
        bool caseInsensitive = false,
        int maxDepth = 32,
        JsonSerializerOptions? serializerOptions = null)
    {
        property = default;
        if (!TryConvertToElement(value, out var root, logger, serializerOptions))
        {
            return false;
        }

        var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var queue = new Queue<(JsonElement Node, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            if (depth > maxDepth)
            {
                continue;
            }

            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var propertyNode in node.EnumerateObject())
                    {
                        if (propertyNode.Name.Equals(propertyName, comparison))
                        {
                            property = UnwrapEmbeddedJson(propertyNode.Value).Clone();
                            return true;
                        }

                        queue.Enqueue((UnwrapEmbeddedJson(propertyNode.Value), depth + 1));
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray())
                    {
                        queue.Enqueue((UnwrapEmbeddedJson(item), depth + 1));
                    }

                    break;

                case JsonValueKind.String:
                    var expanded = UnwrapEmbeddedJson(node);
                    if (expanded.ValueKind != JsonValueKind.String)
                    {
                        queue.Enqueue((expanded, depth + 1));
                    }

                    break;
            }
        }

        return false;
    }

    public static bool TryNavigatePath(
        object? value,
        out JsonElement result,
        ILogger? logger = null,
        JsonSerializerOptions? serializerOptions = null,
        int maxDepthPerHop = 8,
        params string[] path)
    {
        result = default;
        if (path is null || path.Length == 0)
        {
            return TryConvertToElement(value, out result, logger, serializerOptions);
        }

        if (!TryConvertToElement(value, out var current, logger, serializerOptions, maxDepthPerHop))
        {
            return false;
        }

        foreach (var segment in path)
        {
            var moved = false;
            current = UnwrapEmbeddedJson(current, maxDepthPerHop);

            if (current.ValueKind == JsonValueKind.Object)
            {
                if (current.TryGetProperty(segment, out var next))
                {
                    current = UnwrapEmbeddedJson(next, maxDepthPerHop);
                    moved = true;
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray())
                {
                    var candidate = UnwrapEmbeddedJson(item, maxDepthPerHop);
                    if (candidate.ValueKind == JsonValueKind.Object && candidate.TryGetProperty(segment, out var next))
                    {
                        current = UnwrapEmbeddedJson(next, maxDepthPerHop);
                        moved = true;
                        break;
                    }
                }
            }
            else if (current.ValueKind == JsonValueKind.String)
            {
                var expanded = UnwrapEmbeddedJson(current, maxDepthPerHop);
                if (expanded.ValueKind == JsonValueKind.Object && expanded.TryGetProperty(segment, out var next))
                {
                    current = UnwrapEmbeddedJson(next, maxDepthPerHop);
                    moved = true;
                }
                else
                {
                    current = expanded;
                }
            }

            if (!moved)
            {
                return false;
            }
        }

        result = current.Clone();
        return true;
    }
}
