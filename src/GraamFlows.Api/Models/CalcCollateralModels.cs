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
    // Scalar values (used if vector strings are not provided)
    public double Cpr { get; set; } = 6.0; // Annual CPR %
    public double Cdr { get; set; } = 0.5; // Annual CDR %
    public double Severity { get; set; } = 40.0; // Loss severity %
    public double Delinquency { get; set; } = 0.0; // Delinquency rate %
    public double Advancing { get; set; } = 100.0; // Advancing rate %

    // Prepayment convention: "CPR" (default, % of current balance) or "ABS" (% of original balance).
    // Auto ABS deals use "ABS"; RMBS/agency deals use "CPR".
    public string PrepaymentType { get; set; } = "CPR";

    // Weighted average remaining term (months) — used for ABS-to-SMM amortization adjustment.
    public int Wam { get; set; } = 0;

    // Per-period arrays — if provided, these override the scalar values above.
    // Each element is one period's rate (e.g., [10.4, 9.8, 8.2, ...] for monthly CDR %).
    public double[]? CprVector { get; set; }
    public double[]? CdrVector { get; set; }
    public double[]? SeverityVector { get; set; }
    public double[]? DelinquencyVector { get; set; }
    public double[]? AdvancingVector { get; set; }

    // PolyPaths format strings (legacy) — "6.0", "1.0R12,6.0", "6.0/12", "202301,1.0R12,6.0"
    // Used only if the array version above is not provided.
    public string? CprVectorStr { get; set; }
    public string? CdrVectorStr { get; set; }
    public string? SeverityVectorStr { get; set; }
    public string? DelinquencyVectorStr { get; set; }
    public string? AdvancingVectorStr { get; set; }
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
    public double ForbearanceUnscheduled { get; set; }
    public double AccumForbearance { get; set; }
    public double Wac { get; set; }
    public double Wam { get; set; }
    public double Wala { get; set; }
    public double Vpr { get; set; }
    public double Cdr { get; set; }
    public double Sev { get; set; }
    public double Dq { get; set; }
    public double CumDefaultedPrincipal { get; set; }
    public double CumCollateralLoss { get; set; }
    public double UnAdvancedPrincipal { get; set; }
    public double UnAdvancedInterest { get; set; }
    public double AdvancedPrincipal { get; set; }
    public double AdvancedInterest { get; set; }
    public double Expenses { get; set; }
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
    public double CumDefaultPct { get; set; }
    public double CumLossPct { get; set; }
}