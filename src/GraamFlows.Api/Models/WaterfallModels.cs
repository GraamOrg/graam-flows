using System.Text.Json;
using System.Text.Json.Serialization;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Api.Models;

/// <summary>
/// Deserializes JSON null as 0.0 for non-nullable double fields.
/// </summary>
public class NullableDoubleAsZeroConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return 0.0;
        return reader.GetDouble();
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

// ============== Request Models ==============

public class WaterfallRequest
{
    public List<PeriodCashflowDto> CollateralCashflows { get; set; } = new();
    public DealDto Deal { get; set; } = new();

    public Dictionary<string, List<double[]>>?
        MarketRates { get; set; } // e.g., {"SOFR30Avg": [[0.25, 5.0], [1.0, 4.8]]}

    public DateTime ProjectionDate { get; set; }

    /// <summary>
    ///     Settlement date the caller is measuring from. Optional; falls back to
    ///     <see cref="ProjectionDate" />, which is what this endpoint used unconditionally
    ///     before — so an omitted value keeps the previous behaviour exactly.
    ///
    ///     They are NOT the same thing and callers already distinguish them: harmony's
    ///     sensitivity tie-out projects from 2025-03-02 on OBX 2025-NQM6 and settles on the
    ///     closing date 2025-04-14, and its pricing path settles on TODAY by design
    ///     ("an IC analysis run today should price as of today, not the deal's historical
    ///     closing date"). The projection start is where cashflows begin; settlement is where
    ///     the holder's clock starts.
    ///
    ///     It has been sent on the wire as "settleDate" for some time and silently DROPPED,
    ///     because no property bound it and System.Text.Json ignores unmapped members. Nothing
    ///     broke only because nothing here consumed a settle date the caller chose — the
    ///     moment this endpoint returns anything settle-anchored (a WAL), that silence becomes
    ///     a wrong number.
    /// </summary>
    public DateTime? SettleDate { get; set; }
    public List<TriggerForecastDto>? TriggerForecasts { get; set; }

    /// <summary>
    /// Current tranche factors/balances. Keys are tranche names.
    /// Values can be:
    ///   - A number (factor, e.g., 0.5 means 50% of original balance remaining)
    ///   - An object with "balance" property (explicit current balance, used for OC tranches)
    /// Example: { "A-1": 0.0, "C": 0.028, "CERTIFICATES": { "balance": 45000000 } }
    /// </summary>
    public Dictionary<string, FactorEntry>? Factors { get; set; }
}

/// <summary>
/// Factor entry that can represent either a factor (0-1) or an explicit balance.
/// For regular tranches: currentBalance = originalBalance * factor
/// For OC tranches: use balance directly (originalBalance is typically 0)
/// </summary>
[JsonConverter(typeof(FactorEntryConverter))]
public class FactorEntry
{
    /// <summary>
    /// Factor value (0-1). If set, currentBalance = originalBalance * factor
    /// </summary>
    public double? Factor { get; set; }

    /// <summary>
    /// Explicit current balance. Used for OC/Modeling tranches where factor doesn't apply.
    /// </summary>
    public double? Balance { get; set; }

    /// <summary>
    /// Allows implicit conversion from double for simple factor values
    /// </summary>
    public static implicit operator FactorEntry(double factor) => new() { Factor = factor };
}

public class DealDto
{
    public string DealName { get; set; } = "";
    public List<TrancheDto> Tranches { get; set; } = new();
    public string WaterfallType { get; set; } = "Sequential"; // Sequential, Passthrough, Agency
    public List<DealStructureDto>? DealStructures { get; set; }
    public List<DealStructureDto>? ClassGroups { get; set; } // Alias for DealStructures (JSON schema naming)
    public List<TriggerDto>? Triggers { get; set; }
    public List<PayRuleDto>? PayRules { get; set; }
    public List<DealVariableDto>? DealVariables { get; set; }
    public List<DealVariableDto>? Variables { get; set; } // Alias for DealVariables (JSON schema naming)
    public List<ScheduledVariableDto>? ScheduledVariables { get; set; }
    public List<ExpenseDto>? Expenses { get; set; }
    public List<ExchangeShareDto>? ExchangeShares { get; set; }

