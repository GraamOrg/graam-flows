using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Objects.DataObjects;

/// <summary>
///     Template describing the collateral bought when reinvesting. It reuses the
///     existing asset parameter set (coupon, index+margin, amortization type,
///     term, servicing fee) plus a purchase <see cref="Price" /> and a synthetic
///     flag. The reinvestment loop (graam-flows#49) instantiates an asset from
///     this template and the reinvested balance; this object carries no balance
///     of its own. Synthetic (TBA) templates are priced at par — see
///     <see cref="EffectivePrice" />.
/// </summary>
public record ReinvestTemplate
{
    /// <summary>
    ///     Share of eligible proceeds routed to this template, in percent. The
    ///     allocations across a config's templates sum to 100.
    /// </summary>
    public double AllocationPct { get; init; }

    /// <summary>
    ///     Purchase price as a percent of par (e.g. 99.5). Ignored for synthetic
    ///     templates, which are priced at par via <see cref="EffectivePrice" />.
    /// </summary>
    public double Price { get; init; } = 100.0;

    /// <summary>Synthetic (TBA) collateral. Priced at par regardless of <see cref="Price" />.</summary>
    public bool IsSynthetic { get; init; }

    /// <summary>Coupon style of the reinvested asset (FRM / ARM / STEP).</summary>
    public InterestRateType InterestRateType { get; init; } = InterestRateType.FRM;

    /// <summary>Principal-repayment style. Reinvested collateral is typically bullet.</summary>
    public AmortizationType AmortizationType { get; init; } = AmortizationType.Bullet;

    /// <summary>Fixed coupon rate (annual %), used when the coupon is fixed.</summary>
    public double CouponRate { get; init; }

    /// <summary>Forward-curve index for a floating coupon (None for fixed).</summary>
    public MarketDataInstEnum IndexName { get; init; } = MarketDataInstEnum.None;

    /// <summary>Spread over the index for a floating coupon (annual %).</summary>
    public double IndexMargin { get; init; }

    /// <summary>Term to maturity in months.</summary>
    public int TermMonths { get; init; }

    /// <summary>Servicing fee (annual %).</summary>
    public double ServiceFee { get; init; }

    /// <summary>
    ///     The price actually used to buy this collateral: par (100) for
    ///     synthetic templates, otherwise <see cref="Price" />. Use this rather
    ///     than <see cref="Price" /> so a mis-supplied synthetic price cannot
    ///     leak into the reinvestment math.
    /// </summary>
    public double EffectivePrice => IsSynthetic ? 100.0 : Price;
}
