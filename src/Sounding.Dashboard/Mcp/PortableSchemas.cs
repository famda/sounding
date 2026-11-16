using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Sounding.Dashboard.Application.Common;

namespace Sounding.Dashboard.Mcp;

// The SDK writes a nullable value as "type": ["string", "null"]: valid JSON Schema, but clients that map tool schemas
// onto a single-type dialect (such as Gemini's OpenAPI subset) reject the tool or drop the constraint. Rewritten once
// at startup: an optional input loses its null (leaving it out means the same), and anything else becomes anyOf
// branches with one type each.
public static class PortableSchemas
{
    private static readonly HashSet<string> Annotations =
        ["title", "description", "default", "examples", "$comment", "deprecated", "readOnly", "writeOnly", "$id", "$schema", "$defs", "definitions"];

    public static void Apply(McpServerOptions options)
    {
        var choices = Choices();
        foreach (var tool in options.ToolCollection ?? [])
        {
            var protocol = tool.ProtocolTool;
            protocol.InputSchema = Describe(Rewrite(protocol.InputSchema, input: true), choices.GetValueOrDefault(protocol.Name));
            if (protocol.OutputSchema is { } output)
            {
                protocol.OutputSchema = Rewrite(output, input: false);
            }
        }
    }

    // The fixed values of each tool's text parameters ([Choices]), by tool name and parameter name.
    private static Dictionary<string, Dictionary<string, string[]>> Choices()
    {
        var byTool = new Dictionary<string, Dictionary<string, string[]>>();
        foreach (var method in typeof(SoundingTools).GetMethods())
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>()?.Name is not { } name) continue;
            foreach (var parameter in method.GetParameters())
            {
                if (parameter.GetCustomAttribute<ChoicesAttribute>() is not { } allowed || parameter.Name is null) continue;
                if (!byTool.TryGetValue(name, out var parameters)) byTool[name] = parameters = [];
                parameters[parameter.Name] = allowed.Choices;
            }
        }

        return byTool;
    }

    // What helps a model fill a parameter: its fixed values as an enum (clients that constrain generation to the
    // schema then cannot produce another), and no "default": null, which models copy as the text "null".
    private static JsonElement Describe(JsonElement schema, Dictionary<string, string[]>? choices)
    {
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root || root["properties"] is not JsonObject properties) return schema;

        foreach (var (name, property) in properties)
        {
            if (property is not JsonObject parameter) continue;
            if (parameter.TryGetPropertyValue("default", out var value) && value is null) parameter.Remove("default");
            if (choices?.GetValueOrDefault(name) is { } values && !parameter.ContainsKey("enum"))
            {
                parameter["enum"] = new JsonArray([.. values.Select(static v => (JsonNode)JsonValue.Create(v))]);
            }
        }

        return JsonSerializer.SerializeToElement(root);
    }

    public static JsonElement Rewrite(JsonElement schema, bool input)
    {
        if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root)
        {
            return schema;
        }

        // The SDK points repeated types at their first use ("$ref": "#/properties/…"); splitting moves that target
        // into an anyOf branch, so references are inlined first.
        var original = (JsonObject)root.DeepClone();
        root = (JsonObject)Inline(root, original, depth: 0)!;
        Visit(root, input, optional: false);
        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonNode? Inline(JsonNode? node, JsonObject original, int depth)
    {
        switch (node)
        {
            case JsonObject obj when obj["$ref"] is JsonValue reference && reference.GetValue<string>() is ['#', '/', ..] pointer:
                if (depth > 32 || Resolve(original, pointer) is not JsonObject target)
                {
                    return obj;
                }

                var resolved = (JsonObject)target.DeepClone();
                foreach (var (key, value) in obj.Where(p => p.Key != "$ref").ToList())
                {
                    obj.Remove(key);
                    resolved[key] = value;
                }

                return Inline(resolved, original, depth + 1);
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    var inlined = Inline(child, original, depth);
                    if (!ReferenceEquals(child, inlined))
                    {
                        obj[key] = inlined;
                    }
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    var inlined = Inline(child, original, depth);
                    if (!ReferenceEquals(child, inlined))
                    {
                        array[i] = inlined;
                    }
                }

                return array;
            default:
                return node;
        }
    }

    private static JsonNode? Resolve(JsonObject root, string pointer)
    {
        JsonNode? node = root;
        foreach (var segment in pointer[2..].Split('/'))
        {
            var name = segment.Replace("~1", "/").Replace("~0", "~");
            node = node switch
            {
                JsonObject obj => obj[name],
                JsonArray array when int.TryParse(name, out var index) && index < array.Count => array[index],
                _ => null,
            };
        }

        return node;
    }

    private static void Visit(JsonObject schema, bool input, bool optional)
    {
        if (schema["properties"] is JsonObject properties)
        {
            var required = schema["required"] is JsonArray names ? names.Select(n => n?.GetValue<string>()).ToHashSet() : [];
            foreach (var (name, property) in properties)
            {
                if (property is JsonObject child)
                {
                    Visit(child, input, optional: !required.Contains(name));
                }
            }
        }

        foreach (var key in (ReadOnlySpan<string>)["items", "additionalProperties", "not", "contains", "propertyNames"])
        {
            if (schema[key] is JsonObject child)
            {
                Visit(child, input, optional: false);
            }
        }

        foreach (var key in (ReadOnlySpan<string>)["anyOf", "oneOf", "allOf", "prefixItems"])
        {
            if (schema[key] is JsonArray branches)
            {
                foreach (var branch in branches.OfType<JsonObject>())
                {
                    Visit(branch, input, optional: false);
                }
            }
        }

        foreach (var key in (ReadOnlySpan<string>)["$defs", "definitions"])
        {
            if (schema[key] is JsonObject definitions)
            {
                foreach (var (_, definition) in definitions)
                {
                    if (definition is JsonObject child)
                    {
                        Visit(child, input, optional: false);
                    }
                }
            }
        }

        if (schema["type"] is JsonArray types)
        {
            Split(schema, types, dropNull: input && optional);
        }
    }

    private static void Split(JsonObject schema, JsonArray types, bool dropNull)
    {
        var kinds = types.Select(t => t!.GetValue<string>()).Where(t => t != "null").Distinct().ToList();
        var nullable = kinds.Count < types.Count;
        if (kinds.Count == 1 && (!nullable || dropNull))
        {
            schema["type"] = kinds[0];
            RemoveNullFromEnum(schema);
            return;
        }

        schema.Remove("type");
        var branches = new JsonArray();
        if (kinds.Count == 1)
        {
            // The constraints (properties, items, enum, format…) belong to the value's own type, not to null.
            var typed = new JsonObject { ["type"] = kinds[0] };
            foreach (var key in schema.Select(p => p.Key).Where(k => !Annotations.Contains(k)).ToList())
            {
                var value = schema[key];
                schema.Remove(key);
                typed[key] = value;
            }

            RemoveNullFromEnum(typed);
            branches.Add(typed);
        }
        else
        {
            foreach (var kind in kinds)
            {
                branches.Add(new JsonObject { ["type"] = kind });
            }
        }

        if (nullable && !dropNull)
        {
            branches.Add(new JsonObject { ["type"] = "null" });
        }

        schema["anyOf"] = branches;
    }

    private static void RemoveNullFromEnum(JsonObject schema)
    {
        if (schema["enum"] is JsonArray values)
        {
            for (var i = values.Count - 1; i >= 0; i--)
            {
                if (values[i] is null)
                {
                    values.RemoveAt(i);
                }
            }
        }
    }
}