    /// <summary>
    ///     Structured waterfall definition (alternative to PayRules DSL).
    ///     If provided, the transformer generates PayRules from this structure.
    /// </summary>
    public WaterfallStructureDto? Waterfall { get; set; }

    /// <summary>
    ///     Unified waterfall with steps-based format.
    ///     Eliminates classGroups - all cashflow distribution (interest, principal, writedown)
    ///     is explicit in the steps array.
    /// </summary>
    public UnifiedWaterfallDto? UnifiedWaterfall { get; set; }

    // Deal metadata
    public string? DealType { get; set; }
    public string? Trustee { get; set; }
    public string? Issuer { get; set; }
    public string? InterestTreatment { get; set; } = "Collateral";
    public double? BalanceAtIssuance { get; set; }

    /// <summary>
    /// Closing/settlement date of the deal. Used to set FirstSettleDate on tranches
    /// for accurate first-period interest accrual. If not set, FirstSettleDate defaults
    /// to FirstPayDate minus one month.
    /// </summary>
    public DateTime? ClosingDate { get; set; }

    /// <summary>
    /// Date from which collateral cashflows participate in distributions.
    ///
    /// The BOND side of this already has a knob — <see cref="ClosingDate"/> sets each
    /// tranche's FirstSettleDate and so its first accrual. The collateral side had none:
    /// the boundary was derived from whichever tranche happened to be FIRST in the list,
    /// snapped to the 1st of its month. That asymmetry is why callers moved the projection
    /// date to express a collateral intent.
    ///
    /// Null keeps the derived boundary, so existing requests are unaffected.
    /// </summary>
    public DateTime? CollateralAccrualStartDate { get; set; }

    /// <summary>
    /// What to do with collateral periods dated before the first distribution:
    /// "Align" (re-date them onto the pay schedule, folding whatever that cannot reach —
    /// the historical behaviour, and THE DEFAULT when omitted), "Fold" (accumulate them
    /// into the first distribution), or "Drop" (exclude them, paying that principal to
    /// nobody and writing it down nowhere).
    ///
    /// Anything else is rejected rather than defaulted, and the rejection happens when the
    /// deal is built rather than when the branch is first reached, so a typo fails on every
    /// deal and not only on one that happens to have a pre-boundary period.
    /// </summary>
    public string? FirstPeriodCollateralPolicy { get; set; }

    /// <summary>
    ///     Revolving / reinvesting collateral-pool configuration (optional).
    ///     Bound but not yet consumed by the engine (graam-flows#48); the
    ///     reinvestment loop is graam-flows#49.
    /// </summary>
    public ReinvestmentDto? Reinvestment { get; set; }
}

/// <summary>
///     Deal-level revolving / reinvesting collateral-pool configuration. During
///     the reinvestment window, eligible principal proceeds buy new collateral
///     (Templates) up to Target, less Holdback.
/// </summary>
public class ReinvestmentDto
{
    /// <summary>End of the reinvestment window (required).</summary>
    public DateTime ReinvestEndDate { get; set; }

    /// <summary>Optional start of the window; defaults to the projection start.</summary>
    public DateTime? ReinvestStartDate { get; set; }

    /// <summary>Plain balance target the pool is reinvested up to. Used when TargetSchedule is empty.</summary>
    public double Target { get; set; }

    /// <summary>Optional per-period balance target (index 0 = first projection period); clamps at the end.</summary>
    public double[]? TargetSchedule { get; set; }

    /// <summary>Fraction of eligible proceeds released instead of reinvested (default 0).</summary>
    public double Holdback { get; set; }

    /// <summary>Reinvest scheduled principal (default true).</summary>
    public bool? ReinvestScheduledPrincipal { get; set; }

    /// <summary>Reinvest prepayments / unscheduled principal (default true).</summary>
    public bool? ReinvestPrepayments { get; set; }

    /// <summary>Reinvest recoveries (default false).</summary>
    public bool? ReinvestRecoveries { get; set; }

