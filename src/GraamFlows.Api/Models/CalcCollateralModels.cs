namespace GraamFlows.Api.Models;
// ============== Request Models ==============

public class CalcCollateralRequest
{
    public List<AssetDto> Assets { get; set; } = new();
    public DateTime ProjectionDate { get; set; }

    /// <summary>
    ///     Business days before each monthly row's date at which its collections close (a managed
    ///     pool's Collection Period ends N Business Days before the Payment Date). Places a
    ///     less-than-monthly asset's payment date in a row. Default 0: the row's own date.
    /// </summary>
    public int? CollectionCutoffBusinessDays { get; set; }

    /// <summary>US holiday calendar for <see cref="CollectionCutoffBusinessDays" />. Default "Settlement".</summary>
    public string? HolidayCalendar { get; set; }

    /// <summary>
    ///     The date the collateral starts accruing interest for its owner (a deal's Closing Date).
    ///     Interest an asset accrued before it was bought with the asset and is not Interest
    ///     Proceeds, so it is not projected: an asset's first payment pays only the interest accrued
    ///     since this date. Null: interest accrues from the start of the first projection period.
    /// </summary>
    public DateTime? InterestAccrualStartDate { get; set; }

    /// <summary>
    ///     Hold a CMBS trust's interest reserve on the Actual/360 loans: one day's net interest is
    ///     withheld from each one's January (except in a leap year) and February payments and paid
    ///     out with March's, so the pool's net interest is a 30-day month's in all three, as the
    ///     30/360-equivalent Net Mortgage Rate the pass-through rates are set on. Default false:
    ///     other Actual/360 collateral (a CLO's loans) pays the actual days every month.
    /// </summary>
    public bool InterestReserve { get; set; }
    public AssumptionsDto Assumptions { get; set; } = new();

    /// <summary>
    /// Optional per-asset assumption overrides keyed by AssetId. When provided,
    /// each entry's values override the deal-level <see cref="Assumptions"/> for
    /// that specific asset. Assets without an entry fall back to deal-level.
    /// See graam-flows#5: per-loan CDR/CPR/severity vectors from a loan-level
    /// model (e.g. logit-predicted default probabilities scored per loan).
    /// </summary>
    public Dictionary<string, AssetAssumptionDto>? AssetAssumptions { get; set; }

    /// <summary>
    /// Optional forward-rate curves for ARM/hybrid resets (graam-flows#37), keyed
    /// by index name (e.g. "Libor1M"). Each curve is a list of
    /// <c>[monthOffset, rate]</c> points, where monthOffset is months from
    /// <see cref="ProjectionDate"/> (0 = first projection month) and rate is the
    /// annual percentage (e.g. 4.75). Mirrors the Waterfall endpoint's
    /// <c>marketRates</c> shape. When omitted, ARM resets fall back to a flat rate
    /// (legacy behavior); when supplied, each ARM's fully-indexed coupon is
    /// margin + the curve's rate at each reset. Fixed-rate loans are unaffected.
    /// </summary>
    public Dictionary<string, List<double[]>>? MarketRates { get; set; }
}

/// <summary>
/// Per-asset assumption override. Mirrors the rate fields of <see cref="AssumptionsDto"/>
/// but scoped to a single asset. Any field left null falls through to the
/// deal-level value. PrepaymentType is intentionally not exposed — it's a
/// deal-level mode (selects the ABS-vs-SMM conversion path in the engine),
/// not a per-asset toggle.
/// </summary>
public class AssetAssumptionDto
{
    // Scalar overrides (used if vector is not provided for that key)
    public double? Cpr { get; set; }
    public double? Cdr { get; set; }
    public double? Severity { get; set; }
    public double? Delinquency { get; set; }
    public double? Advancing { get; set; }

    // Recovery lag in months (graam-harmony #3449): recoveries on a period-t
    // default are placed at period t + RecoveryLag. Null inherits the deal-level
    // value; 0 = same-period recovery.
    public int? RecoveryLag { get; set; }

