using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        // Allow Infinity/NaN values (can occur with CDR=0 edge cases)
        options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "GraamFlows API", Version = "v1" });
});

// Add CORS for local development
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseSwagger();
app.UseSwaggerUI();

app.UseCors();

// Normalize incoming JSON keys: convert snake_case to camelCase so the API
// accepts both "original_balance" and "originalBalance" transparently.
app.Use(async (context, next) =>
{
    if (context.Request.ContentType?.Contains("json") == true
        && context.Request.ContentLength > 0
        && (context.Request.Method == "POST" || context.Request.Method == "PUT"))
    {
        context.Request.EnableBuffering();
        var body = await new StreamReader(context.Request.Body, Encoding.UTF8).ReadToEndAsync();
        context.Request.Body.Position = 0;

        // Only rewrite if the body contains underscores (fast path: skip if already camelCase)
        if (body.Contains('_'))
        {
            var normalized = NormalizeJsonKeys(body);
            var bytes = Encoding.UTF8.GetBytes(normalized);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
        }
    }

    await next();
});

app.MapControllers();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

var port = Environment.GetEnvironmentVariable("PORT") ?? "5200";
app.Run($"http://0.0.0.0:{port}");

/// <summary>
/// Recursively converts all JSON object keys from snake_case to camelCase.
/// Values (strings, dates, enum values) are left untouched.
/// </summary>
static string NormalizeJsonKeys(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            NormalizeElement(doc.RootElement, writer);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
    catch
    {
        // If parsing fails, return original body unchanged
        return json;
    }
}

static void NormalizeElement(JsonElement element, Utf8JsonWriter writer)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            writer.WriteStartObject();
            foreach (var prop in element.EnumerateObject())
            {
                writer.WritePropertyName(SnakeToCamel(prop.Name));
                NormalizeElement(prop.Value, writer);
            }

            writer.WriteEndObject();
            break;

        case JsonValueKind.Array:
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
                NormalizeElement(item, writer);
            writer.WriteEndArray();
            break;

        default:
            element.WriteTo(writer);
            break;
    }
}

static string SnakeToCamel(string name)
{
    if (!name.Contains('_'))
        return name; // Already camelCase or single word — no change

    var sb = new StringBuilder(name.Length);
    var capitalizeNext = false;
    for (var i = 0; i < name.Length; i++)
    {
        var c = name[i];
        if (c == '_')
        {
            capitalizeNext = true;
        }
        else
        {
            sb.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
            capitalizeNext = false;
        }
    }

    return sb.ToString();
}
