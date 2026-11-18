using System.Diagnostics;
using System.Text.Json.Nodes;
using GraamFlows.Cli.Models;
using GraamFlows.Cli.Services;

namespace GraamFlows.Cli.Commands;

/// <summary>The <c>run</c> and <c>wal-tests</c> commands on a <see cref="RunFile" />.</summary>
public static class RunFileCommands
{
    /// <summary>How close a local WAL must be to the file's reference run to count as reproducing it, in years.</summary>
    public const double ReproduceTolerance = 0.001;

    /// <summary>One scored cell: a published WAL, or a reference WAL, against the local run's.</summary>
    public sealed record Cell(string Scenario, string Tranche, double Speed, double Expected, double? Computed, bool Passed);

    /// <summary>Every published cell and every reference WAL of <paramref name="file" />, run locally.</summary>
    public static (List<Cell> Published, List<Cell> Reference) Score(RunFile file, double tolerance,
        Action<string>? log = null)
    {
        var runner = new RunFileRunner();
        var published = new List<Cell>();
        var reference = new List<Cell>();
        foreach (var scenario in file.Scenarios)
        {
            var outcome = runner.Run(file, scenario);
            var speed = scenario.Speed ?? 0;
            foreach (var (tranche, expected) in scenario.ExpectedWal ?? new())
            {
                var computed = outcome.Wal(tranche, file.WalField);
                published.Add(new Cell(scenario.Name, tranche, speed, expected, computed,
                    computed.HasValue && Math.Abs(file.WalDifference(computed.Value, expected)) <= tolerance));
            }

            foreach (var (tranche, expected) in scenario.ReferenceWal ?? new())
            {
                var computed = outcome.Wal(tranche, file.WalField);
                reference.Add(new Cell(scenario.Name, tranche, speed, expected, computed,
                    computed.HasValue && Math.Abs(computed.Value - expected) <= ReproduceTolerance));
            }

            log?.Invoke(Describe(scenario, outcome, file.WalField));
        }

        return (published, reference);
    }

    public static string Describe(RunFileScenario scenario, RunFileOutcome outcome, string? walField)
    {
        var wals = outcome.Waterfall.Summary.TranchesSummary
            .Select(kv => (kv.Key, Wal: outcome.Wal(kv.Key, walField)))
            .Where(x => x.Wal.HasValue)
            .Select(x => $"{x.Key} {x.Wal:F3}");
        return $"  {scenario.Name}: {string.Join(", ", wals)}";
    }

    public static async Task<int> WalTestsAsync(FileInfo path, double? statedThreshold, FileInfo? output, bool verbose)
    {
        var stopwatch = Stopwatch.StartNew();
        var file = await RunFile.LoadAsync(path.FullName);
        var threshold = statedThreshold ?? file.WalTolerance ?? 0.10;
        var dealName = file.DealName ?? Path.GetFileNameWithoutExtension(path.Name);
        Console.WriteLine($"Run file: {dealName}, collateral as of {file.CollateralDate ?? "(not stated)"}, " +
                          $"{file.Scenarios.Count} scenario(s), WAL field {file.WalField ?? "balanceWal"}");

        var (published, reference) = Score(file, threshold, verbose ? Console.WriteLine : null);

        foreach (var c in published.Where(c => !c.Passed))
            Console.WriteLine($"  MISS {c.Scenario} {c.Tranche}: published {c.Expected:F3}, " +
                              $"model {(c.Computed.HasValue ? c.Computed.Value.ToString("F3") : "none")}");
        foreach (var c in reference.Where(c => !c.Passed))
            Console.WriteLine($"  DIFFERS FROM REFERENCE {c.Scenario} {c.Tranche}: reference {c.Expected:F6}, " +
                              $"local {(c.Computed.HasValue ? c.Computed.Value.ToString("F6") : "none")}");

        Console.WriteLine();
        Console.WriteLine($"WAL Validation Summary for {dealName}");
        Console.WriteLine(new string('-', 50));
        if (published.Count > 0)
        {
            Console.WriteLine($"Published WAL cells within {threshold:F2}y: {published.Count(c => c.Passed)} of {published.Count}");
            var outputPath = new WalTestsOptions { DealModelFile = path, OutputFile = output }.GetOutputPath(dealName);
            new ExcelExporter().ExportWalReport(published.Select(c => new WalValidationResult
            {
                TrancheName = $"{c.Tranche} ({c.Scenario})",
                AbsPct = c.Speed,
                Cpr = c.Speed,
                ExpectedWal = c.Expected,
                ComputedWal = c.Computed ?? double.NaN,
                Passed = c.Passed
            }).Where(r => !double.IsNaN(r.ComputedWal)).ToList(), dealName, outputPath, threshold);
            Console.WriteLine($"Results written to: {outputPath}");
        }
        else
        {
            Console.WriteLine("Published WAL cells: none in this file (its collateral date has no published table)");
        }

        if (reference.Count > 0)
        {
            var maxDiff = reference.Max(c => c.Computed.HasValue ? Math.Abs(c.Computed.Value - c.Expected) : double.PositiveInfinity);
            Console.WriteLine($"Reproduces the file's reference run: {reference.Count(c => c.Passed)} of {reference.Count} " +
                              $"tranche WALs within {ReproduceTolerance}y (largest difference {maxDiff:F6}y)");
        }

        Console.WriteLine($"Completed in {stopwatch.ElapsedMilliseconds}ms");
        return published.All(c => c.Passed) && reference.All(c => c.Passed) ? 0 : 1;
    }