    // Per-period vector overrides — if provided, override the scalar above for this asset.
    public double[]? CprVector { get; set; }
    public double[]? CdrVector { get; set; }
    public double[]? SeverityVector { get; set; }
    public double[]? DelinquencyVector { get; set; }
    public double[]? AdvancingVector { get; set; }
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

    /// <summary>
    ///     The coupon's day count: "30/360" (the default when absent) or "Actual/360". Any other
    ///     spelling is rejected rather than accrued as 30/360.
    /// </summary>
    public string? DayCount { get; set; }

    /// <summary>
    ///     Payments a year: 12 (the default), 6, 4, 3, 2 or 1. Below 12 the asset pays interest and
    ///     prepays, defaults and recovers only on its payment dates; <see cref="NextPaymentDate" />
    ///     is then required.
    /// </summary>
    public int? PaymentFrequency { get; set; }

    /// <summary>The asset's next payment date (places its payment months).</summary>
    public DateTime? NextPaymentDate { get; set; }

    /// <summary>
    ///     The asset's maturity date. When stated it overrides the term: the asset repays in the row
    ///     whose collection cutoff reaches the date, with interest accrued only up to it.
    /// </summary>
    public DateTime? MaturityDate { get; set; }

    /// <summary>
    ///     The date the tape's whole-month remaining term was counted from (its as-of date). With a
    ///     stated calendar and maturity, the maturity carries the months on that monthly grid since the
    ///     asset's last payment, and a scheduled date in the grid month it ends is no payment. Null:
    ///     the rows since its last payment, every scheduled date paid.
    /// </summary>
    public DateTime? RemainingTermAsOfDate { get; set; }

    /// <summary>
    ///     The date the asset's prepayment lockout ends. With its siblings below, the asset's call
    ///     protection: under <c>prepaymentType</c> "CPY" the asset prepays from the month the LATEST of
    ///     the four falls in, and from the first period when it states none. Ignored otherwise.
    /// </summary>
    public DateTime? LockoutEndDate { get; set; }

    /// <summary>The date the asset's defeasance period ends (see <see cref="LockoutEndDate" />).</summary>
    public DateTime? DefeasanceEndDate { get; set; }

    /// <summary>The date the asset's yield-maintenance period ends (see <see cref="LockoutEndDate" />).</summary>
    public DateTime? YieldMaintenanceEndDate { get; set; }

    /// <summary>
    ///     The date the asset's prepayment-premium (fixed penalty) period ends (see
    ///     <see cref="LockoutEndDate" />).
    /// </summary>
    public DateTime? PrepaymentPremiumEndDate { get; set; }

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

    // A balloon loan's amortization schedule (months) when it is longer than the term to
    // maturity — the level payment is computed over it and the remaining balance repays at
    // maturity. Absent: the payment amortizes over the term.
    public int? BalloonAmortizationTerm { get; set; }

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

    // Prepayment convention: "CPR" (default, % of current balance), "ABS" (% of
    // original balance), or "SMM" (direct monthly prepay hazard, no de-annualization).
    // Auto ABS deals use "ABS"; RMBS/agency deals use "CPR"; loan-level monthly
    // hazard models (harmony #1226) use "SMM".
    public string PrepaymentType { get; set; } = "CPR";

    // Default convention: "CDR" (default, annual rate, de-annualized to monthly)
    // or "MDR" (direct monthly default hazard, no de-annualization). Loan-level
    // monthly hazard models (harmony #1226) use "MDR".
    public string DefaultType { get; set; } = "CDR";

    // Weighted average remaining term (months) — used for ABS-to-SMM amortization adjustment.
    public int Wam { get; set; } = 0;

    // Recovery lag in months (graam-harmony #3449): recoveries on a period-t
    // default are placed at period t + RecoveryLag (the liquidation timeline).
    // 0 = same-period recovery.
    public int RecoveryLag { get; set; } = 0;

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

