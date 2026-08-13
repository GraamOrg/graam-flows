using GraamFlows.AssetCashflowEngine;
using GraamFlows.Domain;
using GraamFlows.Factories;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using GraamFlows.RulesEngine;
using GraamFlows.Util;
using GraamFlows.Waterfall.MarketTranche;
using GraamFlows.Waterfall.Structures;
using Task = System.Threading.Tasks.Task;

namespace GraamFlows;

public class CfCore
{
    public CfCore(DateTime firstProjectionDate, IDeal deal)
    {
        FirstProjectionDate = firstProjectionDate;
        Deal = deal;
        CashflowEngine = WaterfallFactory.GetWaterfall(deal.CashflowEngine);
    }

    public IWaterfall CashflowEngine { get; }

    public IDeal Deal { get; }
    public DateTime FirstProjectionDate { get; }

    public CollateralCashflows GenerateAssetCashflows(IRateProvider rateProvider, IAssumptionMill assumps)
    {
        // Extract pool age offset and WAM if available (for ABS prepayment calculation)
        var dealAssumps = assumps as Assumptions.DealLevelAssumptions;
        var poolAgeOffset = dealAssumps?.PoolAgeOffset ?? 0;
        var wam = dealAssumps?.WeightedAverageRemainingTerm ?? 0;

        var dealCashflows = GenerateAssetCashflows(Deal.Assets, FirstProjectionDate,
            g => Deal.DealTriggers.EarliestMandatoryDateRedemption(g),
            assumps.GetAssumptionsForAsset, rateProvider, assumps.Threads, assumps.DisplayAssetCashflows,
            poolAgeOffset, wam);

        // Revolving / reinvesting collateral pool (graam-flows#49). Additive:
        // only runs when the deal carries a ReinvestmentConfig, so the static
        // (non-reinvesting) path is untouched. Reinvested collateral reuses the
        // deal's assumptions (resolved off a sample template asset).
        if (Deal.ReinvestmentConfig is { } reinvestCfg && reinvestCfg.Templates.Count > 0)
        {
            var sampleAsset = BuildReinvestAsset(reinvestCfg.Templates[0], 1.0, FirstProjectionDate, rateProvider, 0);
            var reinvestAssumps = assumps.GetAssumptionsForAsset(sampleAsset);
            var basePool = dealCashflows.PeriodCashflows.ToList();
            foreach (var cf in BuildReinvestmentCashflows(
                         basePool, reinvestCfg, FirstProjectionDate, reinvestAssumps, rateProvider))
                dealCashflows.AddPeriodCashflow(cf);
        }

        // check if the deal pay rules have been compiled
        Task ruleCompileTask = null;
        if (Deal.RuleAssembly == null)
            lock (Deal)
            {
                if (Deal.RuleAssembly == null) ruleCompileTask = Task.Factory.StartNew(() => CompileRules(Deal));
            }

        ruleCompileTask?.Wait();
        return dealCashflows;
    }

