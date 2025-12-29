using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraamFlows.Api.Models;

/// <summary>
/// JSON converter for FactorEntry that handles both:
/// - Plain numbers: { "A-1": 0.5 } -> FactorEntry { Factor = 0.5 }
/// - Objects: { "CERTIFICATES": { "balance": 45000000 } } -> FactorEntry { Balance = 45000000 }
/// </summary>
public class FactorEntryConverter : JsonConverter<FactorEntry>
{
    public override FactorEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            // Plain number = factor value
            return new FactorEntry { Factor = reader.GetDouble() };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            // Object with factor or balance property
            var entry = new FactorEntry();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    break;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var propertyName = reader.GetString()?.ToLowerInvariant();
                    reader.Read();

                    switch (propertyName)
                    {
                        case "factor":
                            entry.Factor = reader.GetDouble();
                            break;
                        case "balance":
                            entry.Balance = reader.GetDouble();
                            break;
                    }
                }
            }

            return entry;
        }

        throw new JsonException($"Unexpected token type {reader.TokenType} for FactorEntry");
    }

    public override void Write(Utf8JsonWriter writer, FactorEntry value, JsonSerializerOptions options)
    {
        if (value.Balance.HasValue)
        {
            // Write as object with balance
            writer.WriteStartObject();
            writer.WriteNumber("balance", value.Balance.Value);
            writer.WriteEndObject();
        }
        else if (value.Factor.HasValue)
        {
            // Write as plain number
            writer.WriteNumberValue(value.Factor.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
