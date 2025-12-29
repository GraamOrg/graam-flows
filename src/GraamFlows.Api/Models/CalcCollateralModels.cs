namespace GraamFlows.Api.Models;
// ============== Request Models ==============

public class CalcCollateralRequest
{
    public List<AssetDto> Assets { get; set; } = new();
    public DateTime ProjectionDate { get; set; }
    public AssumptionsDto Assumptions { get; set; } = new();
}

public class AssetDto
{
    public string AssetName { get; set; } = "";
    public string? AssetId { get; set; }
    public string InterestRateType { get; set; } = "FRM"; // FRM, ARM, STEP
    public DateTime OriginalDate { get; set; }
    public double OriginalBalance { get; set; }
    public double OriginalInterestRate { get; set; }
    public double CurrentInterestRate { get; set; }
    public int OriginalAmortizationTerm { get; set; }
    public double CurrentBalance { get; set; }
    public double ServiceFee { get; set; }
    public double DebtService { get; set; }
    public string GroupNum { get; set; } = "0";

    // ARM-specific fields
    public int InitialAdjustmentPeriod { get; set; }
    public int AdjustmentPeriod { get; set; }
    public double InitialRate { get; set; }
    public string? IndexName { get; set; } // Libor1M, Sofr30Avg, etc.
    public double IndexMargin { get; set; }
    public double? LifeAdjustmentCap { get; set; }
    public double? LifeAdjustmentFloor { get; set; }
    public double? AdjustmentCap { get; set; }

    // IO-specific fields
    public bool IsIO { get; set; }
    public int? IOTerm { get; set; }

    // Forbearance
    public double? ForbearanceAmt { get; set; }

    // Step rates
    public string? StepDatesList { get; set; }
    public string? StepRatesList { get; set; }
}

public class AssumptionsDto
{
    public double Cpr { get; set; } = 6.0; // Annual CPR %
    public double Cdr { get; set; } = 0.5; // Annual CDR %
    public double Severity { get; set; } = 40.0; // Loss severity %
    public double Delinquency { get; set; } = 0.0; // Delinquency rate %
    public double Advancing { get; set; } = 100.0; // Advancing rate %
}

// ============== Response Models ==============

public class CalcCollateralResponse
{
    public List<PeriodCashflowDto> Cashflows { get; set; } = new();
    public CollateralSummaryDto Summary { get; set; } = new();
}

public class PeriodCashflowDto
{
    public int Period { get; set; }
    public DateTime CashflowDate { get; set; }
    public string GroupNum { get; set; } = "0";
    public double BeginBalance { get; set; }
    public double Balance { get; set; }
    public double ScheduledPrincipal { get; set; }
    public double UnscheduledPrincipal { get; set; }
    public double Interest { get; set; }
    public double NetInterest { get; set; }
    public double ServiceFee { get; set; }
    public double DefaultedPrincipal { get; set; }
    public double RecoveryPrincipal { get; set; }
    public double CollateralLoss { get; set; }
    public double DelinqBalance { get; set; }
    public double ForbearanceRecovery { get; set; }
    public double ForbearanceLiquidated { get; set; }
    public double Wac { get; set; }
    public double Wam { get; set; }
    public double Wala { get; set; }
    public double Vpr { get; set; }
    public double Cdr { get; set; }
    public double Sev { get; set; }
    public double Dq { get; set; }
    public double CumDefaultedPrincipal { get; set; }
    public double CumCollateralLoss { get; set; }
}

public class CollateralSummaryDto
{
    public int TotalPeriods { get; set; }
    public double OriginalBalance { get; set; }
    public double Wac { get; set; }
    public double Wam { get; set; }
    public double Wala { get; set; }
    public double TotalScheduledPrincipal { get; set; }
    public double TotalUnscheduledPrincipal { get; set; }
    public double TotalInterest { get; set; }
    public double TotalDefaultedPrincipal { get; set; }
    public double TotalRecoveryPrincipal { get; set; }
    public double TotalCollateralLoss { get; set; }
}