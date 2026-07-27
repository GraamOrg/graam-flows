using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Objects.DataObjects;

public interface IAssetAssumptions
{
    PrepaymentTypeEnum PrepaymentType { get; }
    IAnchorableVector Prepayment { get; }

    DefaultTypeEnum DefaultType { get; }
    IAnchorableVector DefaultRate { get; }

    IAnchorableVector Severity { get; }

    /// <summary>
    ///     Recovery lag in months: recoveries on a period-t default are placed at
    ///     period t + RecoveryLag (the liquidation timeline), per the reference
    ///     calc standard (graam-harmony #3449). 0 = same-period recovery.
    /// </summary>
    int RecoveryLag { get; }

    IAnchorableVector DelinqRate { get; }
    DelinqRateTypeEnum DelinqRateType { get; }

    IAnchorableVector DelinqAdvPctPrin { get; }
    IAnchorableVector DelinqAdvPctInt { get; }

    IAnchorableVector ForbearanceRecoveryPrepay { get; }
    IAnchorableVector ForbearanceRecoveryDefault { get; }
    IAnchorableVector ForbearanceRecoveryMaturity { get; }
}

public interface IYieldCurveAssumptions
{
    List<MarketDataInstEnum> YieldCurveInstruments { get; }
    IInterpolation2D Interpolator { get; }
    IDayCounter DayCounter { get; }
    CompoundingTypeEnum CompoundingType { get; }
    FrequencyTypeEnum Frequency { get; }
}

public interface IAssumptionMill
{
    DateTime SettleDate { get; }
    CompoundingTypeEnum CompoundingMethod { get; }
    IYieldCurveAssumptions YieldCurveAssumptions { get; }
    bool DisplayAssetCashflows { get; }
    int Threads { get; }
    IAssetAssumptions GetAssumptionsForAsset(IAsset asset);
    TriggerForecast GetTriggerForecast(string triggerName, string groupNum);
}