    /// <summary>Reinvestment asset templates; allocations sum to 100.</summary>
    public List<ReinvestTemplateDto> Templates { get; set; } = new();
}

/// <summary>
///     One reinvestment asset template. Reuses the asset parameter set plus a
///     purchase price and a synthetic flag.
/// </summary>
public class ReinvestTemplateDto
{
    /// <summary>Share of eligible proceeds for this template, in percent.</summary>
    public double AllocationPct { get; set; }

    /// <summary>Purchase price (percent of par). Ignored for synthetic templates (priced at par).</summary>
    public double Price { get; set; } = 100.0;

    /// <summary>Synthetic (TBA) collateral, priced at par.</summary>
    public bool IsSynthetic { get; set; }

    /// <summary>Coupon style (FRM / ARM / STEP).</summary>
    public InterestRateType InterestRateType { get; set; } = InterestRateType.FRM;

    /// <summary>Principal-repayment style (typically Bullet for reinvested collateral).</summary>
    public AmortizationType AmortizationType { get; set; } = AmortizationType.Bullet;

    /// <summary>Fixed coupon rate (annual %).</summary>
    public double CouponRate { get; set; }

    /// <summary>Forward-curve index for a floating coupon (None for fixed).</summary>
    public MarketDataInstEnum IndexName { get; set; } = MarketDataInstEnum.None;

    /// <summary>Spread over the index for a floating coupon (annual %).</summary>
    public double IndexMargin { get; set; }

    /// <summary>Term to maturity in months.</summary>
    public int TermMonths { get; set; }

    /// <summary>Servicing fee (annual %).</summary>
    public double ServiceFee { get; set; }
}

/// <summary>
/// OC target configuration for auto ABS turbo paydown.
/// Target OC calculation depends on FormulaType:
/// - "max" (default): MAX(TargetPct * PoolBalance, FloorAmt)
/// - "sum_of": TargetPct * PoolBalance + FloorAmt
/// </summary>
public class OcTargetDto
{
    /// <summary>Target OC as percentage of pool balance (e.g., 0.2335 = 23.35%)</summary>
    public double TargetPct { get; set; }

    /// <summary>Floor OC amount in dollars (e.g., 9058854)</summary>
    public double FloorAmt { get; set; }

    /// <summary>Optional: Floor as percentage of cutoff balance (e.g., 0.015 = 1.5%)</summary>
    public double? FloorPct { get; set; }

    /// <summary>Optional: Cutoff pool balance for floor calculation</summary>
    public double? CutoffBalance { get; set; }

    /// <summary>
    /// Formula type for OC target calculation:
    /// - "max" (default): Target OC = MAX(TargetPct * PoolBalance, FloorAmt)
    /// - "sum_of": Target OC = TargetPct * PoolBalance + FloorAmt
    /// </summary>
    public string? FormulaType { get; set; }

    /// <summary>
    /// If true, OC target is based on cutoff/initial pool balance (static target).
    /// If false (default), OC target is based on current pool balance (dynamic target).
    /// Most auto ABS deals use current pool balance: "X% of aggregate Collateral Balance
    /// as of the end of the related collection period."
    /// </summary>
    public bool UseInitialBalance { get; set; }
}

public class DealVariableDto
{
    public string VariableName { get; set; } = "";
    public string VariableValue { get; set; } = "";
    public string? VariableValue2 { get; set; }
    public string GroupNum { get; set; } = "1";
    public bool IsForecastable { get; set; } = false;
}

public class ScheduledVariableDto
{
    public string VariableName { get; set; } = "";
    public DateTime BeginDate { get; set; }
    public DateTime EndDate { get; set; }
    public double Value { get; set; }
    public int GroupNum { get; set; } = 1;
    public string? Description { get; set; }
}

public class ExpenseDto
{
    public string ExpenseName { get; set; } = "";
    public string? Description { get; set; }
    public string Formula { get; set; } = "";
    public int GroupNum { get; set; } = 1;
}

public class ExchangeShareDto
{
    public string ExchangeTranche { get; set; } = "";
    public List<ExShareDto> Shares { get; set; } = new();
}

