using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Domain;

public class Asset : IAsset
{
    [Database("Trust_Loan_Id")] public string AssetName { get; set; }

    [Database("Trust_Loan_Id")] public string AssetId { get; set; }

    [Database("Product_Type")] public InterestRateType InterestRateType { get; set; }

    [Database("Origination_Date")] public DateTime OriginalDate { get; set; }

    [Database("Original_Balance")] public double OriginalBalance { get; set; }

    [Database("Original_Interest_Rate")] public double OriginalInterestRate { get; set; }

    [Database("Current_Interest_Rate")] public double CurrentInterestRate { get; set; }

    [Database("Amortization_Term")] public int OriginalAmortizationTerm { get; set; }

    [Database("Current_Balance")] public double CurrentBalance { get; set; }

    [Database("Balance_At_Issuance")] public double BalanceAtIssuance { get; set; }

    [Database("Original_LTV")] public double OriginalLTV { get; set; }

    [Database("Group_Num")] public string GroupNum { get; set; }

    [Database("Loan_Status")] public string LoanStatus { get; set; }

    [Database("Service_Fee")] public double ServiceFee { get; set; }

    [Database("Debt_Service")] public double DebtService { get; set; }

    [Database("Is_InterestOnly")] public bool IsIO { get; set; }

    [Database("IO_Term")] public int? IOTerm { get; set; }

    [Database("Arm_Init_Adj_Period")] public int InitialAdjustmentPeriod { get; set; }

    [Database("Arm_Adjustment_Period")] public int AdjustmentPeriod { get; set; }

    [Database("Arm_Init_Rate")] public double InitialRate { get; set; }

    [Database("Arm_Index")] public MarketDataInstEnum IndexName { get; set; }

    [Database("Arm_Index_Margin")] public double IndexMargin { get; set; }

    [Database("Arm_Adjustment_Cap")] public double? AdjustmentCap { get; set; }

    [Database("Arm_Life_Adjustment_Cap")] public double? LifeAdjustmentCap { get; set; }

    [Database("Arm_Life_Adjustment_Floor")]
    public double? LifeAdjustmentFloor { get; set; }

    [Database("Forbearance_Amt")] public double? ForbearanceAmt { get; set; }

    [Database("Step_Dates_List")] public string StepDatesList { get; set; }

    [Database("Step_Rates_List")] public string StepRatesList { get; set; }

    public int Wala { get; set; }

    public override int GetHashCode()
    {
        return AssetId?.GetHashCode() ?? 0;
    }
}