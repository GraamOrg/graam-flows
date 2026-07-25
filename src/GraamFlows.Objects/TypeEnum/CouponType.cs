namespace GraamFlows.Objects.TypeEnum;

public enum CouponType
{
    None,
    Fixed,
    Floating,
    TrancheWac,
    Formula,
    ResidualInterest,

    /// <summary>
    ///     The class accrues at the collateral pool's net WAC (gross WAC less
    ///     servicing/fees) each period — the standard "Net WAC" passthrough coupon
    ///     carried by NQM subordinates (e.g. B-2/B-3). Distinct from
    ///     <see cref="TrancheWac" />, which is a balance-weighted coupon of a named
    ///     set of tranches. The per-period value comes from
    ///     DynamicGroup.CollateralNetWac.
    /// </summary>
    NetWac
}