public class ExShareDto
{
    public string TrancheName { get; set; } = "";
    public double ShareAmount { get; set; }
}

public class TrancheDto
{
    public string TrancheName { get; set; } = "";

    [System.Text.Json.Serialization.JsonConverter(typeof(NullableDoubleAsZeroConverter))]
    public double OriginalBalance { get; set; }
    public double Factor { get; set; } = 1.0;
    public string CouponType { get; set; } = "Fixed"; // Fixed, Floating, TrancheWac, None, ResidualInterest
    public double? FixedCoupon { get; set; }
    public double? FloaterSpread { get; set; }
    public string? FloaterIndex { get; set; } // Libor1M, Sofr30Avg, etc.
    public double Cap { get; set; } = 100.0;
    public double Floor { get; set; } = 0.0;
    /// <summary>
    /// Payments per year (12 = monthly, 4 = quarterly, 2 = semi-annual, 1 = annual).
    /// This is NOT months between payments. Must match the cadence of the collateral
    /// cashflows — <c>/api/calccollateral</c> emits monthly cashflows, so tranches
    /// settled from that output should use 12.
    /// </summary>
    public int PayFrequency { get; set; } = 12;
    public int PayDelay { get; set; } = 0;
    public int PayDay { get; set; } = 25;
    public string DayCount { get; set; } = "30/360";
    public string BusinessDayConvention { get; set; } = "Following";
    public string CashflowType { get; set; } = "PI"; // PI, IO, PO
    public string TrancheType { get; set; } = "Offered"; // Offered, Residual, Notional
    public string? ClassReference { get; set; }

    // #3691: for a class-cut IO strip (TrancheType="Notional"), the funded bond whose
    // amortization its notional tracks (e.g. A-1AX -> A-1A). Distinct from ClassReference
    // (class-GROUP membership); this becomes the DealStructure's ExchangableTranche.
    public string? NotionalReference { get; set; }

    public DateTime? FirstPayDate { get; set; }
    public DateTime? StatedMaturityDate { get; set; }
    public DateTime? LegalMaturityDate { get; set; }
    public string? CouponFormula { get; set; }
    public int SubordinationOrder { get; set; } // 0 = most senior
    public string GroupNum { get; set; } = "0";
    public int InterestPriority { get; set; } = 0;
    public ReserveAccountConfigDto? ReserveConfig { get; set; }
}

/// <summary>
/// Reserve account configuration DTO for JSON deserialization.
/// </summary>
public class ReserveAccountConfigDto
{
    /// <summary>Target reserve as percentage of base balance (e.g., 0.01 = 1.00%)</summary>
    public double TargetPct { get; set; }

    /// <summary>Base for target calculation: "CutoffPoolBalance" or "CurrentPoolBalance"</summary>
    public string? TargetBase { get; set; }

    /// <summary>Pool balance as of cutoff date</summary>
    public double? CutoffPoolBalance { get; set; }

    /// <summary>If true, reserve balance cannot exceed aggregate note principal</summary>
    public bool? CapAtNoteBalance { get; set; }
}

public class DealStructureDto
{
    public string ClassGroupName { get; set; } = "";
    public int SubordinationOrder { get; set; }
    public string PayFrom { get; set; } = "Sequential"; // Sequential, ProRata, Rule, Accrual, Notional
    public string GroupNum { get; set; } = "0";
    public string? ExchangableTranche { get; set; }
    public string? ClassTags { get; set; }
}

public class TriggerDto
{
    public string TriggerName { get; set; } = "";
    public string? Description { get; set; }

    public string TriggerType { get; set; } =
        "FORMULA_CONDITION"; // FORMULA_CONDITION, FORMULA_VALUE, FORMULA_VOID, FORMULA_CONDITION_STICKY, DATE_TERMINATION, COLLATERAL_VALUE, CREDIT_ENHANCEMENT, DELINQ_TRIGGER_SUB_6

    public string? TriggerFormula { get; set; }
    public string? TriggerParam { get; set; }
    public string? TriggerParam2 { get; set; }
    public bool IsMandatory { get; set; } = false;
    public string? PossibleValues { get; set; }
    public string GroupNum { get; set; } = "1";
}

