using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;

namespace GraamFlows.Assumptions;

public class AssetAssumptions : IAssetAssumptions
{
    public AssetAssumptions(PrepaymentTypeEnum prepaymentType, IAnchorableVector ppaySpeed, DefaultTypeEnum defaultType,
        IAnchorableVector defaultRate, IAnchorableVector severityRate)
    {
        // PercentCPR is an annual-rate convention that we eagerly normalize to
        // CPR here (the input is an SMM-shaped fraction the parser produced).
        // SMM, by contrast, is a *direct monthly* hazard the engine must honor
        // as-is (harmony #1226): we preserve both the raw vector and the SMM
        // type so CfCore can skip the annual→monthly de-annualization.
        if (prepaymentType == PrepaymentTypeEnum.PercentCPR)
        {
            Prepayment = ppaySpeed.transform(MathUtil.ConvertToCpr);
            PrepaymentType = PrepaymentTypeEnum.CPR;
        }
        else
        {
            Prepayment = ppaySpeed;
            PrepaymentType = prepaymentType;
        }

        // CDR is de-annualized downstream; MDR is a direct monthly default
        // hazard (harmony #1226) that must flow through untouched so CfCore
        // can skip the de-annualization for it.
        DefaultType = defaultType;
        DefaultRate = defaultRate;

        Severity = severityRate;
        DelinqRate = new ConstVector(0);
        DelinqRateType = DelinqRateTypeEnum.PctCurrBal;
        DelinqAdvPctPrin = new ConstVector(100);
        DelinqAdvPctInt = new ConstVector(100);
        ForbearanceRecoveryMaturity = new ConstVector(100);
    }

    public AssetAssumptions(PrepaymentTypeEnum prepaymentType, IAnchorableVector ppaySpeed, DefaultTypeEnum defaultType,
        IAnchorableVector defaultRate, IAnchorableVector severityRate,
        DelinqRateTypeEnum delinqRateType, IAnchorableVector delinqRate, IAnchorableVector delinqAdvPctPrin,
        IAnchorableVector delinqAdvPctInt)
        : this(prepaymentType, ppaySpeed, defaultType, defaultRate, severityRate)
    {
        DelinqRateType = delinqRateType;
        DelinqRate = delinqRate;
        DelinqAdvPctPrin = delinqAdvPctPrin;
        DelinqAdvPctInt = delinqAdvPctInt;
        ForbearanceRecoveryMaturity = new ConstVector(100);
    }

    public AssetAssumptions(PrepaymentTypeEnum prepaymentType, IAnchorableVector ppaySpeed, DefaultTypeEnum defaultType,
        IAnchorableVector defaultRate, IAnchorableVector severityRate,
        DelinqRateTypeEnum delinqRateType, IAnchorableVector delinqRate, IAnchorableVector delinqAdvPctPrin,
        IAnchorableVector delinqAdvPctInt,
        IAnchorableVector forbRecovPrepay, IAnchorableVector forbRecovDefault, IAnchorableVector forbRecovMaturity)
        : this(prepaymentType, ppaySpeed, defaultType, defaultRate, severityRate)
    {
        DelinqRateType = delinqRateType;
        DelinqRate = delinqRate;
        DelinqAdvPctPrin = delinqAdvPctPrin;
        DelinqAdvPctInt = delinqAdvPctInt;
        ForbearanceRecoveryPrepay = forbRecovPrepay;
        ForbearanceRecoveryDefault = forbRecovDefault;
        ForbearanceRecoveryMaturity = forbRecovMaturity;
    }

    public PrepaymentTypeEnum PrepaymentType { get; }
    public IAnchorableVector Prepayment { get; }
    public DefaultTypeEnum DefaultType { get; }
    public IAnchorableVector DefaultRate { get; }
    public IAnchorableVector Severity { get; }

    /// <summary>
    ///     Recovery lag in months (graam-harmony #3449). Defaults to 0
    ///     (same-period recovery); set via the assumption builders when a deal
    ///     specifies a liquidation timeline.
    /// </summary>
    public int RecoveryLag { get; set; }

    public IAnchorableVector DelinqRate { get; }
    public DelinqRateTypeEnum DelinqRateType { get; }

    public IAnchorableVector DelinqAdvPctPrin { get; }
    public IAnchorableVector DelinqAdvPctInt { get; }
    public IAnchorableVector ForbearanceRecoveryPrepay { get; }
    public IAnchorableVector ForbearanceRecoveryDefault { get; }
    public IAnchorableVector ForbearanceRecoveryMaturity { get; }
}