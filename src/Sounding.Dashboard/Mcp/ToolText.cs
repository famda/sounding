using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using Sounding.Dashboard.Application.Common;

namespace Sounding.Dashboard.Mcp;

// Tool results are text a model reads, not a contract a program binds to (that is the REST API): compact JSON without
// what carries no information (nulls, empty lists, digits of false precision), and bounded lists, so an investigation
// of many calls fits a small model's context.
public static class ToolText
{
    private const int MaxListItems = 40;
    private const int MaxCommandLine = 160;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new RoundedNumbers() },
    };

    private static readonly JsonSerializerOptions Writing = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string From<T>(Result<T> result, params ReadOnlySpan<string> omit) => Text(result.Unwrap(), omit);

    public static string Text<T>(T value, params ReadOnlySpan<string> omit)
    {
        var node = JsonSerializer.SerializeToNode(value, Options);
        Prune(node, omit);
        return node?.ToJsonString(Writing) ?? "null";
    }

    // The SDK turns an McpException into a tool result with IsError set and this message, which the model can act on:
    // so the message says what to do next, not only what went wrong.
    public static T Unwrap<T>(this Result<T> result) => result.IsSuccess
        ? result.Value!
        : throw new McpException(result.Status switch
        {
            ResultStatus.Invalid when result.Errors is { Count: > 0 } errors => string.Join(" ", errors.SelectMany(e => e.Value)),
            _ => result.Error ?? result.Status.ToString(),
        } + result.Status switch
        {
            ResultStatus.NotFound => " Call list_containers or list_hosts to see the names that exist; do not guess.",
            ResultStatus.Invalid when result.Error?.Contains("ambiguous", StringComparison.Ordinal) == true =>
                " Do not pick one silently: ask the user which one is meant, or check each of them and say so.",
            _ => "",
        });

    private static void Prune(JsonNode? node, ReadOnlySpan<string> omit)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (omit.Contains(name) || child is JsonArray { Count: 0 } or JsonObject { Count: 0 })
                    {
                        obj.Remove(name);
                        continue;
                    }

                    if (child is JsonArray { Count: > MaxListItems } list)
                    {
                        obj[name + "Omitted"] = list.Count - MaxListItems;
                        while (list.Count > MaxListItems) list.RemoveAt(list.Count - 1);
                    }

                    // A command line can be a whole script; its start says which program it is.
                    if (name == "commandLine" && child is JsonValue command && command.TryGetValue<string>(out var text) && text.Length > MaxCommandLine)
                    {
                        obj[name] = string.Concat(text.AsSpan(0, MaxCommandLine), "…");
                    }

                    Prune(child, omit);
                    if (child is JsonObject { Count: 0 }) obj.Remove(name);
                }

                break;
            case JsonArray array:
                foreach (var item in array) Prune(item, omit);
                break;
        }
    }

    private sealed class RoundedNumbers : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(double.IsFinite(value) ? Math.Round(value, Math.Abs(value) >= 100 ? 0 : 3) : 0);
    }
}