public class PayRuleDto
{
    public string RuleName { get; set; } = "";
    public string ClassGroupName { get; set; } = "";
    public string Formula { get; set; } = "";
    public int Priority { get; set; }
    public int GroupNum { get; set; } = 0;
}

public class TriggerForecastDto
{
    public string TriggerName { get; set; } = "";
    public int GroupNum { get; set; } = 0;
    public bool AlwaysTrigger { get; set; }
}

// ============== Structured Waterfall Models ==============
// These provide an LLM-friendly alternative to DSL-based PayRules

/// <summary>
///     Structured waterfall definition that transforms to PayRules DSL
/// </summary>
public class WaterfallStructureDto
{
    public WaterfallPrincipalDto? ScheduledPrincipal { get; set; }
    public object? UnscheduledPrincipal { get; set; } // Can be WaterfallPrincipalDto or "same"
    public object? RecoveryPrincipal { get; set; } // Can be WaterfallPrincipalDto or "same"
    public PayableStructureDto? ReservePrincipal { get; set; }
}

/// <summary>
///     Principal waterfall with optional trigger-dependent structures
/// </summary>
public class WaterfallPrincipalDto
{
    public PayableStructureDto? Default { get; set; }
    public TriggerConditionDto? OnTriggerFail { get; set; }
}

/// <summary>
///     Trigger condition for alternative waterfall structure
/// </summary>
public class TriggerConditionDto
{
    public List<string> Triggers { get; set; } = new();
    public string Condition { get; set; } = "ANY"; // ANY or ALL
    public PayableStructureDto? Structure { get; set; }
}

/// <summary>
///     Payment structure node (mirrors IPayable XML format).
///     Supported types: SEQ, PRORATA, SINGLE, SHIFTI, ACCRETE, CSCAP, FIXED, FORCE_PAYDOWN
/// </summary>
public class PayableStructureDto
{
    public string Type { get; set; } = "";
    public List<PayableStructureDto>? Children { get; set; }
    public List<string>? Tranches { get; set; } // Shorthand for PRORATA with SINGLE children
    public string? Tranche { get; set; } // For SINGLE type
    public double? ShiftPercent { get; set; } // For SHIFTI (0-1)
    public string? ShiftVariable { get; set; } // Variable name for dynamic shift %
    public PayableStructureDto? Seniors { get; set; } // For SHIFTI
    public PayableStructureDto? Subordinates { get; set; } // For SHIFTI

    // CSCAP (Credit Support Cap / Enhancement Cap) fields
    public string? CapVariable { get; set; } // Variable name for cap percentage
    public double? CapPercent { get; set; } // Constant cap percentage
    public PayableStructureDto? Primary { get; set; } // Primary payable (seniors)
    public PayableStructureDto? Cap { get; set; } // Cap payable (subordinates that receive excess)

    // FIXED (Fixed Amount) fields
    public string? FixedVariable { get; set; } // Variable name for fixed dollar amount
    public double? FixedAmount { get; set; } // Constant fixed dollar amount
    // Primary = tranche receiving fixed amount, Overflow = remainder destination
    public PayableStructureDto? Overflow { get; set; }

    // FORCE_PAYDOWN fields
    public PayableStructureDto? Forced { get; set; } // Tranche forced to pay down
    public PayableStructureDto? Support { get; set; } // Receives remainder
}

// ============== Unified Waterfall Models ==============
// Steps-based waterfall that eliminates classGroups dependency

/// <summary>
///     Unified waterfall with ordered steps.
///     Step order defines execution order - fully configurable.
/// </summary>
public class UnifiedWaterfallDto
{
    public List<WaterfallStepDto> Steps { get; set; } = new();

    /// <summary>
    /// Explicit execution order for ComposableStructure.
    /// If not provided, order is inferred from Steps array.
    /// Example: ["EXPENSE", "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED",
    ///           "PRINCIPAL_RECOVERY", "RESERVE", "WRITEDOWN", "EXCESS"]
    /// </summary>
    public List<string>? ExecutionOrder { get; set; }

