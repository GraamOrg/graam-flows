using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Objects.DataObjects;

/// <summary>
///     Deal-level configuration for a revolving / reinvesting collateral pool.
///     During the reinvestment window, eligible principal proceeds buy new
///     collateral (from <see cref="Templates" />) up to a plain balance target
///     (reinvest amount = MAX(0, target − poolBalance)), less a
///     <see cref="Holdback" />. This object is input only; the reinvestment loop
///     that consumes it is tracked separately (graam-flows#49).
/// </summary>
public record ReinvestmentConfig
{
    /// <summary>
    ///     End of the reinvestment window. After this date, principal proceeds
    ///     flow through as normal paydown. Required.
    /// </summary>
    public DateTime ReinvestEndDate { get; init; }

    /// <summary>
    ///     Optional start of the reinvestment window. When null, reinvestment is
    ///     active from the projection start.
    /// </summary>
    public DateTime? ReinvestStartDate { get; init; }

    /// <summary>
    ///     Plain balance target the pool is reinvested up to
    ///     (reinvest amount = MAX(0, target − poolBalance)). Used when
    ///     <see cref="TargetSchedule" /> is null.
    /// </summary>
    public double Target { get; init; }

    /// <summary>
    ///     Optional per-period balance target (index 0 = first projection
    ///     period). Values clamp to the last entry past the end of the list. When
    ///     non-empty it overrides the scalar <see cref="Target" />.
    /// </summary>
    public IReadOnlyList<double>? TargetSchedule { get; init; }

    /// <summary>
    ///     Fraction of eligible proceeds released instead of reinvested (0 =
    ///     reinvest everything, 1 = release everything). Default 0.
    /// </summary>
    public double Holdback { get; init; }

    /// <summary>
    ///     Which collateral cashflows are eligible to be reinvested. Defaults to
    ///     scheduled principal + prepayments.
    /// </summary>
    public EligibleProceeds EligibleProceeds { get; init; }
        = EligibleProceeds.ScheduledPrincipal | EligibleProceeds.Prepayments;

    /// <summary>
    ///     Reinvestment asset templates. Eligible proceeds are split across them
    ///     by <see cref="ReinvestTemplate.AllocationPct" />.
    /// </summary>
    public IReadOnlyList<ReinvestTemplate> Templates { get; init; } = Array.Empty<ReinvestTemplate>();

    /// <summary>Balance target for a given zero-based projection period.</summary>
    public double TargetAt(int period)
    {
        if (TargetSchedule is { Count: > 0 })
        {
            var i = period < 0 ? 0
                : period >= TargetSchedule.Count ? TargetSchedule.Count - 1
                : period;
            return TargetSchedule[i];
        }

        return Target;
    }

    /// <summary>True when the given date is inside the reinvestment window.</summary>
    public bool IsInWindow(DateTime date)
    {
        if (ReinvestStartDate.HasValue && date < ReinvestStartDate.Value)
            return false;
        return date <= ReinvestEndDate;
    }

    /// <summary>
    ///     Validate the config, throwing <see cref="InvalidOperationException" />
    ///     on the first problem found. Mirrors the throw-based input validation
    ///     used elsewhere in the build path.
    /// </summary>
    public void Validate(string dealName)
    {
        var ctx = string.IsNullOrEmpty(dealName) ? "Reinvestment" : $"Deal {dealName}: reinvestment";

        if (ReinvestEndDate == default)
            throw new InvalidOperationException($"{ctx} requires a reinvestEndDate");
        if (ReinvestStartDate.HasValue && ReinvestStartDate.Value > ReinvestEndDate)
            throw new InvalidOperationException($"{ctx} reinvestStartDate must be on or before reinvestEndDate");
        if (Holdback < 0 || Holdback > 1)
            throw new InvalidOperationException($"{ctx} holdback must be in [0, 1] (got {Holdback})");
        if (Templates.Count == 0)
            throw new InvalidOperationException($"{ctx} requires at least one template");

        var allocSum = 0.0;
        foreach (var t in Templates)
        {
            if (t.AllocationPct < 0)
                throw new InvalidOperationException($"{ctx} template allocationPct must be non-negative");
            allocSum += t.AllocationPct;
        }

        if (Math.Abs(allocSum - 100.0) > 0.01)
            throw new InvalidOperationException(
                $"{ctx} template allocationPct must sum to 100 (got {allocSum:0.##})");
    }
}