    public static async Task<int> RunAsync(FileInfo path, string? scenarioSelector, double? cpr, double? cdr,
        double? sev, double? dq, FileInfo? output, bool verbose)
    {
        var stopwatch = Stopwatch.StartNew();
        var file = await RunFile.LoadAsync(path.FullName);
        var scenario = file.Select(scenarioSelector);
        Console.WriteLine($"Running {file.DealName} as of {file.CollateralDate ?? "(not stated)"}: scenario \"{scenario.Name}\"");

        Action<JsonObject>? edit = null;
        if (cpr.HasValue || cdr.HasValue || sev.HasValue || dq.HasValue)
            edit = body => Override(body, cpr, cdr, sev, dq, scenario.Name);

        var outcome = new RunFileRunner().Run(file, scenario, edit);

        Console.WriteLine();
        Console.WriteLine($"{"Tranche",-12} {"WAL (yrs)",12} {"Principal",18} {"Interest",18}");
        foreach (var (name, summary) in outcome.Waterfall.Summary.TranchesSummary)
            Console.WriteLine($"{name,-12} {outcome.Wal(name, file.WalField)?.ToString("F3") ?? "-",12} " +
                              $"{summary.TotalPrincipal,18:N2} {summary.TotalInterest,18:N2}");

        var outputPath = new RunOptions { DealModelFile = path, OutputFile = output }.GetOutputPath(file.DealName ?? "deal");
        var result = new WaterfallResult
        {
            TrancheCashflows = outcome.Waterfall.TrancheCashflows,
            TriggerResults = outcome.Waterfall.TriggerResults ?? new(),
            TerminationDate = outcome.Waterfall.TerminationDate,
            Summary = outcome.Waterfall.Summary
        };
        var assumptions = RunFile.Merge(file.CalcCollateral, scenario.CalcCollateral)["assumptions"] as JsonObject;
        new ExcelExporter().Export(result, new DealModelFile { Deal = outcome.Deal }, outputPath,
            cpr ?? Number(assumptions, "cpr"), cdr ?? Number(assumptions, "cdr"),
            sev ?? Number(assumptions, "severity"), dq ?? Number(assumptions, "delinquency"));
        Console.WriteLine();
        Console.WriteLine($"Results written to: {outputPath}");
        Console.WriteLine($"Completed in {stopwatch.ElapsedMilliseconds}ms");
        return 0;
    }

    /// <summary>
    ///     Replace the scenario's deal-level speeds. Refused when the scenario sets a speed per
    ///     asset: those vectors were computed from the file's speed, and keeping them beside a new
    ///     deal-level one would run a mixture of the two.
    /// </summary>
    public static void Override(JsonObject body, double? cpr, double? cdr, double? sev, double? dq, string scenario)
    {
        var assumptions = body["assumptions"] as JsonObject ?? new JsonObject();
        body["assumptions"] = assumptions;
        if (cpr.HasValue && body["assetAssumptions"] is JsonObject perAsset
            && perAsset.Any(a => a.Value is JsonObject o && (o.ContainsKey("cpr") || o.ContainsKey("cprVector"))))
            throw new InvalidOperationException(
                $"Scenario '{scenario}' sets a prepayment speed per asset, derived from its own speed, so --cpr " +
                "cannot replace it. Pick a scenario at the speed you want instead.");
        Set(assumptions, "cpr", cpr);
        Set(assumptions, "cdr", cdr);
        Set(assumptions, "severity", sev);
        Set(assumptions, "delinquency", dq);
    }

    private static void Set(JsonObject assumptions, string key, double? value)
    {
        if (!value.HasValue)
            return;
        assumptions[key] = value.Value;
        assumptions.Remove(key + "Vector");
    }

    private static double Number(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0.0;
}