    /// <summary>
    /// Controls interleaving of INTEREST and PRINCIPAL steps by seniority level.
    /// "standard" (default): all interest then all principal.
    /// "interestFirst": per seniority level, pay interest then principal.
    /// "principalFirst": per seniority level, pay principal then interest.
    /// </summary>
    public string? WaterfallOrder { get; set; }

    /// <summary>
    /// Variables computed dynamically each period before waterfall execution.
    /// Each computed variable has conditional rules (first match wins).
    /// Used for STACR-style deals where variables like SenRedu depend on trigger state.
    /// </summary>
    public List<ComputedVariableDto>? ComputedVariables { get; set; }

    /// <summary>
    ///     CLO per-level OC/IC coverage tests, ordered senior→junior. Each failing
    ///     level diverts available interest to sequential (senior-first) principal
    ///     paydown of the note stack before any junior level's interest is paid.
    ///     Distinct from the single-level RMBS-style OC turbo (EXCESS_TURBO/ocTarget).
    /// </summary>
    public List<CoverageLevelDto>? CoverageCascade { get; set; }
}

/// <summary>
///     One level of the CLO coverage cascade. The OC numerator is the deal's "ACPA"
///     scheduled/deal variable when present (harmony computes/discloses any CCC
///     haircut), else the current collateral balance.
/// </summary>
public class CoverageLevelDto
{
    /// <summary>Level name, e.g. "A/B", "C", "D", "E".</summary>
    public string Level { get; set; } = "";

    /// <summary>Note classes AT OR ABOVE this level (the OC denominator set), senior-first.</summary>
    public List<string> Tranches { get; set; } = new();

    /// <summary>OC trigger in percent (121.58 means 121.58%); null = no OC test at this level.</summary>
    public double? OcTriggerPct { get; set; }

    /// <summary>IC trigger in percent; null = no IC test at this level.</summary>
    public double? IcTriggerPct { get; set; }
}

/// <summary>
/// A variable computed dynamically each period using conditional rules.
/// </summary>
public class ComputedVariableDto
{
    public string Name { get; set; } = "";
    public List<ComputedVariableRuleDto> Rules { get; set; } = new();
}

/// <summary>
/// A conditional rule for computing a variable value.
/// If "when" is null/absent, this is the unconditional fallback (must be last).
/// </summary>
public class ComputedVariableRuleDto
{
    public RuleConditionDto? When { get; set; }
    public string Formula { get; set; } = "";
}

/// <summary>
///     A single step in the waterfall execution order.
///     Step types: INTEREST, PRINCIPAL, WRITEDOWN, EXCESS
/// </summary>
public class WaterfallStepDto
{
    /// <summary>
    ///     Step type: INTEREST, PRINCIPAL, WRITEDOWN, EXCESS
    /// </summary>
    public string Type { get; set; } = "";

    /// <summary>
    ///     Payable structure for INTEREST, WRITEDOWN, EXCESS steps
    /// </summary>
    public PayableStructureDto? Structure { get; set; }

    /// <summary>
    ///     For PRINCIPAL steps: scheduled, unscheduled, recovery
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    ///     Default structure for PRINCIPAL steps (when triggers pass)
    /// </summary>
    public PayableStructureDto? Default { get; set; }

    /// <summary>
    ///     Alternative structure when triggers fail (simple two-branch model)
    /// </summary>
    public TriggerConditionDto? OnTriggerFail { get; set; }

    /// <summary>
    ///     Multi-branch conditional rules (replaces Default/OnTriggerFail for complex deals).
    ///     First matching rule wins. A rule with no "when" is the unconditional fallback (must be last).
    ///     Used for STACR-style deals with multiple trigger/variable combinations.
    /// </summary>
    public List<WaterfallRuleDto>? Rules { get; set; }

    /// <summary>
    ///     Reference to use another source's structure (e.g., "scheduled")
    /// </summary>
    public string? UseStructure { get; set; }

    /// <summary>
    ///     OC target for EXCESS_TURBO step.
    ///     Target OC = MAX(TargetPct * PoolBalance, FloorAmt)
    /// </summary>
    public OcTargetDto? OcTarget { get; set; }

