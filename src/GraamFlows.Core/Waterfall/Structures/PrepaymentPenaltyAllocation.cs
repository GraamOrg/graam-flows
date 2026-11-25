namespace GraamFlows.Waterfall.Structures;

/// <summary>One class's share of the collateral's prepayment penalties, in percent.</summary>
public class PrepaymentPenaltyShare
{
    public PrepaymentPenaltyShare(DynamicClass target, double pct)
    {
        Target = target;
        Pct = pct;
    }

    public DynamicClass Target { get; }
    public double Pct { get; }
}

/// <summary>
///     Where a deal pays the prepayment penalties its collateral collects
///     (<see cref="GraamFlows.Objects.DataObjects.PeriodCashflows.PrepaymentPenalty" />): each named
///     class its share, paid as interest by the PREPAYMENT_PENALTY step ("pay 100% of any
///     Prepayment Penalties ... to Class IO"). The shares total 100%.
///
///     Not an <see cref="IPayable" />. A payable pays interest up to what a class is due, and a
///     penalty is due to no class: an interest-only class's coupon is its notional times its
///     rate, and a penalty paid through it would be capped there and mostly dropped. The step
///     pays each share whole.
/// </summary>
public class PrepaymentPenaltyAllocation
{
    /// <summary>How far the shares may total from 100% (rounding in a stated split).</summary>
    public const double TotalTolerancePct = 1e-6;

    public PrepaymentPenaltyAllocation(IList<PrepaymentPenaltyShare> shares)
    {
        Shares = shares;
    }

    public IList<PrepaymentPenaltyShare> Shares { get; }
}
