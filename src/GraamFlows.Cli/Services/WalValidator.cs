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
        var projectionDate = ResolveProjectionDate(dealModel);

        // Check if the optional redemption should be assumed exercised (for WAL calculation)
        var cleanUpCallAssumed = dealModel.WalScenarios.Assumptions?.CleanUpCallAssumed ?? false;
        var callTriggerNames = ResolveCallTriggerNames(dealModel);
        var useAbsPrepayment = ResolveUseAbsPrepayment(dealModel.WalScenarios.Assumptions);

        if (verbose)
        {
            Console.WriteLine($"  Prepayment convention: {(useAbsPrepayment ? "ABS" : "CPR")}");
            Console.WriteLine(cleanUpCallAssumed
                ? $"  Call basis: {DescribeCallBasis(callTriggerNames)}"
                : "  Call basis: none (to maturity)");
        }

        // Get issuance date for WAL calculation (prospectus WAL is measured from issuance, not first payment)
        var issuanceDate = dealModel.WalScenarios.Assumptions?.PurchaseDate ?? projectionDate;

        var firstDistDate = dealModel.WalScenarios.Assumptions?.FirstDistributionDate
            ?? CollateralBuilder.GetFirstPayDate(dealModel);

        // Unadjusted payment day for the WAL calculation. Prosup WAL tables use contractual
        // payment dates (e.g. the 25th), not business-day-adjusted ones. The declared
        // paymentDayOfMonth used to be read into a local that nothing forwarded; absent it, the
        // day still comes from the first distribution date, exactly as before.
        var paymentDay = dealModel.WalScenarios.Assumptions?.PaymentDayOfMonth ?? firstDistDate.Day;

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

                // Speeds are plain percents (5.0 = 5%), so format them as such. `:P0`/`:P2` is
                // .NET percent format, which multiplies by 100 and printed a 5.0 CPR as
                // "500.00%" — a scale bug in the log that was not one in the values.
                if (verbose)
                    Console.WriteLine($"Testing {trancheName} at speed={scenario.AbsPct:F1}%, CPR={cpr:F2}%...");

                // Run the waterfall at this prepayment speed, in the convention the deal declares.
                // Note: absPercentages in walScenarios are already in percentage form (e.g., 2.0 = 2%)
                var result = runner.Run(
                    dealModel,
                    assets,
                    projectionDate,
                    cpr, // Already in percentage form
                    0, // No defaults
                    0, // No severity
                    0, // No delinquency
                    factors: null,
                    runToCall: cleanUpCallAssumed, // Assume the optional redemption is exercised
                    useAbsPrepayment: useAbsPrepayment,
                    callTriggerNames: callTriggerNames);

                // Calculate WAL for the tranche using unadjusted payment dates
                double computedWal;
                if (trancheName == "All")
                {
                    // Average WAL across all tranches
                    computedWal = CalculateAverageWal(result, dealModel, issuanceDate, paymentDay);
                }
                else
                {
                    computedWal = CalculateTrancheWal(result, trancheName, dealModel, issuanceDate, paymentDay, verbose);
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

    /// <summary>
    ///     The date the projection is struck from. Every leg resolves from the deal — there is no
    ///     <see cref="DateTime.Today" /> fallback, so two runs of the same deal on different days
    ///     produce the same answer (graam-flows#88).
    /// </summary>
    public static DateTime ResolveProjectionDate(DealModelFile dealModel)
    {
        return dealModel.ProjectionDate
               ?? dealModel.WalScenarios?.Assumptions?.FirstDistributionDate
               ?? CollateralBuilder.GetFirstPayDate(dealModel);
    }

    /// <summary>
    ///     Which prepayment convention the published decrement table is struck in.
    ///     ABS is % of the ORIGINAL balance per month (the auto-ABS convention); CPR is % of the
    ///     CURRENT balance. This used to be hard-coded to ABS, which retired an RMBS pool in ~20
    ///     months at speed "5" and collapsed every non-zero column to ~0 (graam-flows#88).
    /// </summary>
    public static bool ResolveUseAbsPrepayment(WalAssumptions? assumptions)
    {
        var declared = assumptions?.PrepaymentType?.Trim();
        if (!string.IsNullOrEmpty(declared))
        {
            if (declared.Equals("ABS", StringComparison.OrdinalIgnoreCase))
                return true;
            if (declared.Equals("CPR", StringComparison.OrdinalIgnoreCase))
                return false;

            // Do not quietly pick a convention for a value we do not understand: the whole point
            // of this defect was a convention nobody had stated being applied anyway.
            throw new InvalidOperationException(
                $"Unrecognised walScenarios.assumptions.prepaymentType '{declared}'. Expected \"CPR\" or \"ABS\".");
        }

        // A stated CPR pricing speed is itself the deal saying the grid is a CPR grid.
        // Silence means CPR, the prospectus default.
        return false;
    }

    /// <summary>
    ///     The triggers the "to redemption date" column is struck to, in declaration order.
    ///     The run terminates at whichever of them fires first.
    /// </summary>
    /// <remarks>
    ///     A deal can declare more than one redemption: an NQM deal can carry both a dated
    ///     step-up redemption (DATE_TERMINATION, 2029-04) and a balance clean-up
    ///     (COLLATERAL_VALUE, 30%). Its published table is struck to the DATED one — the
    ///     subordinate classes sit at 3.95y at every speed from 0% to 40% CPR, which only a dated
    ///     call produces; a balance call would walk them down to ~2.4y at 40% CPR. Measured on
    ///     that deal: dated-only ties all 63 published points (RMSE 0.043y); engaging both, as
    ///     the run did before, misses 56 of 63 (RMSE 2.70y), and the clean-up alone misses all 63
    ///     (RMSE 3.02y).
    ///
    ///     So a dated redemption, when the deal declares one, wins over the clean-up. Auto-ABS
    ///     deals declare only the clean-up and are unaffected. A deal whose table is struck to
    ///     something else states it in <c>walScenarios.assumptions.callTriggers</c>, which is
    ///     also how the earliest-of-both basis is asked for.
    /// </remarks>
    public static List<string> ResolveCallTriggerNames(DealModelFile dealModel)
    {
        var declared = dealModel.WalScenarios?.Assumptions?.CallTriggers;
        if (declared is { Count: > 0 })
            return declared
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var named = (dealModel.Deal.Triggers ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.TriggerName))
            .ToList();

        var datedRedemptions = Names(named.Where(t => t.TriggerType == "DATE_TERMINATION"));
        if (datedRedemptions.Count > 0)
            return datedRedemptions;

        return Names(named.Where(t => t.TriggerType == "COLLATERAL_VALUE"));

        static List<string> Names(IEnumerable<TriggerDto> triggers) =>
            triggers.Select(t => t.TriggerName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Human-readable description of the call basis a run is struck to.</summary>
    public static string DescribeCallBasis(IReadOnlyCollection<string> callTriggerNames)
    {
        if (callTriggerNames.Count == 0)
            return "every optional trigger the deal declares";
        return callTriggerNames.Count == 1
            ? callTriggerNames.First()
            : $"earliest of [{string.Join(", ", callTriggerNames)}]";
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
        DateTime issuanceDate, int paymentDay, bool verbose = false)
    {
        // First try direct lookup
        if (result.TrancheCashflows.TryGetValue(trancheName, out var cashflows) && cashflows.Count > 0)
        {
            // Get initial balance for the tranche (use as denominator per prospectus WAL definition)
            var initialBalance = dealModel?.Deal.Tranches
                .FirstOrDefault(t => t.TrancheName == trancheName)?.OriginalBalance ?? 0;
            return CalculateWalFromCashflows(cashflows, issuanceDate, paymentDay, initialBalance);
        }

        // If not found, check if it's a combined class (e.g., "A2" for A2A+A2B)
        if (dealModel != null)
        {
            var subTranches = FindSubTranches(trancheName, dealModel.Deal.Tranches);
            if (subTranches.Count >= 2)
            {
                if (verbose)
                    Console.WriteLine($"    (Combined class: {trancheName} -> {string.Join("+", subTranches.Select(t => t.TrancheName))})");

                return CalculateCombinedTrancheWal(result, subTranches, issuanceDate, paymentDay);
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
        int paymentDay, double initialBalance = 0)
    {
        if (cashflows.Count == 0)
            return 0;

        var totalPrincipal = cashflows.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal);

        // Use initial balance if provided, otherwise fall back to total principal
        var denominator = initialBalance > 0 ? initialBalance : totalPrincipal;
        if (denominator <= 0)
            return 0;

        // Use unadjusted payment dates for WAL calculation.
        // The prosup WAL table uses contractual dates (e.g., 25th of each month),
        // not the business-day-adjusted dates from the waterfall engine.
        // Map each cashflow to the contractual date in the same month.
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
        DateTime issuanceDate, int paymentDay)
    {
        var totalBalance = 0.0;
        var weightedWalNumerator = 0.0;

        foreach (var tranche in subTranches)
        {
            if (!result.TrancheCashflows.TryGetValue(tranche.TrancheName, out var cashflows) || cashflows.Count == 0)
                continue;

            var trancheBalance = tranche.OriginalBalance;
            var trancheWal = CalculateWalFromCashflows(cashflows, issuanceDate, paymentDay, trancheBalance);

            totalBalance += trancheBalance;
            weightedWalNumerator += trancheBalance * trancheWal;
        }

        return totalBalance > 0 ? weightedWalNumerator / totalBalance : 0;
    }

    private static double CalculateAverageWal(WaterfallResult result, DealModelFile dealModel,
        DateTime issuanceDate, int paymentDay)
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
            var trancheWal = CalculateWalFromCashflows(cashflows, issuanceDate, paymentDay, trancheBalance);

            totalBalance += trancheBalance;
            weightedWal += trancheBalance * trancheWal;
        }

        return totalBalance > 0 ? weightedWal / totalBalance : 0;
    }
}