    /// <summary>
    ///     For SUPPLEMENTAL_REDUCTION: variable name for the cap percentage.
    /// </summary>
    public string? CapVariable { get; set; }

    /// <summary>
    ///     For SUPPLEMENTAL_REDUCTION: sub tranche names (cap overflow recipients, e.g. M1/M1H, M2A/M2AH).
    /// </summary>
    public List<string>? OfferedTranches { get; set; }

    /// <summary>
    ///     For SUPPLEMENTAL_REDUCTION: senior-only tranche names (exclusive to primary, e.g. AH, B1H, B2H, B3H).
    /// </summary>
    public List<string>? SeniorTranches { get; set; }

    /// <summary>
    ///     For MODIFICATION_LOSS: the priorities, in document order.
    /// </summary>
    public List<ModificationLossRungDto>? Rungs { get; set; }

    /// <summary>
    ///     For MODIFICATION_LOSS: the class whose Class Notional Amount is INCREASED by the sum of
    ///     the notional priorities (agency CRT's senior reference tranche). Absent leaves the
    ///     notional bites as pure reductions.
    /// </summary>
    public string? WriteUpTranche { get; set; }
}

/// <summary>
///     One priority of a Modification Loss Priority.
/// </summary>
public class ModificationLossRungDto
{
    /// <summary>
    ///     "NOTIONAL" — reduce the Class Notional Amount, capped at the Preliminary Class Notional
    ///     Amount; or "INTEREST" — reduce the Interest Accrual Amount, capped at it.
    /// </summary>
    public string Effect { get; set; } = "";

    /// <summary>
    ///     The class this rung names, for a single-class priority.
    /// </summary>
    public string? Tranche { get; set; }

    /// <summary>
    ///     The classes this rung names pro rata, for a pro-rata priority. Exclusive with
    ///     <see cref="Tranche" />.
    /// </summary>
    public List<string>? Tranches { get; set; }

    /// <summary>
    ///     For an INTEREST priority over a pro-rata pair, the ONE class whose Interest Accrual
    ///     Amount states the cap — the document caps on a member, not on the group. Absent on a
    ///     single-class priority, where the named class IS the capped one.
    /// </summary>
    public string? CapTranche { get; set; }
}

/// <summary>
/// A conditional waterfall rule: if "when" matches, use "structure".
/// Rules are evaluated in order - first match wins.
/// </summary>
public class WaterfallRuleDto
{
    /// <summary>
    /// Condition for this rule. If null/absent, this is the unconditional fallback.
    /// </summary>
    public RuleConditionDto? When { get; set; }

    /// <summary>
    /// Payable structure to use when this rule matches.
    /// </summary>
    public PayableStructureDto Structure { get; set; } = new();
}

/// <summary>
/// Condition combining trigger pass/fail checks and variable comparisons.
/// All conditions within a single RuleConditionDto are ANDed together.
/// </summary>
public class RuleConditionDto
{
    /// <summary>All listed triggers must be passing</summary>
    public List<string>? Pass { get; set; }

    /// <summary>All listed triggers must be failing</summary>
    public List<string>? Fail { get; set; }

    /// <summary>All variable conditions must be true</summary>
    public List<VarConditionDto>? Vars { get; set; }
}

/// <summary>
/// A variable comparison condition (e.g., VAR('A1TurboFlag') > 0.999)
/// </summary>
public class VarConditionDto
{
    public string Var { get; set; } = "";
    public string Op { get; set; } = ">"; // >, <, >=, <=, ==, !=
    public double Value { get; set; }
}

// ============== Response Models ==============

public class WaterfallResponse
{
    public Dictionary<string, List<TrancheCashflowDto>> TrancheCashflows { get; set; } = new();
    public List<TriggerResultDto>? TriggerResults { get; set; }
    public DateTime? TerminationDate { get; set; }
    public WaterfallSummaryDto Summary { get; set; } = new();
}

