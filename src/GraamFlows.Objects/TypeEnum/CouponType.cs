namespace GraamFlows.Objects.TypeEnum;

public enum CouponType
{
    None,
    Fixed,
    Floating,
    TrancheWac,
    Formula,

    /// <summary>
    /// The monthly-excess-cashflow strip (Class XS): sweeps whatever interest is
    /// left after the coupon-bearing classes are paid, and is the economic
    /// first-loss layer — it absorbs the period loss out of that excess spread
    /// before any funded bond is written down. Was previously modelled as
    /// <c>ResidualInterest</c>; that legacy string is aliased to this in the
    /// tranche parser for back-compat.
    /// </summary>
    ExcessInterest,

    /// <summary>
    /// The REMIC residual (Class R): the terminal catch-all that receives any
    /// principal/interest left unallocated after the waterfall runs. It is never
    /// a first-loss layer and never absorbs a principal writedown.
    /// </summary>
    Residual
}