    public static CollateralCashflows GenerateAssetCashflows(IList<IAsset> assets, DateTime firstProjDate,
        Func<string, DateTime> redempDateFunc,
        Func<IAsset, IAssetAssumptions> assumpFunc, IRateProvider rateProvider, int threads = 1,
        bool displayAssetCf = false, int poolAgeOffset = 0, int wam = 0)
    {
        var groupedAssets = assets.GroupBy(asset => asset.GroupNum);
        var dealCashflows = new CollateralCashflows(displayAssetCf);
        var startTime = DateUtil.CalcAbsT(firstProjDate);

        foreach (var group in groupedAssets)
        {
            var groupNum = group.Key;
            var groupAssets = group.ToList();
            var endDate = redempDateFunc?.Invoke(groupNum) ?? firstProjDate.AddYears(50);
            var endTime = DateUtil.CalcAbsT(endDate);
            var maxPeriods = Math.Min(endTime - startTime + 1, 720);

            // Convert assets to parallel arrays
            var assetData = new AssetDataArrays(groupAssets);

            // Per-asset assumption resolution (graam-flows#5). Previously this
            // code called assumpFunc only on the first asset and applied those
            // assumptions to every asset in the group. The IAssumptionMill
            // abstraction was already keyed by asset, but the engine was
            // discarding the per-asset signal before the amortizer saw it.
            // Now we resolve each asset's IAssetAssumptions independently and
            // build a per-asset row in each rate matrix. Uniform-per-group
            // callers still work transparently: every row ends up identical
            // (and BuildAssumptionArray is cheap).
            var assetAssumps = new IAssetAssumptions[groupAssets.Count];
            for (var i = 0; i < groupAssets.Count; i++)
                assetAssumps[i] = assumpFunc?.Invoke(groupAssets[i]);

            // PrepaymentType (ABS-vs-SMM-vs-CPR conversion path) and DefaultType
            // (CDR de-annualize vs. MDR direct monthly hazard) are resolved
            // per-asset inside the loop below, off each asset's own
            // IAssetAssumptions. Previously these were resolved once from the
            // first asset and applied to the whole group, which silently
            // mis-converted any asset whose mode differed from asset[0]'s
            // (graam-flows#15). The per-asset loop already exists (graam-flows#5),
            // so per-asset resolution is the natural home. harmony #1226.

            // Build per-asset assumption matrices. Each is jagged double[][]:
            // outer index is asset (aligned to groupAssets / assetData),
            // inner index is period. For ABS prepayment type, use the
            // time-varying ABS-to-SMM conversion (formula docs above on
            // BuildAbsAssumptionArray). All other rate types use the standard
            // annual→monthly conversion.
            var assetCount = groupAssets.Count;
            var smmTime = new double[assetCount][];
            var mdrTime = new double[assetCount][];
            var sevTime = new double[assetCount][];
            var delTime = new double[assetCount][];
            var delAdvIntTime = new double[assetCount][];
            var delAdvPrinTime = new double[assetCount][];
            var forbRecovPpayTime = new double[assetCount][];
            var forbRecovMaturityTime = new double[assetCount][];
            var forbRecovDefaultTime = new double[assetCount][];

            // Recovery lag (months) per asset — recoveries land at t + lag
            // (graam-harmony #3449). Left null when every asset is zero-lag so the
            // amortizer keeps the same-period fast path.
            int[]? recoveryLag = null;

            // ORIGMDR default series (monthly fraction of ORIGINAL balance),
            // per-asset. Allocated lazily so groups with no ORIGMDR asset keep
            // the null fast-path in the amortizer. Elements stay null for
            // assets using CDR/MDR.
            double[][]? origMdrTime = null;

            for (var i = 0; i < assetCount; i++)
            {
                var aa = assetAssumps[i];

                // Resolve each asset's prepay/default mode from its own
                // assumptions so a heterogeneous group converts each asset
                // correctly (graam-flows#15).
                var prepaymentType = aa?.PrepaymentType ?? Objects.TypeEnum.PrepaymentTypeEnum.CPR;
                var defaultType = aa?.DefaultType ?? Objects.TypeEnum.DefaultTypeEnum.CDR;

                // Prepay: ABS uses the time-varying ABS→SMM conversion; SMM is a
                // direct monthly hazard (no de-annualization, harmony #1226);
                // CPR/PercentCPR/PSA de-annualize the annual rate as before.
                smmTime[i] = prepaymentType switch
                {
                    Objects.TypeEnum.PrepaymentTypeEnum.ABS =>
                        BuildAbsAssumptionArray(aa?.Prepayment, maxPeriods, startTime, poolAgeOffset, wam),
                    Objects.TypeEnum.PrepaymentTypeEnum.SMM =>
                        BuildAssumptionArray(aa?.Prepayment, maxPeriods, startTime, false),
                    _ => BuildAssumptionArray(aa?.Prepayment, maxPeriods, startTime, true)
                };
                // Default: MDR is a direct monthly hazard (no de-annualization,
                // harmony #1226); CDR de-annualizes the annual rate as before.
                // ORIGMDR is a monthly rate on the ORIGINAL balance — also
                // already-monthly (no de-annualization), but applied against
                // original (not current) balance inside the amortizer, so it
                // travels in its own series and mdrTime is zeroed for the asset.
                if (defaultType == Objects.TypeEnum.DefaultTypeEnum.ORIGMDR)
                {
                    origMdrTime ??= new double[assetCount][];
                    origMdrTime[i] = BuildAssumptionArray(aa?.DefaultRate, maxPeriods, startTime, false);
                    mdrTime[i] = new double[maxPeriods];
                }
                else
                {
                    mdrTime[i] = BuildAssumptionArray(aa?.DefaultRate, maxPeriods, startTime,
                        defaultType != Objects.TypeEnum.DefaultTypeEnum.MDR);
                }
                sevTime[i] = BuildAssumptionArray(aa?.Severity, maxPeriods, startTime, false, 100.0);
                var assetLag = aa?.RecoveryLag ?? 0;
                if (assetLag > 0)
                {
                    recoveryLag ??= new int[assetCount];
                    recoveryLag[i] = assetLag;
                }
                delTime[i] = BuildAssumptionArray(aa?.DelinqRate, maxPeriods, startTime, false, 100.0);
                delAdvIntTime[i] = BuildAssumptionArray(aa?.DelinqAdvPctInt, maxPeriods, startTime, false, 1.0, 100.0);
                delAdvPrinTime[i] = BuildAssumptionArray(aa?.DelinqAdvPctPrin, maxPeriods, startTime, false, 1.0, 100.0);
                forbRecovPpayTime[i] = BuildForbearanceArray(aa?.ForbearanceRecoveryPrepay, maxPeriods, startTime, -1.0);
                forbRecovMaturityTime[i] = BuildForbearanceArray(aa?.ForbearanceRecoveryMaturity, maxPeriods, startTime, 1.0);
                forbRecovDefaultTime[i] = BuildForbearanceArray(aa?.ForbearanceRecoveryDefault, maxPeriods, startTime, -1.0);
            }

            // Build market rate arrays
            var allMarketRates = BuildMarketRateArrays(rateProvider, firstProjDate, maxPeriods);

            // Run the high-performance cashflow generator
            var results = Amortizer.GenerateCashflows(
                assetData,
                startTime,
                endTime,
                smmTime,
                mdrTime,
                sevTime,
                delTime,
                delAdvIntTime,
                delAdvPrinTime,
                forbRecovPpayTime,
                forbRecovMaturityTime,
                forbRecovDefaultTime,
                allMarketRates,
                origMdrTime: origMdrTime,
                recoveryLag: recoveryLag);

            // Convert results to PeriodCashflows and add to deal cashflows
            var periodCashflows = results.ToPeriodCashflows(firstProjDate, groupNum);
            foreach (var periodCf in periodCashflows) dealCashflows.AddPeriodCashflow(periodCf);
        }

        return dealCashflows;
    }

