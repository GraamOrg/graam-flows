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
    public List<WalValidationResult> Validate(DealModelFile dealModel, double threshold, bool verbose, bool useBalanceModel = false)
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

        // Apply servicing fee from WAL assumptions to collateral assets
        // The prospectus WAL tables assume a specific servicing fee rate that reduces net interest
        var servicingFeeRate = dealModel.WalScenarios.Assumptions?.ServicingFeeRate ?? 0;
        if (servicingFeeRate > 0)
        {
            foreach (var asset in assets)
                asset.ServiceFee = servicingFeeRate;
        }

        if (verbose)
        {
            Console.WriteLine($"  Built {assets.Count} collateral asset(s)");
            Console.WriteLine($"  Pool stratification pools: {dealModel.PoolStratification?.Pools?.Count ?? 0}");
            foreach (var a in assets)
                Console.WriteLine($"    {a.AssetId}: bal={a.CurrentBalance:N2}, rate={a.CurrentInterestRate:F3}%, term={a.OriginalAmortizationTerm}, origDate={a.OriginalDate:yyyy-MM-dd}");
        }

        // Determine projection date
        var projectionDate = dealModel.ProjectionDate
            ?? dealModel.WalScenarios.Assumptions?.FirstDistributionDate
            ?? dealModel.Deal.Tranches.FirstOrDefault()?.FirstPayDate
            ?? DateTime.Today;

        // Check if clean-up call should be assumed (for WAL calculation)
        var cleanUpCallAssumed = dealModel.WalScenarios.Assumptions?.CleanUpCallAssumed ?? false;

        // Get issuance date for WAL calculation (prospectus WAL is measured from issuance, not first payment)
        var issuanceDate = dealModel.WalScenarios.Assumptions?.PurchaseDate ?? projectionDate;

        // Get unadjusted payment day for WAL calculation
        // Prosup WAL tables use contractual payment dates (e.g., 15th), not business-day-adjusted dates
        var paymentDay = dealModel.WalScenarios.Assumptions?.PaymentDayOfMonth
            ?? dealModel.Deal.Tranches.FirstOrDefault()?.PayDay
            ?? 15;
        var firstDistDate = dealModel.WalScenarios.Assumptions?.FirstDistributionDate
            ?? dealModel.Deal.Tranches.FirstOrDefault()?.FirstPayDate
            ?? projectionDate;

        // Apply WAL scenario interest rate overrides if provided
        // The prospectus WAL tables may use different rates than the actual note coupons
        var originalRates = new Dictionary<string, double?>();
        if (dealModel.WalScenarios.Assumptions?.InterestRates != null)
        {
            foreach (var rateOverride in dealModel.WalScenarios.Assumptions.InterestRates)
            {
                var tranche = dealModel.Deal.Tranches.FirstOrDefault(t =>
                    t.TrancheName.Equals(rateOverride.TrancheName, StringComparison.OrdinalIgnoreCase));
                if (tranche != null)
                {
                    originalRates[tranche.TrancheName] = tranche.FixedCoupon;
                    tranche.FixedCoupon = rateOverride.Rate;
                    if (verbose)
                        Console.WriteLine($"  Applied WAL rate override: {tranche.TrancheName} -> {rateOverride.Rate}%");
                }
            }
        }

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

                // Calculate WAL for the tranche using unadjusted payment dates
                double computedWal;
                if (trancheName == "All")
                {
                    // Average WAL across all tranches
                    computedWal = CalculateAverageWal(result, dealModel, issuanceDate, firstDistDate);
                }
                else
                {
                    computedWal = CalculateTrancheWal(result, trancheName, dealModel, issuanceDate, firstDistDate, verbose);
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

        // Restore original interest rates
        foreach (var (trancheName, originalRate) in originalRates)
        {
            var tranche = dealModel.Deal.Tranches.FirstOrDefault(t => t.TrancheName == trancheName);
            if (tranche != null)
                tranche.FixedCoupon = originalRate;
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

    private static double CalculateTrancheWal(WaterfallResult result, string trancheName, DealModelFile? dealModel,
        DateTime issuanceDate, DateTime firstDistDate, bool verbose = false)
    {
        // First try direct lookup
        if (result.TrancheCashflows.TryGetValue(trancheName, out var cashflows) && cashflows.Count > 0)
        {
            // Get initial balance for the tranche (use as denominator per prospectus WAL definition)
            var initialBalance = dealModel?.Deal.Tranches
                .FirstOrDefault(t => t.TrancheName == trancheName)?.OriginalBalance ?? 0;
            return CalculateWalFromCashflows(cashflows, issuanceDate, firstDistDate, initialBalance);
        }

        // If not found, check if it's a combined class (e.g., "A2" for A2A+A2B)
        if (dealModel != null)
        {
            var subTranches = FindSubTranches(trancheName, dealModel.Deal.Tranches);
            if (subTranches.Count >= 2)
            {
                if (verbose)
                    Console.WriteLine($"    (Combined class: {trancheName} -> {string.Join("+", subTranches.Select(t => t.TrancheName))})");

                return CalculateCombinedTrancheWal(result, subTranches, issuanceDate, firstDistDate);
            }
        }

        return 0;
    }

    /// <summary>
    /// Calculate WAL from cashflows using the prospectus definition:
    /// WAL = sum(principal * years_from_issuance) / initial_balance
    ///
    /// Uses unadjusted payment dates (contractual schedule) rather than business-day-adjusted dates
    /// to match the prosup WAL table methodology.
    /// </summary>
    private static double CalculateWalFromCashflows(List<TrancheCashflowDto> cashflows, DateTime issuanceDate,
        DateTime firstDistDate, double initialBalance = 0)
    {
        if (cashflows.Count == 0)
            return 0;

        var totalPrincipal = cashflows.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal);

        // Use initial balance if provided, otherwise fall back to total principal
        var denominator = initialBalance > 0 ? initialBalance : totalPrincipal;
        if (denominator <= 0)
            return 0;

        // Use unadjusted payment dates for WAL calculation.
        // The prosup WAL table uses contractual dates (e.g., 15th of each month),
        // not the business-day-adjusted dates from the waterfall engine.
        // Map each cashflow to the contractual date in the same month.
        var paymentDay = firstDistDate.Day;
        var walNumerator = 0.0;
        foreach (var c in cashflows)
        {
            var principal = c.ScheduledPrincipal + c.UnscheduledPrincipal;
            if (Math.Abs(principal) < 0.01)
                continue;

            // Map to contractual payment date (same year/month, unadjusted day)
            var daysInMonth = DateTime.DaysInMonth(c.CashflowDate.Year, c.CashflowDate.Month);
            var day = Math.Min(paymentDay, daysInMonth);
            var unadjustedDate = new DateTime(c.CashflowDate.Year, c.CashflowDate.Month, day);
            var yearsFromIssuance = (unadjustedDate - issuanceDate).TotalDays / 365.0;
            walNumerator += principal * yearsFromIssuance;
        }

        return walNumerator / denominator;
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
    private static double CalculateCombinedTrancheWal(WaterfallResult result, List<TrancheDto> subTranches,
        DateTime issuanceDate, DateTime firstDistDate)
    {
        var totalBalance = 0.0;
        var weightedWalNumerator = 0.0;

        foreach (var tranche in subTranches)
        {
            if (!result.TrancheCashflows.TryGetValue(tranche.TrancheName, out var cashflows) || cashflows.Count == 0)
                continue;

            var trancheBalance = tranche.OriginalBalance;
            var trancheWal = CalculateWalFromCashflows(cashflows, issuanceDate, firstDistDate, trancheBalance);

            totalBalance += trancheBalance;
            weightedWalNumerator += trancheBalance * trancheWal;
        }

        return totalBalance > 0 ? weightedWalNumerator / totalBalance : 0;
    }

    private static double CalculateAverageWal(WaterfallResult result, DealModelFile dealModel,
        DateTime issuanceDate, DateTime firstDistDate)
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
            var trancheWal = CalculateWalFromCashflows(cashflows, issuanceDate, firstDistDate, trancheBalance);

            totalBalance += trancheBalance;
            weightedWal += trancheBalance * trancheWal;
        }

        return totalBalance > 0 ? weightedWal / totalBalance : 0;
    }
}
