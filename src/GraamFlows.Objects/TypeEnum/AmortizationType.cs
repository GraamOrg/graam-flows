namespace GraamFlows.Objects.TypeEnum;

/// <summary>
///     How an asset repays principal over its life. Distinct from
///     <see cref="InterestRateType" /> (which governs the coupon: FRM/ARM/STEP)
///     and from the IO flag (which delays amortization). An asset can be, e.g.,
///     an ARM bullet or a fixed-rate amortizer.
/// </summary>
public enum AmortizationType
{
    /// <summary>
    ///     Standard scheduled amortization (level-pay annuity, or the supplied
    ///     debt service / IO schedule). This is the default and the only
    ///     behavior the engine modeled before bullet/PIK were added.
    /// </summary>
    Amortizing,

    /// <summary>
    ///     No scheduled principal until maturity; the full outstanding balance
    ///     repays at maturity (balloon). Interest accrues on the whole balance
    ///     each period. Typical of term loans / leveraged loans.
    /// </summary>
    Bullet,

    /// <summary>
    ///     Payment-in-kind: the period coupon is capitalized into the balance
    ///     instead of paid as cash (the balance grows by the accrued interest),
    ///     and principal repays at maturity. No interest cashflow while PIK-ing.
    /// </summary>
    Pik
}
