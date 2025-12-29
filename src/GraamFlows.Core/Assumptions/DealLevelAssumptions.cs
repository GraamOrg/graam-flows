using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Assumptions;

public class DealLevelAssumptions : IAssumptionMill
{
    public readonly List<TriggerForecast> TriggerForecasts = new();

    public DealLevelAssumptions(DateTime settleDate, IAssetAssumptions assumps)
    {
        SettleDate = settleDate;
        Assumptions = assumps;
        CompoundingMethod = CompoundingTypeEnum.Continuous;
        YieldCurveAssumptions = new DefaultYieldCurveAssumptions();
        DisplayAssetCashflows = false;
        Threads = -1;
    }

    public DealLevelAssumptions(DateTime settleDate, Func<IAsset, IAssetAssumptions> assumps)
    {
        SettleDate = settleDate;
        AssumFunc = assumps;
        Assumptions = null;
        CompoundingMethod = CompoundingTypeEnum.Continuous;
        YieldCurveAssumptions = new DefaultYieldCurveAssumptions();
        DisplayAssetCashflows = false;
        Threads = -1;
    }

    public DealLevelAssumptions(DateTime settleDate, IModelAssumps modelAssumps)
    {
        SettleDate = settleDate;
        ModelAssumptions = modelAssumps;
        CompoundingMethod = CompoundingTypeEnum.Continuous;
        YieldCurveAssumptions = new DefaultYieldCurveAssumptions();
        DisplayAssetCashflows = false;
        Threads = -1;
    }

    public DealLevelAssumptions(DateTime settleDate)
    {
        SettleDate = settleDate;
    }

    public DealLevelAssumptions()
    {
    }

    public IAssetAssumptions Assumptions { get; }
    public Func<IAsset, IAssetAssumptions> AssumFunc { get; }

    public bool RunToCall { get; set; }

    public DateTime SettleDate { get; }

    public IAssetAssumptions GetAssumptionsForAsset(IAsset asset)
    {
        if (Assumptions != null)
            return Assumptions;
        return AssumFunc.Invoke(asset);
    }

    public CompoundingTypeEnum CompoundingMethod { get; set; }
    public IYieldCurveAssumptions YieldCurveAssumptions { get; }
    public bool DisplayAssetCashflows { get; set; }
    public int Threads { get; set; }

    public TriggerForecast GetTriggerForecast(string triggerName, string groupNum)
    {
        var forecast = TriggerForecasts.SingleOrDefault(t => t.TriggerName == triggerName && t.GroupNum == groupNum);
        if (forecast != null)
            return forecast;

        forecast = TriggerForecasts.SingleOrDefault(t => t.TriggerName == triggerName && t.GroupNum == "0");
        if (forecast != null)
            return forecast;

        if (RunToCall)
            return new TriggerForecast(triggerName, groupNum, true);
        return null;
    }

    public IModelAssumps ModelAssumptions { get; set; }

    public void AddTriggerForecast(string triggerName, string group, bool value)
    {
        TriggerForecasts.Add(new TriggerForecast(triggerName, group, value));
    }