public class TrancheCashflowDto
{
    public int Period { get; set; }
    public DateTime CashflowDate { get; set; }
    public double BeginBalance { get; set; }
    public double Balance { get; set; }
    public double ScheduledPrincipal { get; set; }
    public double UnscheduledPrincipal { get; set; }
    public double Interest { get; set; }
    public double Coupon { get; set; }
    public double EffectiveCoupon { get; set; }
    public double Expense { get; set; } // Expense amount for expense tranches
    public double ExpenseShortfall { get; set; }
    public double Writedown { get; set; }
    public double CumWritedown { get; set; }

    /// <summary>
    ///     Modification Loss Amount allocated against this class's Interest Accrual Amount for the
    ///     period — interest it was not paid. Not an interest shortfall: it is never repaid out of
    ///     ordinary interest.
    /// </summary>
    public double ModificationLoss { get; set; }

    /// <summary>
    ///     The part of <see cref="Writedown" /> that came from a Modification Loss Amount rather
    ///     than a credit event. NEGATIVE on the class the notional bites are transferred to.
    /// </summary>
    public double ModificationWritedown { get; set; }
    public double Factor { get; set; }
    public double CreditSupport { get; set; }
    public double BeginCreditSupport { get; set; }
    public double InterestShortfall { get; set; }
    public double AccumInterestShortfall { get; set; }
    public double InterestShortfallPayback { get; set; }
    public double ExcessInterest { get; set; }
    public double? IndexValue { get; set; }
    public double FloaterMargin { get; set; }
    public int AccrualDays { get; set; }
    public bool IsLockedOut { get; set; }
}

public class TriggerResultDto
{
    /// <summary>1-based cashflow period — the index of <see cref="CashflowDate"/>
    /// in the projection schedule. All triggers tested on the same date
    /// share the same period number.</summary>
    public int Period { get; set; }
    public DateTime CashflowDate { get; set; }
    public string TriggerName { get; set; } = "";
    /// <summary>True when the trigger test PASSED this period — the
    /// engine's <see cref="DataObjects.TriggerResult.Passed"/>.</summary>
    public bool Triggered { get; set; }
    /// <summary>The actual numeric value the trigger test produced this
    /// period (e.g. realized delinquency, cumulative net loss).</summary>
    public double? Value { get; set; }
    /// <summary>The threshold the trigger compared against (e.g. the
    /// configured delinquency cutoff, the schedule's loss cap).</summary>
    public double? RequiredValue { get; set; }
}

public class WaterfallSummaryDto
{
    public int TotalPeriods { get; set; }
    public Dictionary<string, TrancheSummaryDto> TranchesSummary { get; set; } = new();
}

public class TrancheSummaryDto
{
    /// <summary>
    ///     Weighted average life in years, from the request's settle date, computed by
    ///     <c>Cashflow.WeightedAverageLife()</c> — ActualActual-ISDA, and balance-weighted
    ///     rather than principal-weighted for an IO stream, so a notional strip gets the WAL
    ///     of its notional instead of nothing.
    ///
    ///     This endpoint returned no WAL, so every caller computed its own. harmony grew two
    ///     that disagreed — one anchored at settle, one at <c>period / 12</c> from the
    ///     projection start, which overstated every WAL by the cutoff-to-closing gap — and
    ///     consolidated them into a third that approximates ActualActual-ISDA as
    ///     actual/365.25. Returning the engine's own number is what stops the fourth.
    /// </summary>
    public double? Wal { get; set; }

    /// <summary>
    ///     WAL from the balance change (<c>PrevBalance - Balance</c>) on a Thirty360Us day
    ///     count — <c>Cashflow.BalanceWeightedAverageLife()</c>. Distinct from
    ///     <see cref="Wal" /> in BOTH weighting and day count, and the convention prospectus
    ///     decrement tables are usually quoted on.
    /// </summary>
    public double? BalanceWal { get; set; }

    public double TotalPrincipal { get; set; }
    public double TotalInterest { get; set; }
    public double TotalExpense { get; set; } // Total expense for expense tranches
    public double TotalWritedown { get; set; }
    public double WritedownPct { get; set; }
    public double FinalBalance { get; set; }
    public double FinalFactor { get; set; }
}