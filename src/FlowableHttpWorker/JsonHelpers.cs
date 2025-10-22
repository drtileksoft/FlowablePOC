using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FlowableHttpWorker
{

    public static class JsonHelpers
    {
        // Bezpečně naparsuje JSON ze stringu do JsonElement (klonuje ho, takže je nezávislý na JsonDocument)
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

        // Pokud je element typu String a ten string je JSON, rozbalí ho (jedna úroveň „double-encoded“).
        public static JsonElement UnwrapIfJsonString(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (TryParseJsonElement(s, out var inner))
                    return inner;
            }
            return el;
        }

        // Zkusí z libovolného "value" (string/JsonElement/…)
        // získat element root, který je skutečným JSON objektem/poolem.
        public static bool TryGetRootElement(object? value, out JsonElement root, ILogger logger)
        {
            root = default;

            switch (value)
            {
                case null:
                    logger.LogWarning("Value is null.");
                    return false;

                case string s:
                    if (!TryParseJsonElement(s, out root))
                    {
                        logger.LogWarning("String value is not a valid JSON.");
                        return false;
                    }
                    root = UnwrapIfJsonString(root); // kdyby to byl string s JSONem
                    return true;

                case JsonElement el:
                    // Může to být rovnou objekt/array/primitive, nebo string s JSONem
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var s2 = el.GetString();
                        if (!TryParseJsonElement(s2, out root))
                        {
                            logger.LogWarning("JsonElement string is not a valid JSON.");
                            return false;
                        }
                        root = UnwrapIfJsonString(root);
                        return true;
                    }
                    else
                    {
                        root = el;
                        return true;
                    }

                default:
                    // Zkuste ToString() – někdy sem přijde typ od knihovny, který do ToString() položí JSON
                    var txt = value.ToString();
                    if (!TryParseJsonElement(txt, out root))
                    {
                        logger.LogWarning("Unsupported value type {Type} and ToString() is not valid JSON.", value.GetType().FullName);
                        return false;
                    }
                    root = UnwrapIfJsonString(root);
                    return true;
            }
        }

        // Vytáhne inputPayload (pokud existuje) a případně ještě jednou rozbalí, pokud je to JSON uvnitř stringu
        public static bool TryGetInputPayload(object? value, out JsonElement inputPayload, string elementName, ILogger logger, JsonSerializerOptions? options = null)
        {
            inputPayload = default;

            if (!TryGetRootElement(value, out var root, logger))
                return false;

            // Pokud root rovnou obsahuje inputPayload jako property
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(elementName, out var payload))
            {
                inputPayload = UnwrapIfJsonString(payload);
                return true;
            }

            // Pokud "root" sám JE tím payloadem (knihovny občas pošlou jen payload bez obalu)
            inputPayload = UnwrapIfJsonString(root);
            return true;
        }
    }

}
