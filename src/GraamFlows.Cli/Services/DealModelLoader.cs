using System.Text.Json;
using System.Text.Json.Serialization;
using GraamFlows.Api.Models;

namespace GraamFlows.Cli.Services;

public class DealModelFile
{
    public DealDto Deal { get; set; } = new();
    public PoolStratificationSection? PoolStratification { get; set; }
    public CollateralSection? Collateral { get; set; }
    public WalScenariosSection? WalScenarios { get; set; }
    public DateTime? ProjectionDate { get; set; }
    public Dictionary<string, FactorEntry>? Factors { get; set; }
    public DateTime? ClosingDate { get; set; }
    public DateTime? CutoffDate { get; set; }
}

public class PoolStratificationSection
{
    public string? Description { get; set; }
    public double? TotalBalance { get; set; }
    public double? WeightedAverageApr { get; set; }
    public double? WeightedAverageTerm { get; set; }
    public double? WeightedAverageRemainingTerm { get; set; }
    public List<PoolEntry>? Pools { get; set; }
}

public class PoolEntry
{
    public int PoolNum { get; set; } = 1;
    public double AggregateBalance { get; set; }
    public double GrossApr { get; set; }
    public DateTime? NextPaymentDate { get; set; }
    public int OriginalTermMonths { get; set; }
    public int RemainingTermMonths { get; set; }
}

public class PoolStratificationEntry
{
    public string GroupNum { get; set; } = "1";
    public double Balance { get; set; }
    public double Wac { get; set; }
    public double Wam { get; set; }
    public double? Wala { get; set; }
    public int? OriginalTerm { get; set; }
    public double? ServiceFee { get; set; }
    public string? LoanStatus { get; set; }
}

public class CollateralSection
{
    public double? TotalBalance { get; set; }
    public double? Wac { get; set; }
    public double? Wam { get; set; }
    public double? Wala { get; set; }
    public int? OriginalTerm { get; set; }
    public double? ServiceFee { get; set; }
    public List<AssetEntry>? Assets { get; set; }
}

public class AssetEntry
{
    public string? AssetId { get; set; }
    public string GroupNum { get; set; } = "1";
    public double Balance { get; set; }
    public double InterestRate { get; set; }
    public int RemainingTerm { get; set; }
    public int? OriginalTerm { get; set; }
    public double? ServiceFee { get; set; }
    public DateTime? OriginationDate { get; set; }
    public string? LoanStatus { get; set; }
}

public class WalScenariosSection
{
    public List<double> AbsPercentages { get; set; } = new();
    public string? Description { get; set; }
    public WalAssumptions? Assumptions { get; set; }
    public List<WalTrancheEntry> Tranches { get; set; } = new();

    /// <summary>
    /// Flatten the WAL scenarios into a list of individual scenario entries for validation
    /// </summary>
    public List<WalScenarioEntry> ToScenarioEntries()
    {
        var entries = new List<WalScenarioEntry>();

        foreach (var tranche in Tranches)
        {
            // Skip tranches with null WAL data
            if (tranche.WalToCall == null)
                continue;

            for (var i = 0; i < AbsPercentages.Count && i < tranche.WalToCall.Count; i++)
            {
                // Skip null WAL values (tranche doesn't exist at this ABS%)
                if (!tranche.WalToCall[i].HasValue)
                    continue;

                entries.Add(new WalScenarioEntry
                {
                    TrancheName = tranche.TrancheName,
                    AbsPct = AbsPercentages[i], // Already in percentage form (2.0 = 2%)
                    ExpectedWal = tranche.WalToCall[i].Value,
                    Cpr = AbsPercentages[i] // ABS% = CPR for auto ABS
                });
            }
        }

        return entries;
    }
}

public class WalAssumptions
{
    public DateTime? PurchaseDate { get; set; }
    public DateTime? FirstDistributionDate { get; set; }
    public int? PaymentDayOfMonth { get; set; }
    public bool CleanUpCallAssumed { get; set; } = true; // Default to true for prospectus WAL tables
    public double? ServicingFeeRate { get; set; }
    public double? OtherMonthlyFees { get; set; }
    public double? ReserveAccountTargetPct { get; set; }
    public List<WalInterestRate>? InterestRates { get; set; }
}

public class WalInterestRate
{
    public string? TrancheName { get; set; }
    public double Rate { get; set; }
    public string? DayCount { get; set; }
}

public class WalTrancheEntry
{
    public string TrancheName { get; set; } = "";
    public List<double?> WalToCall { get; set; } = new();
    public List<double?> WalToMaturity { get; set; } = new();
}

public class WalScenarioEntry
{
    public string? TrancheName { get; set; }
    public double AbsPct { get; set; }
    public double ExpectedWal { get; set; }
    public double? Cpr { get; set; }
}