    /// <summary>
    ///     Build assumption array from IAnchorableVector, converting annual rates to monthly if needed.
    /// </summary>
    private static double[] BuildAssumptionArray(IAnchorableVector vector, int maxPeriods, int startTime,
        bool convertToMonthly, double divisor = 100.0, double defaultValue = 0.0)
    {
        var result = new double[maxPeriods];

        for (var period = 0; period < maxPeriods; period++)
        {
            var value = vector?.ValueAt(period, startTime + period) ?? defaultValue;

            if (convertToMonthly)
                // Convert annual rate (CPR/CDR) to monthly rate (SMM/MDR)
                result[period] = 1.0 - Math.Pow(1.0 - value / 100.0, 1.0 / 12.0);
            else
                result[period] = value / divisor;
        }

        return result;
    }

    /// <summary>
    ///     Build assumption array for ABS prepayment type using the time-varying ABS-to-SMM conversion.
    ///
    ///     Base formula: SMM = 100 * ABS / (100 - ABS * (n - 1))
    ///
    ///     When WAM (weighted average remaining term) is provided and ABS rate is high (>= 1.5%),
    ///     applies a partial amortization adjustment to prevent the formula from underestimating
    ///     SMM at later periods. The adjustment is scaled by the ABS rate to be more aggressive
    ///     at higher speeds where the base formula's assumptions break down.
    /// </summary>
    /// <param name="vector">The ABS rate vector</param>
    /// <param name="maxPeriods">Maximum number of periods</param>
    /// <param name="startTime">Start time for the vector</param>
    /// <param name="poolAgeOffset">Pool age offset in months (weighted average WALA) to account for seasoning</param>
    /// <param name="wam">Weighted average remaining term in months (0 = no amortization adjustment)</param>
    /// <param name="defaultValue">Default value if vector is null</param>
    private static double[] BuildAbsAssumptionArray(IAnchorableVector vector, int maxPeriods, int startTime,
        int poolAgeOffset = 0, int wam = 0, double defaultValue = 0.0)
    {
        var result = new double[maxPeriods];

        for (var period = 0; period < maxPeriods; period++)
        {
            var abs = vector?.ValueAt(period, startTime + period) ?? defaultValue;

            if (abs <= 0)
            {
                result[period] = 0;
                continue;
            }

            // n is the period number (1-indexed) plus pool age offset for seasoning
            // For a pool with WALA = poolAgeOffset, the effective age at projection period 0 is (poolAgeOffset + 1)
            var n = period + 1 + poolAgeOffset;

            // Convert ABS to SMM using the time-varying formula
            // SMM = 100 * ABS / (100 - ABS * (n - 1))
            var denominator = 100.0 - abs * (n - 1);

            if (denominator <= 0)
            {
                // At this point, the formula suggests 100% prepayment
                result[period] = 1.0;
            }
            else
            {
                var smm = 100.0 * abs / denominator;


                // Cap SMM at 100%
                result[period] = Math.Min(smm / 100.0, 1.0);
            }
        }

        return result;
    }

