namespace GraamFlows.Objects.DataObjects;

/// <summary>
/// A performance fee taken from the residual (equity) class's distributions once that class has
/// earned a hurdle internal rate of return — the "incentive management fee" of a CLO.
///
/// Each Payment Date, after every other step (including a termination payoff), the residual
/// class's distributions are split: first, whatever it still needs to reach the hurdle IRR on
/// <see cref="Investment" /> made on <see cref="InvestmentDate" />; then <see cref="SharePct" />
/// of the remainder is the fee and the rest stays with the residual. Interest is split before
/// principal, as an interest priority of payments runs before a principal one. The IRR is an
/// XIRR: actual days over 365, annual compounding, on unadjusted Payment Dates.
///
/// The amount still needed on date t is
/// <c>(Investment − Σ prior distributions × (1 + h)^(−tᵢ)) × (1 + h)^t</c>, so the state carried
/// between periods is one running present value. See <c>IncentiveFeeHurdle</c>.
/// </summary>
public record IncentiveFeeConfig
{
    /// <summary>The residual (equity) class whose distributions are measured and shared.</summary>
    public string ResidualTranche { get; init; } = "";

    /// <summary>The expense the fee is booked to (declared on the deal, typically formula "0").</summary>
    public string FeeExpense { get; init; } = "";

    /// <summary>Hurdle IRR, percent per annum (12.0 means 12%).</summary>
    public double HurdleIrrPct { get; init; }

    /// <summary>Share of distributions above the hurdle paid as the fee, percent (20.0 means 20%).</summary>
    public double SharePct { get; init; }

    /// <summary>The amount the residual is deemed purchased for.</summary>
    public double Investment { get; init; }

    /// <summary>The date of that purchase (the IRR's time zero).</summary>
    public DateTime InvestmentDate { get; init; }

    /// <summary>
    /// Distributions the residual received BEFORE the projection (a seasoned deal), counted toward
    /// the hurdle. Empty for a deal projected from its closing.
    /// </summary>
    public IReadOnlyList<(DateTime Date, double Amount)> PriorDistributions { get; init; } =
        Array.Empty<(DateTime, double)>();
}