    public static DealLevelAssumptions CreateConstAssumptions(DateTime settleDate, int anchorAbsT, double vpr,
        double cdr, double sev)
    {
        var assetAssumps = new AssetAssumptions(PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, vpr),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, cdr), new ConstVector(anchorAbsT, sev));
        return new DealLevelAssumptions(settleDate, assetAssumps);
    }

    public static DealLevelAssumptions CreateConstAssumptions(DateTime settleDate, int anchorAbsT, double vpr,
        double cdr, double sev,
        double delinq, double adv)
    {
        var assetAssumps = new AssetAssumptions(PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, vpr),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, cdr), new ConstVector(anchorAbsT, sev),
            DelinqRateTypeEnum.PctCurrBal, new ConstVector(anchorAbsT, delinq),
            new ConstVector(anchorAbsT, adv), new ConstVector(anchorAbsT, adv));
        return new DealLevelAssumptions(settleDate, assetAssumps);
    }

    public static DealLevelAssumptions CreateConstAssumptions(DateTime settleDate, int anchorAbsT, double vpr,
        double cdr, double sev,
        double delinq, double adv, double forbRecovPrepay, double forbRecovDefault, double forbRecovMatur)
    {
        var assetAssumps = new AssetAssumptions(PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, vpr),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, cdr), new ConstVector(anchorAbsT, sev),
            DelinqRateTypeEnum.PctCurrBal, new ConstVector(anchorAbsT, delinq),
            new ConstVector(anchorAbsT, adv), new ConstVector(anchorAbsT, adv),
            new ConstVector(anchorAbsT, forbRecovPrepay), new ConstVector(anchorAbsT, forbRecovDefault),
            new ConstVector(anchorAbsT, forbRecovMatur));
        return new DealLevelAssumptions(settleDate, assetAssumps);
    }

    public static DealLevelAssumptions CreateConstAssumptions(DateTime settleDate, int anchorAbsT,
        double vpr, double cdr, double sev, double delinq)
    {
        var assetAssumps = new AssetAssumptions(PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, vpr),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, cdr), new ConstVector(anchorAbsT, sev),
            DelinqRateTypeEnum.PctOrigBal, new ConstVector(anchorAbsT, delinq),
            new ConstVector(anchorAbsT, 100.0), new ConstVector(anchorAbsT, 100.0));
        return new DealLevelAssumptions(settleDate, assetAssumps);
    }

    public static DealLevelAssumptions CreateConstAssumptions(DateTime settleDate, int anchorAbsT, double vpr1,
        double cdr1, double sev1,
        double vpr2, double cdr2, double sev2)
    {
        var assetAssumps1 = new AssetAssumptions(PrepaymentTypeEnum.CPR,
            new ConstVector(anchorAbsT, vpr1), DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, cdr1),
            new ConstVector(anchorAbsT, sev1));

        var assetAssumps2 = new AssetAssumptions(PrepaymentTypeEnum.CPR,
            new ConstVector(anchorAbsT, vpr2), DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, cdr2),
            new ConstVector(anchorAbsT, sev2));

        return new DealLevelAssumptions(settleDate, asset =>
        {
            if (asset.GroupNum == "1")
                return assetAssumps1;
            return assetAssumps2;
        });
    }


    public static DealLevelAssumptions CreateConstAssumptions(DateTime settleDate, int anchorAbsT,
        string vprStr,
        string cdrStr, string sevStr,
        string delinqStr, string advStr)
    {
        var vpr = PolyPathsVectorLanguageParser.parseAnchorableVector(vprStr, 0, null, anchorAbsT);
        var cdr = PolyPathsVectorLanguageParser.parseAnchorableVector(cdrStr, 0, null, anchorAbsT);
        var sev = PolyPathsVectorLanguageParser.parseAnchorableVector(sevStr, 0, null, anchorAbsT);
        var delinq = PolyPathsVectorLanguageParser.parseAnchorableVector(delinqStr, 0, null, anchorAbsT);
        var adv = PolyPathsVectorLanguageParser.parseAnchorableVector(advStr, 0, null, anchorAbsT);
        var assetAssumps = new AssetAssumptions(PrepaymentTypeEnum.CPR, vpr,
            DefaultTypeEnum.CDR, cdr, sev,
            DelinqRateTypeEnum.PctCurrBal, delinq,
            adv, adv);
        return new DealLevelAssumptions(settleDate, assetAssumps);
    }

    public static DealLevelAssumptions CreateConstAssumptionsPsa(DateTime settleDate, int anchorAbsT,
        string psaStr)
    {
        var psa = PolyPathsVectorLanguageParser.parseAnchorableVector(psaStr, 0, null, anchorAbsT);
        var assetAssumps = new AssetAssumptions(PrepaymentTypeEnum.PSA, psa,
            DefaultTypeEnum.CDR, new ConstVector(0), new ConstVector(0),
            DelinqRateTypeEnum.PctCurrBal, new ConstVector(0),
            new ConstVector(0), new ConstVector(0));
        return new DealLevelAssumptions(settleDate, assetAssumps);
    }
}