    /// <summary>
    ///     Build raw ABS rate array (as decimal fraction of original balance per period).
    ///     For ABS prepay, the rate represents what percentage of ORIGINAL balance to prepay each period.
    ///     E.g., 2.0% ABS means prepay 2% of original balance each period = 0.02 in the array.
    /// </summary>
    private static double[] BuildRawAbsArray(IAnchorableVector vector, int maxPeriods, int startTime,
        double defaultValue = 0.0)
    {
        var result = new double[maxPeriods];

        for (var period = 0; period < maxPeriods; period++)
        {
            var abs = vector?.ValueAt(period, startTime + period) ?? defaultValue;
            // Convert from percentage to decimal (e.g., 2.0 -> 0.02)
            result[period] = abs / 100.0;
        }

        return result;
    }

    /// <summary>
    ///     Build forbearance recovery array with special default handling.
    /// </summary>
    private static double[] BuildForbearanceArray(IAnchorableVector vector, int maxPeriods, int startTime,
        double defaultValue)
    {
        var result = new double[maxPeriods];

        for (var period = 0; period < maxPeriods; period++)
        {
            var value = vector?.ValueAt(period, startTime + period) ?? -1.0;
            result[period] = value > 0 ? value / 100.0 : defaultValue;
        }

        return result;
    }

    /// <summary>
    ///     Build market rate arrays for all rate indices.
    /// </summary>
    private static double[][] BuildMarketRateArrays(IRateProvider rateProvider, DateTime firstProjDate, int maxPeriods)
    {
        if (rateProvider == null)
            return null;

        // One per-period rate row per index, indexed by the MarketDataInstEnum
        // ordinal so the amortizer's `allMarketRates[(int)asset.IndexName]` lookup
        // is always in bounds. Sizing to the enum's full range (rather than only
        // the 5 Libor rows the previous version built) is what lets Swap- and
        // SOFR-indexed ARMs reset off their curve instead of throwing an
        // IndexOutOfRange or silently reading a never-populated row (graam-flows#37).
        //
        // NB: use a plain loop with direct enum→int casts — NOT LINQ
        // (Cast<int>()/Max()). The published server runtime throws
        // "Entry point was not found" on that LINQ path, so every CalcCollateral
        // call (this method runs for all of them) 400'd. Plain array/enum ops are
        // safe under that runtime; unit tests run under the JIT and didn't catch it.
        var insts = Enum.GetValues<MarketDataInstEnum>();

        var maxOrdinal = 0;
        foreach (var inst in insts)
        {
            var ordinal = (int)inst;
            if (ordinal > maxOrdinal) maxOrdinal = ordinal;
        }

        var allRates = new double[maxOrdinal + 1][];
        foreach (var inst in insts)
        {
            var row = new double[maxPeriods];
            for (var period = 0; period < maxPeriods; period++)
                row[period] = rateProvider.GetRate(inst, firstProjDate.AddMonths(period));
            allRates[(int)inst] = row;
        }

        return allRates;
    }

    public DealCashflows GenerateTrancheCashflows(IAssumptionMill assumps, IRateProvider rateProvider)
    {
        var collatFlows = GenerateAssetCashflows(Deal.Assets, FirstProjectionDate,
            g => Deal.DealTriggers.EarliestMandatoryDateRedemption(g), assumps.GetAssumptionsForAsset,
            rateProvider);

        return GenerateTrancheCashflows(collatFlows, rateProvider, assumps);
    }

