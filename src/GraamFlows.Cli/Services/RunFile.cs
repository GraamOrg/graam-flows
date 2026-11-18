using System.Text.Json;
using System.Text.Json.Nodes;

namespace GraamFlows.Cli.Services;

/// <summary>
///     A run file: the exact requests a modelling service sent to this engine's HTTP API for one
///     deal, so a local run is the same run.
/// </summary>
/// <remarks>
///     <para>
///         A deal model file describes a deal and leaves the CLI to rebuild the collateral, the
///         prepayment convention and the structure from it, which is a second implementation of
///         whatever the caller does before it posts. A run file is the posts themselves:
///         <c>calcCollateral</c> is the body of <c>POST /api/CalcCollateral</c> and
///         <c>waterfall</c> the body of <c>POST /api/Waterfall</c> without its
///         <c>collateralCashflows</c>, which the CLI fills from the first call's response, as the
///         caller did. Nothing is rebuilt, so nothing can come out different.
///     </para>
///     <para>
///         Each scenario states only what differs between runs (the prepayment speed, a per-asset
///         vector, a trigger forecast); its top-level keys replace the shared request's. A scenario
///         can also carry what the caller added to the collateral rows between the two calls
///         (<c>collateralAdjustments</c>), or the rows themselves when it changed their shape
///         (<c>collateralCashflows</c>). <c>expectedWal</c> is a published figure to score against;
///         <c>referenceWal</c> is what the caller's own run of this request reported, so a
///         local run can show it reproduced it.
///     </para>
/// </remarks>
public sealed class RunFile
{
    /// <summary>The format version. Present on every run file, and how one is told from a deal model.</summary>
    public int GraamflowsRun { get; set; }

    public string? DealName { get; set; }

    /// <summary>The date the collateral and class balances are as of (a cut-off or a distribution date).</summary>
    public string? CollateralDate { get; set; }

    public string? Description { get; set; }

    /// <summary>The <c>POST /api/CalcCollateral</c> body every scenario shares.</summary>
    public JsonObject CalcCollateral { get; set; } = new();

    /// <summary>The <c>POST /api/Waterfall</c> body every scenario shares, without collateral cashflows.</summary>
    public JsonObject Waterfall { get; set; } = new();

    /// <summary>The tranche-summary WAL field the figures are stated in: <c>balanceWal</c> (default) or <c>wal</c>.</summary>
    public string? WalField { get; set; }

    /// <summary>The tolerance, in years, a published WAL is scored at when the command line states none.</summary>
    public double? WalTolerance { get; set; }

    /// <summary>
    ///     Decimal places the difference between a model and a published WAL is rounded to before it
    ///     is held to the tolerance, when the publisher of the file scores it that way. Unstated, the
    ///     difference is compared unrounded. A file whose cells were scored as
    ///     <c>round(model - published, 3) &lt;= 0.15</c> states 3, so a model WAL 0.1504 years off is
    ///     a tie here as it was there.
    /// </summary>
    public int? WalDiffDecimals { get; set; }

    /// <summary>The difference <paramref name="computed" /> − <paramref name="expected" />, rounded as the file states.</summary>
    public double WalDifference(double computed, double expected)
    {
        var diff = computed - expected;
        return WalDiffDecimals is { } d ? Math.Round(diff, d, MidpointRounding.ToEven) : diff;
    }

    public List<RunFileScenario> Scenarios { get; set; } = new();

    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Whether <paramref name="json" /> is a run file rather than a deal model.</summary>
    public static bool IsRunFile(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("graamflowsRun", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static RunFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<RunFile>(json, ReadOptions)
                   ?? throw new InvalidOperationException("The run file is empty.");
        if (file.GraamflowsRun != 1)
            throw new InvalidOperationException(
                $"This CLI reads run file version 1; the file says version {file.GraamflowsRun}. Update the CLI.");
        if (file.Scenarios.Count == 0)
            throw new InvalidOperationException("The run file has no scenarios to run.");
        return file;
    }

    public static async Task<RunFile> LoadAsync(string path) => Parse(await File.ReadAllTextAsync(path));

    /// <summary>The scenario named <paramref name="selector" />, or at that 1-based position; the first when null.</summary>
    public RunFileScenario Select(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return Scenarios[0];
        var byName = Scenarios.FirstOrDefault(s => string.Equals(s.Name, selector, StringComparison.OrdinalIgnoreCase));
        if (byName != null)
            return byName;
        if (int.TryParse(selector, out var n) && n >= 1 && n <= Scenarios.Count)
            return Scenarios[n - 1];
        throw new InvalidOperationException(
            $"No scenario '{selector}'. The file has: {string.Join(", ", Scenarios.Select((s, i) => $"{i + 1} \"{s.Name}\""))}.");
    }

    /// <summary>The scenario's request: the shared body with the scenario's top-level keys replacing it.</summary>
    public static JsonObject Merge(JsonObject shared, JsonObject? overrides)
    {
        var merged = (JsonObject)shared.DeepClone();
        if (overrides == null)
            return merged;
        foreach (var (key, value) in overrides)
            merged[key] = value?.DeepClone();
        return merged;
    }
}

public sealed class RunFileScenario
{
    public string Name { get; set; } = "";

    /// <summary><c>walToMaturity</c> or <c>walToCall</c>; informational.</summary>
    public string? Basis { get; set; }

    /// <summary>The prepayment speed, in <see cref="PrepaymentType" />; informational.</summary>
    public double? Speed { get; set; }

    public string? PrepaymentType { get; set; }

    public JsonObject? CalcCollateral { get; set; }
    public JsonObject? Waterfall { get; set; }

    /// <summary>Amounts the caller added to the engine's collateral rows before posting them to the waterfall.</summary>
    public List<CollateralAdjustment>? CollateralAdjustments { get; set; }

    /// <summary>The collateral rows the caller posted, when it did not post the engine's rows as returned.</summary>
    public JsonArray? CollateralCashflows { get; set; }

    /// <summary>Published WAL by tranche, to score against.</summary>
    public Dictionary<string, double>? ExpectedWal { get; set; }

    /// <summary>The WAL by tranche the caller's own run of this request reported.</summary>
    public Dictionary<string, double>? ReferenceWal { get; set; }
}

public sealed class CollateralAdjustment
{
    /// <summary>0-based position of the row in the collateral response.</summary>
    public int Row { get; set; }

    /// <summary>Field name → amount added to it.</summary>
    public Dictionary<string, double> Add { get; set; } = new();
}
