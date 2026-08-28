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
    /// RE-DATE the i-th pre-first-pay collateral period onto the i-th pay date, so each
    /// collateral month funds exactly one distribution. THE DEFAULT, and the only policy
    /// that satisfies both constraints at once: nothing is dropped (principal conserves)
    /// and no distribution receives more than one collateral month (the #2748 ceiling —
    /// a residual/XS class sweeps whatever interest is left, so two months in period 0
    /// pays out more than the pool earned).
    ///
    /// Fold conserves but breaches that ceiling; Drop holds the ceiling but pays the
    /// excluded stub to nobody. Removing this policy and defaulting to either of them was
    /// tried on this branch and both failed — which is what established that it is doing
    /// a job neither replacement does.
    ///
    /// Monthly deals only, exactly as before: for any other PayFrequency this is a no-op
    /// and the fold runs, which is what the engine has always done for quarterly and
    /// semi-annual deals.
    ///
    /// Declared FIRST so `default(FirstPeriodCollateralPolicyEnum)` agrees with the
    /// documented default rather than contradicting it.
    /// </summary>
    Align,

    /// <summary>
    /// Spend the pre-boundary periods in the first distribution. Correct when the
    /// collateral dated before the first pay date is REAL trust property — a deal whose
    /// cut-off precedes its closing collects over that whole window and distributes it
    /// on the first payment date. This is the US ABS/RMBS convention and what Payscen
    /// does.
    ///
    /// Conserves principal, but a distribution may then receive more than one collateral
    /// month — which is the #2748 flood. Correct where the document SAYS the first payment
    /// covers a multi-month window: STACR 2025-DNA1's first Reporting Period runs December
    /// 1 to January 31 and Appendix G prints the resulting two-month Class A-1 payment, so
    /// that deal states Fold and ties to the cent.
    ///
    /// Note this is what the engine already did for any deal whose PayFrequency is not 12,
    /// because the re-timing above is monthly-only.
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
    /// NOT the default, and not a fail-safe. The excluded principal is neither paid nor
    /// written down: the pool balance falls and the bond balance does not, so the stack
    /// ends permanently under-collateralized by exactly the dropped stub, and the classes
    /// that never amortized keep accruing on principal the pool no longer holds. Measured
    /// on a 100M pool with one stub period, run to full amortization, Drop delivers
    /// 1,514,301.28 LESS principal and 1,369,692.94 MORE interest than Fold. The ceiling
    /// it enforces is a FIRST-PERIOD ceiling only.
    ///
    /// Select it only for a pool whose pre-first-pay periods are an artifact of where the
    /// projection was started — where that principal is not the trust's and was never
    /// real. Everywhere else, Fold.
    /// </summary>
    Drop
}