    public DealCashflows GenerateTrancheCashflows(CollateralCashflows cashflows, IRateProvider rateProvider,
        IAssumptionMill assumps)
    {
        return CashflowEngine.Waterfall(Deal, rateProvider, FirstProjectionDate, cashflows, assumps,
            new TrancheAllocator());
    }

    /// <summary>
    ///     Revolving / reinvesting collateral pool (graam-flows#49). Cohort
    ///     orchestration: the per-asset amortizer is left untouched. During the
    ///     reinvestment window, eligible principal proceeds buy new collateral
    ///     from the config's templates up to a plain balance target
    ///     (reinvest cash = MIN(available proceeds, MAX(0, target − poolBalance))).
    ///     Each purchase is a fresh cohort projected forward by
    ///     <see cref="Amortizer.GenerateCashflows" />, and the cohorts are summed
    ///     into a "REINVEST" group returned as period cashflows for the caller to
    ///     merge into the collateral totals.
    ///
    ///     Monthly projection only (v1): quarterly reinvestment additionally needs
    ///     the period-indexed axis wiring deferred with graam-flows#46.
    ///     Reinvested collateral reuses one representative assumption set
    ///     (<paramref name="reinvestAssumps" />); ABS / ORIGMDR / forbearance /
    ///     recovery-lag on reinvested collateral are out of scope for v1.
    /// </summary>
    /// <param name="basePool">The already-projected (non-reinvesting) collateral
    /// period cashflows across all groups. Read only.</param>
    public static IList<PeriodCashflows> BuildReinvestmentCashflows(
        IList<PeriodCashflows> basePool, ReinvestmentConfig cfg, DateTime firstProjDate,
        IAssetAssumptions reinvestAssumps, IRateProvider rateProvider)
    {
        cfg.Validate("");
        var empty = new List<PeriodCashflows>();
        if (cfg.Templates.Count == 0 || basePool == null || basePool.Count == 0)
            return empty;

        var startTime = DateUtil.CalcAbsT(firstProjDate);

        // Base-pool aggregate arrays, indexed by whole-month period from the
        // projection start (monthly v1). Multiple groups sum into the same period.
        var basePeriods = 0;
        foreach (var cf in basePool)
            basePeriods = Math.Max(basePeriods, MonthsBetween(firstProjDate, cf.CashflowDate) + 1);
        if (basePeriods <= 0)
            return empty;

        var windowEndPeriod = MonthsBetween(firstProjDate, cfg.ReinvestEndDate);
        if (windowEndPeriod < 0)
            return empty;

        var maxTerm = cfg.Templates.Max(t => t.TermMonths);
        var horizon = Math.Min(720, Math.Max(basePeriods, windowEndPeriod + maxTerm + 2));

        var baseBalance = new double[horizon];
        var baseSched = new double[horizon];
        var baseUnsched = new double[horizon];
        var baseRecovery = new double[horizon];
        foreach (var cf in basePool)
        {
            var p = MonthsBetween(firstProjDate, cf.CashflowDate);
            if (p < 0 || p >= horizon) continue;
            baseBalance[p] += cf.Balance;
            baseSched[p] += cf.ScheduledPrincipal;
            baseUnsched[p] += cf.UnscheduledPrincipal;
            baseRecovery[p] += cf.RecoveryPrincipal;
        }

        var cohortAccum = new CashflowResultArrays(horizon);
        var eligible = cfg.EligibleProceeds;
        var seq = 0;

        for (var t = 0; t < horizon; t++)
        {
            var date = firstProjDate.AddMonths(t);
            if (date > cfg.ReinvestEndDate) break;
            if (cfg.ReinvestStartDate.HasValue && date < cfg.ReinvestStartDate.Value) continue;

            // Eligible proceeds and pool balance at end of period t, across the
            // original pool plus every cohort bought so far.
            var proceeds = 0.0;
            if (eligible.HasFlag(EligibleProceeds.ScheduledPrincipal))
                proceeds += baseSched[t] + cohortAccum.ScheduledPrincipal[t];
            if (eligible.HasFlag(EligibleProceeds.Prepayments))
                proceeds += baseUnsched[t] + cohortAccum.UnscheduledPrincipal[t];
            if (eligible.HasFlag(EligibleProceeds.Recoveries))
                proceeds += baseRecovery[t] + cohortAccum.RecoveryPrincipal[t];

            var available = proceeds * (1.0 - cfg.Holdback);
            var totalBalance = baseBalance[t] + cohortAccum.Balance[t];
            var gap = Math.Max(0.0, cfg.TargetAt(t) - totalBalance);
            var reinvestCash = Math.Min(available, gap);
            if (reinvestCash < 1.0) continue;

            // Cohorts originate at period t and begin amortizing at t+1.
            var cohortStart = t + 1;
            if (cohortStart >= horizon) continue;

            var cohortAssets = new List<IAsset>();
            foreach (var template in cfg.Templates)
            {
                var cash = reinvestCash * template.AllocationPct / 100.0;
                if (cash < 0.005) continue;
                // Cash buys face at the (par-for-synthetic) purchase price.
                var face = cash / (template.EffectivePrice / 100.0);
                cohortAssets.Add(BuildReinvestAsset(template, face, date, rateProvider, seq++));
            }

            if (cohortAssets.Count == 0) continue;

            var cohortPeriods = horizon - cohortStart;
            var cohortStartAbsT = startTime + cohortStart;
            var cohortEndAbsT = startTime + horizon - 1;

            var assetData = new AssetDataArrays(cohortAssets);
            var m = BuildReinvestAssumptionMatrices(reinvestAssumps, cohortAssets.Count, cohortPeriods, cohortStartAbsT);
            var allMarketRates = BuildMarketRateArrays(rateProvider, date, cohortPeriods);

            var cohortResult = Amortizer.GenerateCashflows(
                assetData, cohortStartAbsT, cohortEndAbsT,
                m.Smm, m.Mdr, m.Sev, m.Del, m.DelAdvInt, m.DelAdvPrin, m.ForbP, m.ForbM, m.ForbD,
                allMarketRates);

            // Accumulate the cohort's per-period vectors at the global offset.
            for (var p = 0; p < cohortResult.MaxPeriods; p++)
            {
                var gp = cohortStart + p;
                if (gp >= horizon) break;
                cohortAccum.BeginBalance[gp] += cohortResult.BeginBalance[p];
                cohortAccum.Balance[gp] += cohortResult.Balance[p];
                cohortAccum.ScheduledPrincipal[gp] += cohortResult.ScheduledPrincipal[p];
                cohortAccum.UnscheduledPrincipal[gp] += cohortResult.UnscheduledPrincipal[p];
                cohortAccum.Interest[gp] += cohortResult.Interest[p];
                cohortAccum.NetInterest[gp] += cohortResult.NetInterest[p];
                cohortAccum.ServiceFee[gp] += cohortResult.ServiceFee[p];
                cohortAccum.DefaultedPrincipal[gp] += cohortResult.DefaultedPrincipal[p];
                cohortAccum.RecoveryPrincipal[gp] += cohortResult.RecoveryPrincipal[p];
                cohortAccum.DelinqBalance[gp] += cohortResult.DelinqBalance[p];
                cohortAccum.UnAdvancedPrincipal[gp] += cohortResult.UnAdvancedPrincipal[p];
                cohortAccum.UnAdvancedInterest[gp] += cohortResult.UnAdvancedInterest[p];
                cohortAccum.AdvancedPrincipal[gp] += cohortResult.AdvancedPrincipal[p];
                cohortAccum.AdvancedInterest[gp] += cohortResult.AdvancedInterest[p];
                cohortAccum.ForbearanceRecovery[gp] += cohortResult.ForbearanceRecovery[p];
                cohortAccum.ForbearanceLiquidated[gp] += cohortResult.ForbearanceLiquidated[p];
                cohortAccum.AccumForbearance[gp] += cohortResult.AccumForbearance[p];
            }
        }

        cohortAccum.ComputeNumberOfPeriods();
        if (cohortAccum.NumberOfPeriods == 0)
            return empty;

        return cohortAccum.ToPeriodCashflows(firstProjDate, "REINVEST");
    }

