namespace GraamFlows.Objects.TypeEnum;

/// <summary>
/// What becomes of collateral periods dated BEFORE the first distribution.
///
/// A deal's cut-off date normally precedes its closing, so the first Payment Date
/// distributes more than one period of collections. Which of those periods the
/// waterfall may spend is a modelling choice, not a fact, and the two defensible
/// answers pull in opposite directions: folding them in floods a residual/excess
/// tranche whose bonds accrue only one period (harmony #1714), while dropping them
/// starves a senior class whose first scheduled payment assumes the whole window
/// (harmony #2454). Callers previously expressed this by MOVING the projection
/// date, which is a different variable — hence this enum.
/// </summary>
public enum FirstPeriodCollateralPolicyEnum
{
    /// <summary>
    /// RE-DATE the collateral 1:1 onto the pay schedule, so each collateral month funds
    /// exactly one distribution and nothing is left before the boundary. This is what the
    /// engine actually does today (`AlignStubPeriodsToPaySchedule`, added for harmony
    /// #2748 to stop the residual sweeping ~2 months of interest in period 0) and so it
    /// is the default. Note it PRE-EMPTS Fold: with alignment on, the fold below never
    /// fires, which is why Fold and Drop were previously indistinguishable.
    /// </summary>
    Align,

    /// <summary>
    /// Accumulate the pre-first-pay periods into the first distribution — what Payscen
    /// does, and what a deal whose Appendix G ladder assumes the whole cut-off-to-closing
    /// window needs. Requires alignment to be off, which selecting this does.
    /// </summary>
    Fold,

    /// <summary>Exclude them entirely; the first distribution spends one period.</summary>
    Drop
}
