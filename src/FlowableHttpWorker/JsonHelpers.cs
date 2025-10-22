using System;
using System.Buffers.Text;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FlowableHttpWorker
{
    public static class JsonHelpers
    {
        /// <summary>
        /// Default JSON options – můžete si předat vlastní, ale tohle je OK pro log/serialize.
        /// </summary>
        public static readonly JsonSerializerOptions DefaultOptions = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = false
        };

        /// <summary>
        /// Bezpečně naparsuje JSON ze stringu do JsonElement (klonuje ho, takže je nezávislý na JsonDocument).
        /// </summary>
        public static bool TryParseJsonElement(string? json, out JsonElement element)
        {
            element = default;
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                using var doc = JsonDocument.Parse(json);
                element = doc.RootElement.Clone();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Zkusí detekovat base64 a převést na string (někdy knihovny posílají JSON base64-kódovaný).
        /// </summary>
        private static bool TryDecodeBase64ToString(string? s, out string? decoded)
        {
            decoded = null;
            if (string.IsNullOrWhiteSpace(s)) return false;

            // rychlá heuristika – base64 délka musí být násobek 4
            if ((s!.Length & 3) != 0) return false;

            try
            {
                var bytes = Convert.FromBase64String(s);
                decoded = Encoding.UTF8.GetString(bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Pokud je element typu String a ten string je JSON (nebo base64 JSON), rozbalí ho.
        /// Opakuje až do vyčerpání hloubky (vícenásobné „double-encoded“ řetězce).
        /// </summary>
        public static JsonElement UnwrapIfJsonString(JsonElement el, int maxDepth = 8)
        {
            var current = el;
            for (int i = 0; i < maxDepth; i++)
            {
                if (current.ValueKind != JsonValueKind.String)
                    break;

                var s = current.GetString();

                // 1) zkus base64 -> string
                if (TryDecodeBase64ToString(s, out var decoded) && TryParseJsonElement(decoded, out var fromB64))
                {
                    current = fromB64;
                    continue;
                }

                // 2) přímo string -> JSON
                if (TryParseJsonElement(s, out var inner))
                {
                    current = inner;
                    continue;
                }

                // 3) nic – dál už to řetězec s JSONem nebude
                break;
            }

            return current;
        }

        /// <summary>
        /// Z libovolné hodnoty (string/JsonElement/byte[]/IDictionary/ToString JSON) udělá JsonElement.
        /// Zároveň rozbalí stringy obsahující JSON (vícekrát).
        /// </summary>
        public static bool TryCoerceToElement(
            object? value,
            out JsonElement root,
            ILogger? logger = null,
            JsonSerializerOptions? options = null,
            int unwrapDepth = 8)
        {
            root = default;
            options ??= DefaultOptions;

            switch (value)
            {
                case null:
                    logger?.LogWarning("Value is null.");
                    return false;

                case JsonElement el:
                    root = UnwrapIfJsonString(el, unwrapDepth);
                    return true;

                case string s:
                    if (TryParseJsonElement(s, out root))
                    {
                        root = UnwrapIfJsonString(root, unwrapDepth);
                        return true;
                    }
                    // base64?
                    if (TryDecodeBase64ToString(s, out var decoded) && TryParseJsonElement(decoded, out root))
                    {
                        root = UnwrapIfJsonString(root, unwrapDepth);
                        return true;
                    }
                    logger?.LogWarning("String value is not a valid JSON.");
                    return false;

                case byte[] bytes:
                    try
                    {
                        var txtVal = Encoding.UTF8.GetString(bytes);
                        if (!TryParseJsonElement(txtVal, out root))
                        {
                            logger?.LogWarning("byte[] value is not a valid UTF8 JSON.");
                            return false;
                        }
                        root = UnwrapIfJsonString(root, unwrapDepth);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to read byte[] as UTF8 JSON.");
                        return false;
                    }

                case IDictionary dict:
                    try
                    {
                        // nejjednodušší je to serializovat – bývá to stejně mix JsonElementů
                        var json = JsonSerializer.Serialize(dict, options);
                        using var doc = JsonDocument.Parse(json);
                        root = doc.RootElement.Clone();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to serialize IDictionary to JSON.");
                        return false;
                    }

                default:
                    // fallback – zkus ToString() jako JSON
                    var txt = value.ToString();
                    if (!TryParseJsonElement(txt, out root))
                    {
                        logger?.LogWarning("Unsupported value type {Type} and ToString() is not valid JSON.", value.GetType().FullName);
                        return false;
                    }
                    root = UnwrapIfJsonString(root, unwrapDepth);
                    return true;
            }
        }

        /// <summary>
        /// Hluboké vyhledání první property s daným názvem (case-sensitive/insensitive) v celém JSONu.
        /// Prochází objekty, pole a stringy, které obsahují JSON (rekurzivně).
        /// </summary>
        public static bool TryFindPropertyDeep(
            object? value,
            string propertyName,
            out JsonElement found,
            ILogger? logger = null,
            bool caseInsensitive = false,
            int maxDepth = 32,
            JsonSerializerOptions? options = null)
        {
            found = default;
            if (!TryCoerceToElement(value, out var root, logger, options))
                return false;

            var cmp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            var q = new Queue<(JsonElement el, int depth)>();
            q.Enqueue((root, 0));

            while (q.Count > 0)
            {
                var (node, depth) = q.Dequeue();
                if (depth > maxDepth) continue;

                switch (node.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var prop in node.EnumerateObject())
                        {
                            if (prop.Name.Equals(propertyName, cmp))
                            {
                                found = prop.Value.Clone();
                                // kdyby to byl string s JSONem, ještě rozbalíme
                                found = UnwrapIfJsonString(found);
                                return true;
                            }

                            // Enqueue child – s rozbalením string-JSONu
                            q.Enqueue((UnwrapIfJsonString(prop.Value), depth + 1));
                        }
                        break;

                    case JsonValueKind.Array:
                        foreach (var item in node.EnumerateArray())
                            q.Enqueue((UnwrapIfJsonString(item), depth + 1));
                        break;

                    case JsonValueKind.String:
                        // string, který možná obsahuje JSON → rozbalit a pokračovat
                        var unwrapped = UnwrapIfJsonString(node);
                        if (!unwrapped.ValueKind.Equals(JsonValueKind.String))
                            q.Enqueue((unwrapped, depth + 1));
                        break;

                        // primitiva ignorujeme
                }
            }

            return false;
        }

        /// <summary>
        /// Když znáte „cestu“ (např. ["payload","inputPayload"]), projde JSON deterministicky.
        /// V každém kroku umí rozbalit string-JSON i projít pole (zkusí na každém prvku).
        /// </summary>
        public static bool TryGetByPath(
            object? value,
            out JsonElement result,
            ILogger? logger = null,
            JsonSerializerOptions? options = null,
            int maxDepthPerHop = 8,
            params string[] path)
        {
            result = default;
            if (path is null || path.Length == 0)
                return TryCoerceToElement(value, out result, logger, options);

            if (!TryCoerceToElement(value, out var current, logger, options, maxDepthPerHop))
                return false;

            foreach (var segment in path)
            {
                bool moved = false;

                current = UnwrapIfJsonString(current, maxDepthPerHop);

                if (current.ValueKind == JsonValueKind.Object)
                {
                    if (current.TryGetProperty(segment, out var next))
                    {
                        current = UnwrapIfJsonString(next, maxDepthPerHop);
                        moved = true;
                    }
                }
                else if (current.ValueKind == JsonValueKind.Array)
                {
                    // hledáme segment v každém prvku pole
                    foreach (var item in current.EnumerateArray())
                    {
                        var candidate = UnwrapIfJsonString(item, maxDepthPerHop);
                        if (candidate.ValueKind == JsonValueKind.Object &&
                            candidate.TryGetProperty(segment, out var next))
                        {
                            current = UnwrapIfJsonString(next, maxDepthPerHop);
                            moved = true;
                            break;
                        }
                    }
                }
                else if (current.ValueKind == JsonValueKind.String)
                {
                    // další pokus – třeba se rozbalí na objekt s property
                    var uw = UnwrapIfJsonString(current, maxDepthPerHop);
                    if (uw.ValueKind == JsonValueKind.Object && uw.TryGetProperty(segment, out var next))
                    {
                        current = UnwrapIfJsonString(next, maxDepthPerHop);
                        moved = true;
                    }
                    else
                    {
                        current = uw; // posuň se i tak
                    }
                }

                if (!moved)
                    return false;
            }

            result = current.Clone();
            return true;
        }
    }
}