    private static int MonthsBetween(DateTime from, DateTime to)
    {
        return (to.Year - from.Year) * 12 + (to.Month - from.Month);
    }

    /// <summary>
    ///     Instantiate a reinvested asset from a template and a face amount,
    ///     originated on <paramref name="originationDate" />. Floating templates
    ///     are resolved to an effective fixed coupon at origination (index +
    ///     margin) — a v1 approximation that avoids the curve-offset a mid-stream
    ///     cohort would otherwise hit in the ARM path.
    /// </summary>
    private static Asset BuildReinvestAsset(ReinvestTemplate t, double face, DateTime originationDate,
        IRateProvider rateProvider, int seq)
    {
        var couponPct = t.CouponRate;
        if (t.IndexName != MarketDataInstEnum.None && rateProvider != null)
            couponPct = rateProvider.GetRate(t.IndexName, originationDate) + t.IndexMargin;

        return new Asset
        {
            AssetName = $"REINVEST_{originationDate:yyyyMM}_{seq}",
            AssetId = $"REINVEST_{originationDate:yyyyMMdd}_{seq}",
            InterestRateType = InterestRateType.FRM,
            AmortizationType = t.AmortizationType,
            OriginalDate = originationDate,
            OriginalBalance = face,
            CurrentBalance = face,
            BalanceAtIssuance = face,
            OriginalInterestRate = couponPct,
            CurrentInterestRate = couponPct,
            OriginalAmortizationTerm = t.TermMonths,
            ServiceFee = t.ServiceFee,
            IndexName = MarketDataInstEnum.None,
            GroupNum = "REINVEST",
            IsIO = false
        };
    }

