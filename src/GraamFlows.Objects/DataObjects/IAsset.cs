using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Objects.DataObjects;

public interface IAsset
{
    string AssetName { get; set; }
    string AssetId { get; set; }
    InterestRateType InterestRateType { get; set; }
    DateTime OriginalDate { get; set; }
    double OriginalBalance { get; set; }
    double OriginalInterestRate { get; set; }
    double CurrentInterestRate { get; set; }
    int OriginalAmortizationTerm { get; set; }
    double CurrentBalance { get; set; }
    double BalanceAtIssuance { get; set; }
    double OriginalLTV { get; set; }
    string GroupNum { get; set; }
    string LoanStatus { get; set; }
    double ServiceFee { get; set; }
    double DebtService { get; set; }
    int InitialAdjustmentPeriod { get; set; }
    int AdjustmentPeriod { get; set; }
    double InitialRate { get; set; }
    MarketDataInstEnum IndexName { get; set; }
    double IndexMargin { get; set; }
    double? LifeAdjustmentCap { get; set; }
    double? LifeAdjustmentFloor { get; set; }
    double? AdjustmentCap { get; set; }
    bool IsIO { get; set; }
    int? IOTerm { get; set; }
    double? ForbearanceAmt { get; set; }
    string StepDatesList { get; set; }
    string StepRatesList { get; set; }

    /// <summary>
    ///     Weighted average loan age in months. Used for ABS-to-SMM conversion where
    ///     the pool's seasoning affects the prepayment rate formula.
    /// </summary>
    int Wala { get; set; }
}