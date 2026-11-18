using System.Text.Json;
using System.Text.Json.Nodes;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Json;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraamFlows.Cli.Services;

/// <summary>What one scenario of a run file produced.</summary>
public sealed class RunFileOutcome
{
    public required RunFileScenario Scenario { get; init; }
    public required CalcCollateralResponse? Collateral { get; init; }
    public required WaterfallResponse Waterfall { get; init; }
    public required DealDto Deal { get; init; }

    /// <summary>The tranche's WAL in the file's stated field, or null when the engine reported none.</summary>
    public double? Wal(string tranche, string? walField)
    {
        if (!Waterfall.Summary.TranchesSummary.TryGetValue(tranche, out var summary))
            return null;
        return string.Equals(walField, "wal", StringComparison.OrdinalIgnoreCase) ? summary.Wal : summary.BalanceWal;
    }
}

/// <summary>
///     Runs a <see cref="RunFile" /> scenario through the same controller code the HTTP API runs:
///     the body is key-normalized and bound with the API's JSON options, then handed to
///     <see cref="CalcCollateralController" /> and <see cref="WaterfallController" />. There is no
///     CLI-side model of the deal, so the CLI cannot answer differently from the API on the same body.
/// </summary>
public sealed class RunFileRunner
{
    /// <param name="file">the run file.</param>
    /// <param name="scenario">one of its scenarios.</param>
    /// <param name="editCollateral">a change to the collateral request before it runs (command-line overrides).</param>
    public RunFileOutcome Run(RunFile file, RunFileScenario scenario, Action<JsonObject>? editCollateral = null)
    {
        CalcCollateralResponse? collateral = null;
        JsonArray rows;
        if (scenario.CollateralCashflows != null)
        {
            if (editCollateral != null)
                throw new InvalidOperationException(
                    $"Scenario '{scenario.Name}' carries the collateral rows it was run on, not a collateral " +
                    "request, so its assumptions cannot be changed here.");
            rows = (JsonArray)scenario.CollateralCashflows.DeepClone();
        }
        else
        {
            var calcBody = RunFile.Merge(file.CalcCollateral, scenario.CalcCollateral);
            editCollateral?.Invoke(calcBody);
            var calcRequest = Bind<CalcCollateralRequest>(calcBody);
            collateral = Unwrap(new CalcCollateralController(NullLogger<CalcCollateralController>.Instance)
                .Calculate(calcRequest), "CalcCollateral");
            rows = JsonSerializer.SerializeToNode(collateral.Cashflows, ApiJson.Options)!.AsArray();
            ApplyAdjustments(rows, scenario);
        }

        var waterfallBody = RunFile.Merge(file.Waterfall, scenario.Waterfall);
        waterfallBody["collateralCashflows"] = rows;
        var waterfallRequest = Bind<WaterfallRequest>(waterfallBody);
        var waterfall = Unwrap(new WaterfallController(NullLogger<WaterfallController>.Instance)
            .Execute(waterfallRequest), "Waterfall");

        return new RunFileOutcome
        {
            Scenario = scenario, Collateral = collateral, Waterfall = waterfall, Deal = waterfallRequest.Deal
        };
    }

    /// <summary>Bind a body as the API does: snake_case keys rewritten, then the API's serializer options.</summary>
    public static T Bind<T>(JsonObject body)
    {
        var json = ApiJson.NormalizeKeys(body.ToJsonString());
        return JsonSerializer.Deserialize<T>(json, ApiJson.Options)
               ?? throw new InvalidOperationException($"The {typeof(T).Name} body is empty.");
    }

    private static void ApplyAdjustments(JsonArray rows, RunFileScenario scenario)
    {
        foreach (var adjustment in scenario.CollateralAdjustments ?? [])
        {
            if (adjustment.Row < 0 || adjustment.Row >= rows.Count || rows[adjustment.Row] is not JsonObject row)
                throw new InvalidOperationException(
                    $"Scenario '{scenario.Name}' adjusts collateral row {adjustment.Row}, but the engine returned " +
                    $"{rows.Count} rows. The file was made against a different collateral run.");
            foreach (var (field, amount) in adjustment.Add)
            {
                var key = row.Select(p => p.Key)
                    .FirstOrDefault(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase)) ?? field;
                var current = row[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0.0;
                row[key] = current + amount;
            }
        }
    }

    private static T Unwrap<T>(ActionResult<T> result, string endpoint)
    {
        if (result.Value is { } value)
            return value;
        switch (result.Result)
        {
            case ObjectResult { StatusCode: null or 200, Value: T ok }:
                return ok;
            case ObjectResult refused:
                var reason = refused.Value is null
                    ? "(no reason)"
                    : JsonSerializer.Serialize(refused.Value, ApiJson.Options);
                throw new InvalidOperationException($"{endpoint} refused the request ({refused.StatusCode}): {reason}");
            default:
                throw new InvalidOperationException($"{endpoint} returned no result.");
        }
    }
}
