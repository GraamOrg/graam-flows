using System.CommandLine;
using System.Diagnostics;
using GraamFlows.Cli.Models;
using GraamFlows.Cli.Services;

namespace GraamFlows.Cli.Commands;

public static class WalTestsCommand
{
    public static Command Create()
    {
        var dealModelArg = new Argument<FileInfo>(
            name: "deal-model",
            description: "Path to the deal model JSON file")
        {
            Arity = ArgumentArity.ExactlyOne
        };

        var outputOption = new Option<FileInfo?>(
            aliases: ["--output", "-o"],
            description: "Output Excel report file path (default: {dealName}_wal_report.xlsx)");

        var thresholdOption = new Option<double>(
            name: "--threshold",
            getDefaultValue: () => 0.10,
            description: "WAL tolerance threshold in years (default: 0.10)");

        var absOption = new Option<double?>(
            name: "--abs",
            description: "Run single ABS% scenario and output full cashflows to Excel");

        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            getDefaultValue: () => false,
            description: "Verbose output");

        var command = new Command("wal-tests", "Validate WAL against prospectus decrement tables")
        {
            dealModelArg,
            outputOption,
            thresholdOption,
            absOption,
            verboseOption
        };

        command.SetHandler(async (context) =>
        {
            var options = new WalTestsOptions
            {
                DealModelFile = context.ParseResult.GetValueForArgument(dealModelArg),
                OutputFile = context.ParseResult.GetValueForOption(outputOption),
                Threshold = context.ParseResult.GetValueForOption(thresholdOption),
                AbsPct = context.ParseResult.GetValueForOption(absOption),
                Verbose = context.ParseResult.GetValueForOption(verboseOption)
            };

            context.ExitCode = await ExecuteAsync(options);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(WalTestsOptions options)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Validate input file exists
            if (!options.DealModelFile.Exists)
            {
                Console.Error.WriteLine($"Error: Deal model file not found: {options.DealModelFile.FullName}");
                return 1;
            }

            if (options.Verbose)
                Console.WriteLine($"Loading deal model: {options.DealModelFile.FullName}");

            // Load deal model
            var loader = new DealModelLoader();
            var dealModel = await loader.LoadAsync(options.DealModelFile.FullName);

            if (options.Verbose)
                Console.WriteLine($"Loaded deal: {dealModel.Deal.DealName} with {dealModel.Deal.Tranches.Count} tranches");

            // If single ABS% specified, run that scenario and output full cashflows
            if (options.AbsPct.HasValue)
            {
                return await ExecuteSingleScenarioAsync(options, dealModel, stopwatch);
            }

            // Check for WAL scenarios
            if (dealModel.WalScenarios == null || dealModel.WalScenarios.Tranches.Count == 0)
            {
                Console.Error.WriteLine("Error: Deal model does not contain walScenarios section");
                return 1;
            }

            var scenarioCount = dealModel.WalScenarios.ToScenarioEntries().Count;
            if (options.Verbose)
                Console.WriteLine($"Found {scenarioCount} WAL scenarios to validate ({dealModel.WalScenarios.Tranches.Count} tranches x {dealModel.WalScenarios.AbsPercentages.Count} ABS%)");

            // Run WAL validation
            var validator = new WalValidator();
            var results = validator.Validate(dealModel, options.Threshold, options.Verbose);

            // Export results
            var outputPath = options.GetOutputPath(dealModel.Deal.DealName);
            var exporter = new ExcelExporter();
            exporter.ExportWalReport(results, dealModel.Deal.DealName, outputPath, options.Threshold);

            // Print summary
            var passCount = results.Count(r => r.Passed);
            var failCount = results.Count(r => !r.Passed);
            var overallRmse = Math.Sqrt(results.Sum(r => r.Error * r.Error) / results.Count);

            stopwatch.Stop();

            Console.WriteLine();
            Console.WriteLine($"WAL Validation Summary for {dealModel.Deal.DealName}");
            Console.WriteLine(new string('-', 50));
            Console.WriteLine($"Scenarios Tested: {results.Count}");
            Console.WriteLine($"Passed: {passCount}");
            Console.WriteLine($"Failed: {failCount}");
            Console.WriteLine($"Overall RMSE: {overallRmse:F4} years");
            Console.WriteLine($"Threshold: {options.Threshold:F2} years");
            Console.WriteLine();
            Console.WriteLine($"Results written to: {outputPath}");
            Console.WriteLine($"Completed in {stopwatch.ElapsedMilliseconds}ms");

            return failCount > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (options.Verbose)
                Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static async Task<int> ExecuteSingleScenarioAsync(WalTestsOptions options, DealModelFile dealModel, Stopwatch stopwatch)
    {
        var absPct = options.AbsPct!.Value;
        var cpr = absPct; // ABS% = CPR for auto ABS

        Console.WriteLine($"Running single scenario: ABS={absPct}%, CPR={cpr}%");

        // Build collateral using all pools for accurate amortization profile
        var collateralBuilder = new CollateralBuilder();
        var assets = collateralBuilder.BuildAssets(dealModel);

        // Apply servicing fee from WAL assumptions to collateral assets
        var servicingFeeRate = dealModel.WalScenarios?.Assumptions?.ServicingFeeRate ?? 0;
        if (servicingFeeRate > 0)
        {
            foreach (var asset in assets)
                asset.ServiceFee = servicingFeeRate;
            if (options.Verbose)
                Console.WriteLine($"Applied servicing fee: {servicingFeeRate}% to {assets.Count} asset(s)");
        }

        // Determine projection date
        var projectionDate = dealModel.ProjectionDate
            ?? dealModel.WalScenarios?.Assumptions?.FirstDistributionDate
            ?? dealModel.Deal.Tranches.FirstOrDefault()?.FirstPayDate
            ?? DateTime.Today;

        if (options.Verbose)
        {
            Console.WriteLine($"Projection date: {projectionDate:yyyy-MM-dd}");
            Console.WriteLine($"Built {assets.Count} collateral assets");
        }

        // Check if clean-up call should be assumed (for WAL calculation)
        var cleanUpCallAssumed = dealModel.WalScenarios?.Assumptions?.CleanUpCallAssumed ?? true;

        // Apply WAL scenario interest rate overrides to match prospectus assumptions
        if (dealModel.WalScenarios?.Assumptions?.InterestRates != null)
        {
            foreach (var rateOverride in dealModel.WalScenarios.Assumptions.InterestRates)
            {
                var tranche = dealModel.Deal.Tranches.FirstOrDefault(t =>
                    t.TrancheName.Equals(rateOverride.TrancheName, StringComparison.OrdinalIgnoreCase));
                if (tranche != null)
                {
                    tranche.FixedCoupon = rateOverride.Rate;
                    if (options.Verbose)
                        Console.WriteLine($"Applied WAL rate override: {tranche.TrancheName} -> {rateOverride.Rate}%");
                }
            }
        }

        // Run waterfall with ABS prepayment convention (prepay as % of original balance)
        var runner = new WaterfallRunner();
        var result = runner.Run(
            dealModel,
            assets,
            projectionDate,
            cpr, // ABS%
            0,   // CDR
            0,   // SEV
            0,   // DQ
            factors: null,
            runToCall: cleanUpCallAssumed,
            useAbsPrepayment: true);

        if (options.Verbose)
        {
            Console.WriteLine($"Waterfall completed: {result.TrancheCashflows.Count} tranches");
            // Print collateral balance trajectory
            if (result.CollateralCashflows != null)
            {
                var collatPeriods = result.CollateralCashflows.PeriodCashflows;
                Console.WriteLine($"  Collateral periods: {collatPeriods.Count}");
                for (var i = 0; i < Math.Min(collatPeriods.Count, 30); i++)
                {
                    var pcf = collatPeriods[i];
                    Console.WriteLine($"    P{i + 1}: EndBal={pcf.Balance:N0}, SchedPrin={pcf.ScheduledPrincipal:N0}, Prepay={pcf.UnscheduledPrincipal:N0}");
                }
            }
        }

        // Export full cashflows to Excel
        var outputPath = options.GetOutputPath(dealModel.Deal.DealName);
        var exporter = new ExcelExporter();
        exporter.Export(result, dealModel, outputPath, cpr, 0, 0, 0);

        stopwatch.Stop();

        // Print WAL summary
        Console.WriteLine();
        Console.WriteLine($"WAL Summary at ABS={absPct}%");
        Console.WriteLine(new string('-', 70));
        Console.WriteLine($"{"Tranche",-15} {"Orig Balance",15} {"Total Prin",15} {"WAL (yrs)",20}");
        Console.WriteLine(new string('-', 70));

        foreach (var tranche in dealModel.Deal.Tranches.OrderBy(t => t.SubordinationOrder))
        {
            if (tranche == null || string.IsNullOrEmpty(tranche.TrancheName))
                continue;

            if (!result.TrancheCashflows.TryGetValue(tranche.TrancheName, out var cashflows) || cashflows == null || cashflows.Count == 0)
                continue;

            var totalPrincipal = cashflows.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal);

            // Use same WAL formula as WalValidator: measure from issuance date (purchaseDate)
            var walIssuanceDate = dealModel.WalScenarios?.Assumptions?.PurchaseDate
                ?? dealModel.ProjectionDate
                ?? cashflows.First().CashflowDate;
            var walDenominator = tranche.OriginalBalance > 0 ? tranche.OriginalBalance : totalPrincipal;

            var walNumerator = cashflows.Sum(c =>
            {
                var principal = c.ScheduledPrincipal + c.UnscheduledPrincipal;
                var yearsFromIssuance = (c.CashflowDate - walIssuanceDate).TotalDays / 365.0;
                return principal * yearsFromIssuance;
            });
            var wal = walDenominator > 0 ? walNumerator / walDenominator : 0;

            // Check expected WAL if available
            var expectedWal = GetExpectedWal(dealModel, tranche.TrancheName, absPct);
            var walStr = expectedWal.HasValue
                ? $"{wal:F2} (exp: {expectedWal.Value:F2})"
                : $"{wal:F2}";

            Console.WriteLine($"{tranche.TrancheName,-15} {tranche.OriginalBalance,15:N0} {totalPrincipal,15:N0} {walStr,20}");
        }

        Console.WriteLine();
        Console.WriteLine($"Results written to: {outputPath}");
        Console.WriteLine($"Completed in {stopwatch.ElapsedMilliseconds}ms");

        return 0;
    }

    private static double? GetExpectedWal(DealModelFile dealModel, string trancheName, double absPct)
    {
        if (dealModel.WalScenarios == null)
            return null;

        var trancheEntry = dealModel.WalScenarios.Tranches
            .FirstOrDefault(t => t.TrancheName?.Equals(trancheName, StringComparison.OrdinalIgnoreCase) == true);

        if (trancheEntry?.WalToCall == null)
            return null;

        // Find the index for this ABS%
        var absIndex = dealModel.WalScenarios.AbsPercentages
            .Select((v, i) => new { Value = v, Index = i })
            .FirstOrDefault(x => Math.Abs(x.Value - absPct) < 0.01);

        if (absIndex == null || absIndex.Index >= trancheEntry.WalToCall.Count)
            return null;

        return trancheEntry.WalToCall[absIndex.Index];
    }
}
