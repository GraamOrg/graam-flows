using GraamFlows.AssetCashflowEngine;
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

            // Get assumptions from first asset (assumes uniform assumptions per group)
            var firstAssetAssumps = groupAssets.Count > 0 ? assumpFunc?.Invoke(groupAssets[0]) : null;

            // Get prepayment type
            var prepaymentType = firstAssetAssumps?.PrepaymentType ?? Objects.TypeEnum.PrepaymentTypeEnum.CPR;

            // Build assumption arrays
            // For ABS prepayment type, use time-varying ABS-to-SMM conversion
            // SMM = ABS / (100 - ABS * (n-1)) where n is the period number (1-indexed)
            // This formula is mathematically exact for the ABS convention where a constant fraction
            // of the original NUMBER of receivables prepay each month.
            var smmTime = prepaymentType == Objects.TypeEnum.PrepaymentTypeEnum.ABS
                ? BuildAbsAssumptionArray(firstAssetAssumps?.Prepayment, maxPeriods, startTime, poolAgeOffset, wam)
                : BuildAssumptionArray(firstAssetAssumps?.Prepayment, maxPeriods, startTime, true);
            var mdrTime = BuildAssumptionArray(firstAssetAssumps?.DefaultRate, maxPeriods, startTime, true);
            var sevTime = BuildAssumptionArray(firstAssetAssumps?.Severity, maxPeriods, startTime, false, 100.0);
            var delTime = BuildAssumptionArray(firstAssetAssumps?.DelinqRate, maxPeriods, startTime, false, 100.0);
            var delAdvIntTime = BuildAssumptionArray(firstAssetAssumps?.DelinqAdvPctInt, maxPeriods, startTime, false,
                1.0, 100.0);
            var delAdvPrinTime = BuildAssumptionArray(firstAssetAssumps?.DelinqAdvPctPrin, maxPeriods, startTime, false,
                1.0, 100.0);
            var forbRecovPpayTime = BuildForbearanceArray(firstAssetAssumps?.ForbearanceRecoveryPrepay, maxPeriods,
                startTime, -1.0);
            var forbRecovMaturityTime = BuildForbearanceArray(firstAssetAssumps?.ForbearanceRecoveryMaturity,
                maxPeriods, startTime, 1.0);
            var forbRecovDefaultTime = BuildForbearanceArray(firstAssetAssumps?.ForbearanceRecoveryDefault, maxPeriods,
                startTime, -1.0);

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
                allMarketRates);

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

        // 5 rate indices: None, Libor1M, Libor3M, Libor6M, Libor12M
        var allRates = new double[5][];
        var indexNames = new[]
        {
            MarketDataInstEnum.None, MarketDataInstEnum.Libor1M, MarketDataInstEnum.Libor3M,
            MarketDataInstEnum.Libor6M, MarketDataInstEnum.Libor12M
        };

        for (var i = 0; i < 5; i++)
        {
            allRates[i] = new double[maxPeriods];
            var indexName = indexNames[i];

            for (var period = 0; period < maxPeriods; period++)
            {
                var date = firstProjDate.AddMonths(period);
                allRates[i][period] = rateProvider.GetRate(indexName, date);
            }
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

    public static void CompileRules(IDeal deal)
    {
        deal.RuleAssembly = RulesBuilder.CompileRules(deal);
    }
}