public class DealModelLoader
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new FlexibleDateTimeConverter(), new FactorEntryConverter() }
    };

    public async Task<DealModelFile> LoadAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);

        // First try to deserialize as full DealModelFile (with nested "deal" object)
        var model = JsonSerializer.Deserialize<DealModelFile>(json, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize deal model");

        // If deal.dealName is empty, try parsing as a flat structure where deal properties are at root
        if (string.IsNullOrEmpty(model.Deal.DealName))
        {
            // Try deserializing the root as DealDto directly
            var dealDto = JsonSerializer.Deserialize<DealDto>(json, _jsonOptions);
            if (dealDto != null && !string.IsNullOrEmpty(dealDto.DealName))
            {
                model.Deal = dealDto;
            }
        }

        // Validate required fields
        if (string.IsNullOrEmpty(model.Deal.DealName))
            throw new InvalidOperationException("Deal name is required");

        if (model.Deal.Tranches.Count == 0)
            throw new InvalidOperationException("At least one tranche is required");

        // If no Collateral section exists, try to build one from deal-level fields.
        // Deal JSON may have collateralBalance/balanceAtIssuance at root
        // plus collateral characteristics embedded in a "collateral" object.
        if (model.Collateral == null && model.PoolStratification == null)
        {
            // Try parsing a root-level "collateral" object
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("collateral", out var collateralEl) && collateralEl.ValueKind == JsonValueKind.Object)
            {
                model.Collateral = JsonSerializer.Deserialize<CollateralSection>(collateralEl.GetRawText(), _jsonOptions);
            }

            // If still no collateral section, synthesize from top-level fields
            if (model.Collateral == null)
            {
                double? balance = null;
                double? wac = null;
                double? wam = null;

                if (root.TryGetProperty("collateralBalance", out var cbEl) && cbEl.ValueKind == JsonValueKind.Number)
                    balance = cbEl.GetDouble();
                else if (root.TryGetProperty("balanceAtIssuance", out var baiEl) && baiEl.ValueKind == JsonValueKind.Number)
                    balance = baiEl.GetDouble();
                else if (model.Deal.BalanceAtIssuance.HasValue && model.Deal.BalanceAtIssuance > 0)
                    balance = model.Deal.BalanceAtIssuance;

                if (root.TryGetProperty("wac", out var wacEl) && wacEl.ValueKind == JsonValueKind.Number)
                    wac = wacEl.GetDouble();
                else if (root.TryGetProperty("wavgRate", out var wrEl) && wrEl.ValueKind == JsonValueKind.Number)
                    wac = wrEl.GetDouble();

                if (root.TryGetProperty("wam", out var wamEl) && wamEl.ValueKind == JsonValueKind.Number)
                    wam = wamEl.GetDouble();
                else if (root.TryGetProperty("wavgTerm", out var wtEl) && wtEl.ValueKind == JsonValueKind.Number)
                    wam = wtEl.GetDouble();

                if (balance.HasValue && balance > 0)
                {
                    model.Collateral = new CollateralSection
                    {
                        TotalBalance = balance,
                        Wac = wac,
                        Wam = wam,
                        OriginalTerm = wam.HasValue ? (int)Math.Round(wam.Value) : null,
                        Wala = 0
                    };
                }
            }
        }

        return model;
    }

    public async Task<Dictionary<string, FactorEntry>> LoadFactorsAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<Dictionary<string, FactorEntry>>(json, _jsonOptions)
            ?? new Dictionary<string, FactorEntry>();
    }
}

/// <summary>
/// Handles partial date strings like "2024-05" by defaulting to the last day of the month.
/// </summary>
public class FlexibleDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        var str = reader.GetString();
        if (string.IsNullOrEmpty(str))
            return null;

        if (DateTime.TryParse(str, out var dt))
            return dt;

        // Handle "YYYY-MM" → last day of month
        if (str.Length == 7 && str[4] == '-'
            && int.TryParse(str[..4], out var year)
            && int.TryParse(str[5..], out var month))
        {
            return new DateTime(year, month, DateTime.DaysInMonth(year, month));
        }

        throw new JsonException($"Cannot parse date: '{str}'");
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd"));
        else
            writer.WriteNullValue();
    }
}

public class FactorEntryConverter : JsonConverter<FactorEntry>
{
    public override FactorEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return new FactorEntry { Factor = reader.GetDouble() };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
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
            writer.WriteStartObject();
            writer.WriteNumber("balance", value.Balance.Value);
            writer.WriteEndObject();
        }
        else if (value.Factor.HasValue)
        {
            writer.WriteNumberValue(value.Factor.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
