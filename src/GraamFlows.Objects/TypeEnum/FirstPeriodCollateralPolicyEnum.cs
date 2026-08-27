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
    /// <summary>Accumulate them into the first distribution. The historical behaviour.</summary>
    Fold,

    /// <summary>Exclude them entirely; the first distribution spends one period.</summary>
    Drop
}
