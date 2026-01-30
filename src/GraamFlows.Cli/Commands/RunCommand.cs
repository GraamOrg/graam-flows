using System.CommandLine;
using System.Diagnostics;
using GraamFlows.Cli.Models;
using GraamFlows.Cli.Services;

namespace GraamFlows.Cli.Commands;

public static class RunCommand
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
            description: "Output Excel file path (default: {dealName}_results.xlsx)");

        var cprOption = new Option<double>(
            aliases: ["--cpr", "-p"],
            getDefaultValue: () => 0.0,
            description: "Prepayment rate (annual %)");

        var cdrOption = new Option<double>(
            aliases: ["--cdr", "-d"],
            getDefaultValue: () => 0.0,
            description: "Default rate (annual %)");

        var sevOption = new Option<double>(
            aliases: ["--sev", "-s"],
            getDefaultValue: () => 0.0,
            description: "Loss severity (%)");

        var dqOption = new Option<double>(
            name: "--dq",
            getDefaultValue: () => 0.0,
            description: "Delinquency rate (%)");

        var projectionDateOption = new Option<DateTime?>(
            name: "--projection-date",
            description: "Projection start date (default: first pay date from deal)");

        var factorsOption = new Option<FileInfo?>(
            aliases: ["--factors", "-f"],
            description: "Tranche factors JSON file");

        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            getDefaultValue: () => false,
            description: "Verbose output");

        var collatBalOption = new Option<double?>(
            name: "--collat-bal",
            description: "Override collateral balance ($)");

        var wacOption = new Option<double?>(
            name: "--wac",
            description: "Override weighted average coupon (%)");

        var termOption = new Option<int?>(
            name: "--term",
            description: "Override collateral term (months)");

        var command = new Command("run", "Run waterfall execution on a deal model")
        {
            dealModelArg,
            outputOption,
            cprOption,
            cdrOption,
            sevOption,
            dqOption,
            projectionDateOption,
            factorsOption,
            verboseOption,
            collatBalOption,
            wacOption,
            termOption
        };

        command.SetHandler(async (context) =>
        {
            var options = new RunOptions
            {
                DealModelFile = context.ParseResult.GetValueForArgument(dealModelArg),
                OutputFile = context.ParseResult.GetValueForOption(outputOption),
                Cpr = context.ParseResult.GetValueForOption(cprOption),
                Cdr = context.ParseResult.GetValueForOption(cdrOption),
                Sev = context.ParseResult.GetValueForOption(sevOption),
                Dq = context.ParseResult.GetValueForOption(dqOption),
                ProjectionDate = context.ParseResult.GetValueForOption(projectionDateOption),
                FactorsFile = context.ParseResult.GetValueForOption(factorsOption),
                Verbose = context.ParseResult.GetValueForOption(verboseOption),
                CollatBal = context.ParseResult.GetValueForOption(collatBalOption),
                Wac = context.ParseResult.GetValueForOption(wacOption),
                Term = context.ParseResult.GetValueForOption(termOption)
            };

            context.ExitCode = await ExecuteAsync(options);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(RunOptions options)
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
            {
                Console.WriteLine($"Loading deal model: {options.DealModelFile.FullName}");
                Console.WriteLine($"Assumptions: CPR={options.Cpr}%, CDR={options.Cdr}%, SEV={options.Sev}%, DQ={options.Dq}%");
            }

            // Load deal model
            var loader = new DealModelLoader();
            var dealModel = await loader.LoadAsync(options.DealModelFile.FullName);

            if (options.Verbose)
            {
                Console.WriteLine($"Loaded deal: {dealModel.Deal.DealName} with {dealModel.Deal.Tranches.Count} tranches");
                Console.WriteLine($"UnifiedWaterfall steps: {dealModel.Deal.UnifiedWaterfall?.Steps?.Count ?? 0}");
                Console.WriteLine($"PayRules: {dealModel.Deal.PayRules?.Count ?? 0}");
            }

            // Load factors if provided
            Dictionary<string, GraamFlows.Api.Models.FactorEntry>? factors = null;
            if (options.FactorsFile != null && options.FactorsFile.Exists)
            {
                factors = await loader.LoadFactorsAsync(options.FactorsFile.FullName);
                if (options.Verbose)
                    Console.WriteLine($"Loaded {factors.Count} tranche factors");
            }

            // Determine projection date
            var projectionDate = options.ProjectionDate
                ?? dealModel.Deal.Tranches.FirstOrDefault()?.FirstPayDate
                ?? DateTime.Today;

            if (options.Verbose)
                Console.WriteLine($"Projection date: {projectionDate:yyyy-MM-dd}");

            // Build collateral
            var collateralBuilder = new CollateralBuilder();
            var assets = collateralBuilder.BuildAssets(dealModel);

            if (options.Verbose)
                Console.WriteLine($"Built {assets.Count} collateral assets");

            // Apply collateral overrides from CLI
            if (options.CollatBal.HasValue || options.Wac.HasValue || options.Term.HasValue)
            {
                foreach (var asset in assets)
                {
                    if (options.CollatBal.HasValue)
                    {
                        asset.CurrentBalance = options.CollatBal.Value;
                        asset.BalanceAtIssuance = options.CollatBal.Value;
                        asset.OriginalBalance = options.CollatBal.Value;
                    }

                    if (options.Wac.HasValue)
                    {
                        asset.CurrentInterestRate = options.Wac.Value;
                        asset.OriginalInterestRate = options.Wac.Value;
                    }

                    if (options.Term.HasValue)
                        asset.OriginalAmortizationTerm = options.Term.Value;
                }

                if (options.Verbose)
                {
                    Console.WriteLine($"Collateral overrides applied:"
                        + (options.CollatBal.HasValue ? $" balance=${options.CollatBal.Value:N0}" : "")
                        + (options.Wac.HasValue ? $" wac={options.Wac.Value}%" : "")
                        + (options.Term.HasValue ? $" term={options.Term.Value}mo" : ""));
                }
            }

            // Run waterfall
            var runner = new WaterfallRunner();
            var result = runner.Run(
                dealModel,
                assets,
                projectionDate,
                options.Cpr,
                options.Cdr,
                options.Sev,
                options.Dq,
                factors);

            if (options.Verbose)
                Console.WriteLine($"Waterfall completed: {result.TrancheCashflows.Count} tranches");

            // Export to Excel
            var outputPath = options.GetOutputPath(dealModel.Deal.DealName);
            var exporter = new ExcelExporter();
            exporter.Export(result, dealModel, outputPath, options.Cpr, options.Cdr, options.Sev, options.Dq);

            stopwatch.Stop();
            Console.WriteLine($"Results written to: {outputPath}");
            Console.WriteLine($"Completed in {stopwatch.ElapsedMilliseconds}ms");

            return 0;
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
}
