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
        if (prepaymentType == PrepaymentTypeEnum.SMM || prepaymentType == PrepaymentTypeEnum.PercentCPR)
        {
            Prepayment = ppaySpeed.transform(MathUtil.ConvertToCpr);
            PrepaymentType = PrepaymentTypeEnum.CPR;
        }
        else
        {
            Prepayment = ppaySpeed;
            PrepaymentType = prepaymentType;
        }

        if (defaultType != DefaultTypeEnum.CDR)
        {
            DefaultRate = defaultRate.transform(MathUtil.ConvertToCpr);
            DefaultType = DefaultTypeEnum.CDR;
        }
        else
        {
            DefaultType = defaultType;
            DefaultRate = defaultRate;
        }

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

    public IAnchorableVector DelinqRate { get; }
    public DelinqRateTypeEnum DelinqRateType { get; }

    public IAnchorableVector DelinqAdvPctPrin { get; }
    public IAnchorableVector DelinqAdvPctInt { get; }
    public IAnchorableVector ForbearanceRecoveryPrepay { get; }
    public IAnchorableVector ForbearanceRecoveryDefault { get; }
    public IAnchorableVector ForbearanceRecoveryMaturity { get; }
}