namespace GraamFlows.Domain.Waterfall;

// Typed AST for the unified waterfall. Replaces the
// UnifiedWaterfallBuilder -> PayRule DSL -> Roslyn pipeline:
// JSON deserializes directly into these records, and the executor
// walks the AST. No intermediate representations, no runtime
// compilation, no reflection invoke.
//
// Custom JsonConverters in GraamFlows.Api.Models.Waterfall handle
// the mapping between the existing unified-waterfall JSON shape
// (which uses a fat optional-field record with a `type`
// discriminator) and these tagged-union records.

public sealed record UnifiedWaterfallAst(
    IReadOnlyList<WaterfallStep> Steps,
    IReadOnlyList<string>? ExecutionOrder = null,
    WaterfallOrdering WaterfallOrder = WaterfallOrdering.Standard,
    IReadOnlyList<ComputedVariable>? ComputedVariables = null);

public enum WaterfallOrdering
{
    Standard,
    InterestFirst,
    PrincipalFirst
}

// ---------- Steps ----------

public abstract record WaterfallStep;

public sealed record InterestStep(PayableExpr Structure) : WaterfallStep;

public sealed record PrincipalStep(
    PrincipalSource Source,
    IReadOnlyList<ConditionalStructure> Rules,
    string? UseStructure = null) : WaterfallStep;

public enum PrincipalSource
{
    Scheduled,
    Unscheduled,
    Recovery
}

public sealed record WritedownStep(PayableExpr Structure) : WaterfallStep;

public sealed record ExpenseStep(
    string ExpenseName,
    PayableExpr? Payees = null) : WaterfallStep;

public sealed record ReserveDepositStep(
    string Account,
    AmountExpr Amount) : WaterfallStep;

public sealed record ExcessStep(PayableExpr Structure) : WaterfallStep;

public sealed record ExcessTurboStep(
    PayableExpr Structure,
    OcTarget? OcTarget = null) : WaterfallStep;

public sealed record ExcessReleaseStep(PayableExpr Structure) : WaterfallStep;

public sealed record CapCarryoverStep(PayableExpr Structure) : WaterfallStep;

public sealed record SupplementalReductionStep(
    PayableExpr Structure,
    string? CapVariable = null,
    IReadOnlyList<string>? OfferedTranches = null,
    IReadOnlyList<string>? SeniorTranches = null) : WaterfallStep;

// A structure paired with an optional gating condition. Used by
// PrincipalStep to express multi-branch rules (first-match-wins).
// A rule with When == null is the unconditional fallback and must
// be last.
public sealed record ConditionalStructure(
    PayableExpr Structure,
    Condition? When = null);

public sealed record OcTarget(
    double TargetPct,
    double FloorAmt,
    double? FloorPct = null,
    double? CutoffBalance = null,
    OcTargetFormula Formula = OcTargetFormula.Max,
    bool UseInitialBalance = false);

public enum OcTargetFormula
{
    Max,
    SumOf
}

// ---------- Payable structures ----------

public abstract record PayableExpr;

public sealed record SinglePayable(string Tranche) : PayableExpr;

public sealed record SeqPayable(
    IReadOnlyList<PayableExpr> Children) : PayableExpr;

public sealed record ProrataPayable(
    IReadOnlyList<PayableExpr> Children) : PayableExpr;

public sealed record ShiftIPayable(
    AmountExpr ShiftPct,
    PayableExpr Seniors,
    PayableExpr Subordinates) : PayableExpr;

public sealed record CsCapPayable(
    AmountExpr CapPct,
    PayableExpr Primary,
    PayableExpr Cap) : PayableExpr;

public sealed record FixedPayable(
    AmountExpr Amount,
    PayableExpr Primary,
    PayableExpr Overflow) : PayableExpr;

public sealed record ForcePaydownPayable(
    PayableExpr Forced,
    PayableExpr Support) : PayableExpr;

public sealed record ProformaPayable(
    IReadOnlyList<ProformaPart> Parts) : PayableExpr;

public sealed record ProformaPart(PayableExpr Child, double Share);

public sealed record AccretePayable(string Tranche) : PayableExpr;

// ---------- Amount expressions ----------
//
// Unifies the const/variable dichotomy that the existing DTO
// spreads across sibling fields (shiftPercent/shiftVariable,
// capPercent/capVariable, fixedAmount/fixedVariable). The
// converter reads whichever field is present and produces one of
// these.

public abstract record AmountExpr;

public sealed record AmountConst(double Value) : AmountExpr;

public sealed record AmountVar(string Name) : AmountExpr;

// ---------- Conditions ----------
//
// All fields are ANDed together, matching the existing
// RuleConditionDto semantics.

public sealed record Condition(
    IReadOnlyList<string>? Pass = null,
    IReadOnlyList<string>? Fail = null,
    IReadOnlyList<VarCondition>? Vars = null);

public sealed record VarCondition(
    string Var,
    CompareOp Op,
    double Value);

public enum CompareOp
{
    Gt,
    Lt,
    Ge,
    Le,
    Eq,
    Ne
}

// ---------- Computed variables ----------
//
// A computed variable is evaluated before the waterfall runs each
// period. Its rules are first-match-wins, matching the existing
// ComputedVariableDto semantics. Formula is kept as a string for
// this first pass; a follow-up commit will lift it into an
// AmountExpr once the executor is wired up.

public sealed record ComputedVariable(
    string Name,
    IReadOnlyList<ComputedVariableRule> Rules);

public sealed record ComputedVariableRule(
    string Formula,
    Condition? When = null);
