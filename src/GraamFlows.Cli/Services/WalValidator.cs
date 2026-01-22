using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Cli.Services;

public class WalValidationResult
{
    public required string TrancheName { get; set; }
    public double AbsPct { get; set; }
    public double Cpr { get; set; }
    public double ExpectedWal { get; set; }
    public double ComputedWal { get; set; }
    public double Error => Math.Abs(ComputedWal - ExpectedWal);
    public bool Passed { get; set; }
}

public class WalValidator
{
    public List<WalValidationResult> Validate(DealModelFile dealModel, double threshold, bool verbose)
    {
        var results = new List<WalValidationResult>();

        if (dealModel.WalScenarios == null || dealModel.WalScenarios.Tranches.Count == 0)
            return results;

        // Convert the structured WAL scenarios to flat entries
        var scenarios = dealModel.WalScenarios.ToScenarioEntries();
        if (scenarios.Count == 0)
            return results;

        var collateralBuilder = new CollateralBuilder();
        var assets = collateralBuilder.BuildAssets(dealModel);

        // Determine projection date
        var projectionDate = dealModel.ProjectionDate
            ?? dealModel.WalScenarios.Assumptions?.FirstDistributionDate
            ?? dealModel.Deal.Tranches.FirstOrDefault()?.FirstPayDate
            ?? DateTime.Today;

        // Check if clean-up call should be assumed (for WAL calculation)
        var cleanUpCallAssumed = dealModel.WalScenarios.Assumptions?.CleanUpCallAssumed ?? false;

        var runner = new WaterfallRunner();

        // Group scenarios by tranche
        var scenariosByTranche = scenarios
            .GroupBy(s => s.TrancheName ?? "All")
            .ToList();

        foreach (var trancheGroup in scenariosByTranche)
        {
            var trancheName = trancheGroup.Key;

            foreach (var scenario in trancheGroup)
            {
                // Convert ABS% to CPR if not provided
                var cpr = scenario.Cpr ?? ConvertAbsToCpr(scenario.AbsPct);

                if (verbose)
                    Console.WriteLine($"Testing {trancheName} at ABS={scenario.AbsPct:P0}, CPR={cpr:P2}...");

                // Run waterfall with this ABS prepayment speed
                // Note: absPercentages in walScenarios are already in percentage form (e.g., 2.0 = 2%)
                // Use ABS prepayment convention (prepay as % of original balance) for Auto ABS deals
                var result = runner.Run(
                    dealModel,
                    assets,
                    projectionDate,
                    cpr, // Already in percentage form
                    0, // No defaults
                    0, // No severity
                    0, // No delinquency
                    factors: null,
                    runToCall: cleanUpCallAssumed, // Enable clean-up call trigger for WAL calculation
                    useAbsPrepayment: true); // Use ABS prepayment convention for Auto ABS

                // Calculate WAL for the tranche
                double computedWal;
                if (trancheName == "All")
                {
                    // Average WAL across all tranches
                    computedWal = CalculateAverageWal(result, dealModel);
                }
                else
                {
                    computedWal = CalculateTrancheWal(result, trancheName, dealModel, verbose);
                }

                var passed = Math.Abs(computedWal - scenario.ExpectedWal) <= threshold;

                if (verbose)
                    Console.WriteLine($"  Expected={scenario.ExpectedWal:F2}, Computed={computedWal:F2}, Error={Math.Abs(computedWal - scenario.ExpectedWal):F4}, {(passed ? "PASS" : "FAIL")}");

                results.Add(new WalValidationResult
                {
                    TrancheName = trancheName,
                    AbsPct = scenario.AbsPct,
                    Cpr = cpr,
                    ExpectedWal = scenario.ExpectedWal,
                    ComputedWal = computedWal,
                    Passed = passed
                });
            }
        }

        return results;
    }

    private static double ConvertAbsToCpr(double absPct)
    {
        // ABS is typically the annualized prepayment speed
        // For auto ABS, common convention is ABS% = CPR%
        // Some deals use ABS as percentage of original balance
        // Here we assume ABS% directly corresponds to CPR
        return absPct;
    }

    private static double CalculateTrancheWal(WaterfallResult result, string trancheName, DealModelFile? dealModel = null, bool verbose = false)
    {
        // First try direct lookup
        if (result.TrancheCashflows.TryGetValue(trancheName, out var cashflows) && cashflows.Count > 0)
        {
            return CalculateWalFromCashflows(cashflows);
        }

        // If not found, check if it's a combined class (e.g., "A2" for A2A+A2B)
        if (dealModel != null)
        {
            var subTranches = FindSubTranches(trancheName, dealModel.Deal.Tranches);
            if (subTranches.Count >= 2)
            {
                if (verbose)
                    Console.WriteLine($"    (Combined class: {trancheName} -> {string.Join("+", subTranches.Select(t => t.TrancheName))})");

                return CalculateCombinedTrancheWal(result, subTranches);
            }
        }

        return 0;
    }

    private static double CalculateWalFromCashflows(List<TrancheCashflowDto> cashflows)
    {
        if (cashflows.Count == 0)
            return 0;

        var firstPayDate = cashflows.First().CashflowDate;
        var totalPrincipal = cashflows.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal);

        if (totalPrincipal <= 0)
            return 0;

        var walNumerator = cashflows.Sum(c =>
        {
            var principal = c.ScheduledPrincipal + c.UnscheduledPrincipal;
            var yearsFromStart = (c.CashflowDate - firstPayDate).TotalDays / 365.25;
            return principal * yearsFromStart;
        });

        return walNumerator / totalPrincipal;
    }

    /// <summary>
    /// Find sub-tranches for a combined class name.
    /// E.g., "A2" matches "A2A", "A2B" but not "A2" itself.
    /// </summary>
    private static List<TrancheDto> FindSubTranches(string combinedName, List<TrancheDto> allTranches)
    {
        return allTranches
            .Where(t => t.TrancheName.StartsWith(combinedName) && t.TrancheName.Length > combinedName.Length)
            .ToList();
    }

    /// <summary>
    /// Calculate weighted average WAL for a combined class by combining sub-tranche cashflows.
    /// </summary>
    private static double CalculateCombinedTrancheWal(WaterfallResult result, List<TrancheDto> subTranches)
    {
        var totalBalance = 0.0;
        var weightedWalNumerator = 0.0;

        foreach (var tranche in subTranches)
        {
            if (!result.TrancheCashflows.TryGetValue(tranche.TrancheName, out var cashflows) || cashflows.Count == 0)
                continue;

            var trancheBalance = tranche.OriginalBalance;
            var trancheWal = CalculateWalFromCashflows(cashflows);

            totalBalance += trancheBalance;
            weightedWalNumerator += trancheBalance * trancheWal;
        }

        return totalBalance > 0 ? weightedWalNumerator / totalBalance : 0;
    }

    private static double CalculateAverageWal(WaterfallResult result, DealModelFile dealModel)
    {
        if (result.TrancheCashflows.Count == 0)
            return 0;

        var totalBalance = 0.0;
        var weightedWal = 0.0;

        foreach (var tranche in dealModel.Deal.Tranches)
        {
            if (!result.TrancheCashflows.TryGetValue(tranche.TrancheName, out var cashflows))
                continue;

            var trancheBalance = tranche.OriginalBalance;
            var trancheWal = CalculateWalFromCashflows(cashflows);

            totalBalance += trancheBalance;
            weightedWal += trancheBalance * trancheWal;
        }

        return totalBalance > 0 ? weightedWal / totalBalance : 0;
    }
}
