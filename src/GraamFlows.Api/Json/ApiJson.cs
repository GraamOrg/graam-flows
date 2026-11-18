using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraamFlows.Api.Json;

/// <summary>
///     How the HTTP API reads and writes JSON, in one place, so a caller that runs a request
///     without HTTP (the CLI replaying a run file) binds it exactly as the API would.
/// </summary>
public static class ApiJson
{
    /// <summary>The serializer options the API's controllers bind requests and write responses with.</summary>
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions());

    /// <summary>Apply the API's JSON settings to <paramref name="options" /> (used by Program.cs too).</summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.Converters.Add(new JsonStringEnumConverter());
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        // Allow Infinity/NaN values (can occur with CDR=0 edge cases)
        options.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        return options;
    }

    /// <summary>
    ///     Recursively converts all JSON object keys from snake_case to camelCase, as the API does to
    ///     every POST body before binding it. Values (strings, dates, enum values) are left untouched.
    ///     A body that does not parse is returned unchanged.
    /// </summary>
    /// <param name="json">the request body.</param>
    /// <param name="named">fields whose own keys are names the caller chose, such as a fee's, so they are data.</param>
    public static string NormalizeKeys(string json, IReadOnlySet<string>? named = null)
    {
        // Fast path, as the API's middleware: a body with no underscore has nothing to rewrite.
        if (!json.Contains('_'))
            return json;
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                NormalizeElement(doc.RootElement, writer, named);
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return json;
        }
    }

    private static void NormalizeElement(JsonElement element, Utf8JsonWriter writer, IReadOnlySet<string>? named,
        bool keepKeys = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    var name = keepKeys ? prop.Name : SnakeToCamel(prop.Name);
                    writer.WritePropertyName(name);
                    NormalizeElement(prop.Value, writer, named, !keepKeys && named?.Contains(name) == true);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    NormalizeElement(item, writer, named);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string SnakeToCamel(string name)
    {
        if (!name.Contains('_'))
            return name; // Already camelCase or single word — no change

        var sb = new StringBuilder(name.Length);
        var capitalizeNext = false;
        foreach (var c in name)
            if (c == '_')
            {
                capitalizeNext = true;
            }
            else
            {
                sb.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
                capitalizeNext = false;
            }

        return sb.ToString();
    }
}