    private readonly record struct ReinvestMatrices(
        double[][] Smm, double[][] Mdr, double[][] Sev, double[][] Del,
        double[][] DelAdvInt, double[][] DelAdvPrin,
        double[][] ForbP, double[][] ForbM, double[][] ForbD);

    /// <summary>
    ///     Build the per-asset assumption matrices for a reinvestment cohort from
    ///     a single representative assumption set (every cohort asset shares the
    ///     row). Mirrors the standard (non-ABS, non-ORIGMDR) conversions in the
    ///     base amortization path; forbearance rows are zero (reinvested assets
    ///     carry no forbearance).
    /// </summary>
    private static ReinvestMatrices BuildReinvestAssumptionMatrices(
        IAssetAssumptions aa, int assetCount, int periods, int startAbsT)
    {
        var prepayType = aa?.PrepaymentType ?? PrepaymentTypeEnum.CPR;
        var defaultType = aa?.DefaultType ?? DefaultTypeEnum.CDR;

        // SMM is already-monthly; CPR/PSA/ABS de-annualize (ABS not fully
        // supported for reinvested collateral in v1 — treated CPR-style).
        var smmRow = BuildAssumptionArray(aa?.Prepayment, periods, startAbsT,
            prepayType != PrepaymentTypeEnum.SMM);
        // MDR/ORIGMDR are already-monthly; CDR de-annualizes (ORIGMDR treated as
        // a current-balance hazard for reinvested collateral in v1).
        var mdrRow = BuildAssumptionArray(aa?.DefaultRate, periods, startAbsT,
            defaultType == DefaultTypeEnum.CDR);
        var sevRow = BuildAssumptionArray(aa?.Severity, periods, startAbsT, false, 100.0);
        var delRow = BuildAssumptionArray(aa?.DelinqRate, periods, startAbsT, false, 100.0);
        var delAdvIntRow = BuildAssumptionArray(aa?.DelinqAdvPctInt, periods, startAbsT, false, 1.0, 100.0);
        var delAdvPrinRow = BuildAssumptionArray(aa?.DelinqAdvPctPrin, periods, startAbsT, false, 1.0, 100.0);
        var zeroRow = new double[periods];

        double[][] Rep(double[] row)
        {
            var m = new double[assetCount][];
            for (var i = 0; i < assetCount; i++) m[i] = row;
            return m;
        }

        return new ReinvestMatrices(
            Rep(smmRow), Rep(mdrRow), Rep(sevRow), Rep(delRow),
            Rep(delAdvIntRow), Rep(delAdvPrinRow),
            Rep(zeroRow), Rep(zeroRow), Rep(zeroRow));
    }

    public static void CompileRules(IDeal deal)
    {
        deal.RuleAssembly = RulesBuilder.CompileRules(deal);
    }
}