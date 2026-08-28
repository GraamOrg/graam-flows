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
    /// Spend the pre-boundary periods in the first distribution. Correct when the
    /// collateral dated before the first pay date is REAL trust property — a deal whose
    /// cut-off precedes its closing collects over that whole window and distributes it
    /// on the first payment date. This is the US ABS/RMBS convention and what Payscen
    /// does.
    /// </summary>
    Fold,

    /// <summary>
    /// Exclude them entirely; the first distribution spends exactly one period. Correct
    /// when the periods ahead of the first pay date are an artifact of where the
    /// projection was started rather than real collections — the amortizer emits a full
    /// month of interest for every period it is given, so a pool handed the closing date
    /// produces a "stub" month that the trust never earned. Folding it made the residual
    /// (XS) sweep ~2 months of collateral interest in period 0 (harmony #2748).
    ///
    /// This is the DEFAULT, because it is the fail-safe direction: a distribution can
    /// never pay out more than the pool earned in one period. A deal whose cut-off
    /// genuinely precedes its closing owns that window and must say so — state the
    /// cut-off as CollateralAccrualStartDate and select Fold.
    /// </summary>
    Drop
}
