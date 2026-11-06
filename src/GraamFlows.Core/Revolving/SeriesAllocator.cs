namespace GraamFlows.Revolving;

/// <summary>Where in the month the allocator measures the pool.</summary>
public enum PoolMeasure
{
    PeriodStart,
    Average,
    PeriodEnd
}

public static class PoolMeasureParser
{
    public static PoolMeasure Parse(string? measure)
    {
        return measure?.Trim().ToUpperInvariant() switch
        {
            "PERIOD_START" => PoolMeasure.PeriodStart,
            "AVERAGE" => PoolMeasure.Average,
            "PERIOD_END" => PoolMeasure.PeriodEnd,
            _ => throw new RevolvingConfigException(
                $"Unknown allocator poolMeasure '{measure}'. Supported: PERIOD_START, AVERAGE and "
                + "PERIOD_END.")
        };
    }
}

/// <param name="Series">the series name.</param>
/// <param name="Numerator">its adjusted invested amount or nominal liquidation amount.</param>
/// <param name="Phase">its phase this month.</param>
/// <param name="FrozenPrincipalPct">
///     under <see cref="PrincipalFreeze.FrozenPercent" />, the share frozen at the last revolving day.
///     Null while revolving, and on the first month after, when the allocator computes it.
/// </param>
/// <param name="FrozenNumerator">
///     under <see cref="PrincipalFreeze.FrozenNumerator" />, the numerator from the last revolving day.
///     Null while revolving, and on the first month after, when it is this month's numerator.
/// </param>
public readonly record struct SeriesClaim(
    string Series,
    double Numerator,
    SeriesPhase Phase,
    double? FrozenPrincipalPct = null,
    double? FrozenNumerator = null);

/// <param name="FloatingPct">the series' share of finance collections and defaults.</param>
/// <param name="PrincipalPct">its share of gross principal collections.</param>
/// <param name="FrozenPrincipalPct">the share to hold from here on, under that freeze.</param>
/// <param name="FrozenNumerator">the numerator to hold from here on, under that freeze.</param>
public readonly record struct SeriesAllocation(
    string Series,
    double Numerator,
    double FloatingPct,
    double PrincipalPct,
    double? FrozenPrincipalPct,
    double? FrozenNumerator = null);

/// <summary>
///     Splits a month's collections across the series of one pool. Floating % is
///     <c>numerator / max(pool, sum of numerators)</c>. Principal % equals floating % while a series
///     revolves. After that, the program states what freezes: the percentage from the last revolving
///     day, or that day's numerator over the current pool. Either way it applies to gross principal
///     collections while the transferor funds continuing new receivables. What the series do not take
///     belongs to the transferor.
/// </summary>
public static class SeriesAllocator
{
    public static IReadOnlyList<SeriesAllocation> Allocate(PoolPeriod pool, PoolMeasure measure,
        PrincipalFreeze freeze, IReadOnlyList<SeriesClaim> claims)
    {
        var sumOfNumerators = claims.Sum(c => c.Numerator);
        var denominator = Math.Max(Measure(pool, measure), sumOfNumerators);

        // The last revolving day is the close of the month before, whatever the program measures
        // its monthly floating % on.
        var lastRevolvingDay = Math.Max(pool.Beginning, sumOfNumerators);

        // Frozen numerators stand in for the current ones here, so the shares they make fit the pool.
        var currentPool = Math.Max(Measure(pool, measure),
            claims.Sum(c => c.Phase == SeriesPhase.Revolving ? c.Numerator : c.FrozenNumerator ?? c.Numerator));

        var allocations = new List<SeriesAllocation>(claims.Count);
        foreach (var claim in claims)
        {
            var floating = denominator > 0 ? claim.Numerator / denominator : 0.0;
            if (claim.Phase == SeriesPhase.Revolving)
            {
                allocations.Add(new SeriesAllocation(claim.Series, claim.Numerator, floating, floating, null));
            }
            else if (freeze == PrincipalFreeze.FrozenPercent)
            {
                var frozen = claim.FrozenPrincipalPct
                             ?? (lastRevolvingDay > 0 ? claim.Numerator / lastRevolvingDay : 0.0);
                allocations.Add(new SeriesAllocation(claim.Series, claim.Numerator, floating, frozen, frozen));
            }
            else
            {
                var frozen = claim.FrozenNumerator ?? claim.Numerator;
                allocations.Add(new SeriesAllocation(claim.Series, claim.Numerator, floating,
                    currentPool > 0 ? frozen / currentPool : 0.0, null, frozen));
            }
        }

        return WithinTheWholePool(allocations);
    }

    /// <summary>
    ///     A frozen share does not shrink with the pool, and a frozen numerator does not fall with the
    ///     series' balance, so the shares can add to more than the collections there are. Each then
    ///     takes its proportion of the whole. The frozen value itself is kept.
    /// </summary>
    private static IReadOnlyList<SeriesAllocation> WithinTheWholePool(List<SeriesAllocation> allocations)
    {
        var total = allocations.Sum(a => a.PrincipalPct);
        if (total <= 1.0)
            return allocations;

        return allocations.Select(a => a with { PrincipalPct = a.PrincipalPct / total }).ToList();
    }

    private static double Measure(PoolPeriod pool, PoolMeasure measure)
    {
        return measure switch
        {
            PoolMeasure.PeriodStart => pool.Beginning,
            PoolMeasure.Average => (pool.Beginning + pool.Ending) / 2.0,
            PoolMeasure.PeriodEnd => pool.Ending,
            _ => throw new ArgumentOutOfRangeException(nameof(measure), measure, null)
        };
    }
}