    /// <summary>
    ///     What the run priced its floating indices on (graam-flows#102). Present on every
    ///     successful response: a consumer that needs to know whether a number rests on a supplied
    ///     index or an assumed one reads <see cref="MarketRateResolutionDto.IndexSource" />.
    /// </summary>
    public MarketRateResolutionDto? MarketRateResolution { get; set; }

    /// <summary>
    ///     The prepayment convention the run applied. Present on every successful response, so a
    ///     consumer reads which convention ran instead of inferring it from the speeds; under CPY it
    ///     also says which assets had no call-protection date and so prepaid from the first period.
    /// </summary>
    public PrepaymentResolutionDto? PrepaymentResolution { get; set; }
}

public class PrepaymentResolutionDto
{
    /// <summary>The convention applied: "CPR", "ABS", "SMM" or "CPY".</summary>
    public string PrepaymentType { get; set; } = "CPR";

    /// <summary>Under CPY, the assets that stated at least one call-protection end date. Null otherwise.</summary>
    public int? AssetsWithCallProtectionDates { get; set; }

    /// <summary>
    ///     Under CPY, the assets that stated none and so prepaid from the first period. Null otherwise.
    /// </summary>
    public int? AssetsWithoutCallProtectionDates { get; set; }

    /// <summary>
    ///     The ids of the first <see cref="MaxListedAssets" /> of
    ///     <see cref="AssetsWithoutCallProtectionDates" />.
    /// </summary>
    public List<string> AssetIdsWithoutCallProtectionDates { get; set; } = new();

    public const int MaxListedAssets = 20;
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

    /// <summary>
    ///     Recovery spent on reinvestment purchases (graam-harmony#5596). Returned so the identity
    ///     <c>defaulted = collateralLoss + recoveryPrincipal + reinvestedRecoveryPrincipal</c> is
    ///     checkable on a reinvesting run. Zero otherwise.
    /// </summary>
    public double ReinvestedRecoveryPrincipal { get; set; }

    public double CollateralLoss { get; set; }
    public double DelinqBalance { get; set; }
    public double ForbearanceRecovery { get; set; }
    public double ForbearanceLiquidated { get; set; }
    public double ForbearanceUnscheduled { get; set; }
    public double AccumForbearance { get; set; }
    public double Wac { get; set; }

    /// <summary>
    ///     WAC net of the servicing fee — <c>NetInterest * 1200 / BeginBalance</c>, computed on
    ///     every period alongside <see cref="Wac" />. It was the one wavg the mapper never
    ///     carried, so a consumer rendering a Net WAC column had nothing to render.
    /// </summary>
    public double NetWac { get; set; }

    /// <summary>
    ///     The WAC the WATERFALL actually distributed on, stamped by the structure during the
    ///     run. Zero on <c>/api/CalcCollateral</c>, which returns collateral before any waterfall
    ///     has touched it; real on the collateral the waterfall returns.
    /// </summary>
    public double EffectiveWac { get; set; }

    public double Wam { get; set; }
    public double Wala { get; set; }
    public double Vpr { get; set; }
    public double Cdr { get; set; }
    public double Sev { get; set; }
    public double Dq { get; set; }
    public double CumDefaultedPrincipal { get; set; }
    public double CumDefaultedPrincipalPct { get; set; }
    public double CumCollateralLoss { get; set; }
    public double CumCollateralLossPct { get; set; }
    public double UnAdvancedPrincipal { get; set; }
    public double UnAdvancedInterest { get; set; }
    public double AdvancedPrincipal { get; set; }
    public double AdvancedInterest { get; set; }
    public double Expenses { get; set; }

    /// <summary>
    ///     The period's Modification Loss Amount, for a deal that states a Modification Loss
    ///     Priority (agency CRT). Optional and zero by default, so every existing caller is
    ///     unaffected. Posted rather than derived: it is a function of each loan's Original and
    ///     Current Accrual Rates, which the waterfall never sees.
    /// </summary>
    public double ModificationLoss { get; set; }
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