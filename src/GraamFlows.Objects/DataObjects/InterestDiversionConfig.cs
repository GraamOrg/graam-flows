namespace GraamFlows.Objects.DataObjects;

/// <summary>
/// A CLO's reinvestment-period interest diversion test: on each Payment Date up to
/// <see cref="EndDate" />, if the overcollateralization ratio of <see cref="Tranches" /> is below
/// <see cref="TriggerPct" />, the lesser of <see cref="MaxPctOfInterest" /> of the interest left
/// at that point of the priority of payments and the cash needed to restore the ratio is diverted
/// to buy additional collateral, instead of reaching the residual.
///
/// Distinct from a coverage-cascade level (<see cref="CoverageLevelConfig" />), whose cure PAYS
/// DOWN notes: this cure BUYS collateral, so the waterfall cannot settle it alone — the diverted
/// cash becomes a reinvestment purchase, whose collateral changes later periods. The caller runs
/// the waterfall to a fixed point (<c>CfCore.RunReinvestingWaterfall</c>).
///
/// The ratio's numerator is the coverage tests' (the deal's ACPA variable, or the collateral
/// balance plus the period's principal collections) LESS any collateral this same diversion
/// bought on the date: the test is measured before the cure, as a Determination Date test is.
/// </summary>
public record InterestDiversionConfig
{
    /// <summary>The note classes whose balance is the ratio's denominator (e.g. every class through E).</summary>
    public IReadOnlyList<string> Tranches { get; init; } = Array.Empty<string>();

    /// <summary>The trigger, percent (103.75 means 103.75%).</summary>
    public double TriggerPct { get; init; }

    /// <summary>The most that can be diverted, as a percent of the interest remaining at the test.</summary>
    public double MaxPctOfInterest { get; init; } = 100.0;

    /// <summary>The last Payment Date on which the test applies (the reinvestment period's end).</summary>
    public DateTime EndDate { get; init; }

    /// <summary>
    /// Price (percent of par) the diverted cash buys collateral at — the cure buys par / price.
    /// Defaults to the reinvestment templates' allocation-weighted price.
    /// </summary>
    public double PurchasePricePct { get; init; } = 100.0;
}

/// <summary>One Payment Date on which the interest diversion test failed.</summary>
public record InterestDiversionResult
{
    public DateTime Date { get; init; }

    /// <summary>The ratio before the cure, percent.</summary>
    public double RatioPct { get; init; }

    /// <summary>The cash that would restore the trigger.</summary>
    public double CureCash { get; init; }

    /// <summary>What was diverted: the lesser of the cure and the cap.</summary>
    public double Diverted { get; init; }
}
