namespace GraamFlows.Revolving;

/// <summary>
///     Required subordinated amounts and their usage, for a de-linked note series.
///
///     A senior tranche's required amount of a junior class is a percentage of its adjusted amount.
///     A middle class's required amount of the class below is the senior classes' claim on that
///     class, plus its percentage of whatever part of the middle class the seniors do not claim.
///     Usage is what a tranche has spent of its required amount: charge-offs moved down to juniors,
///     and principal moved to pay it. Unused is the difference, and it caps every further move.
///     Reimbursing a junior class gives usage back.
/// </summary>
internal static class Subordination
{
    private const double Dust = 1e-9;

    /// <summary>Sets every tranche's required amounts from the adjusted amounts given.</summary>
    /// <param name="adjusted">a tranche's adjusted amount. Zero takes it out of the sums.</param>
    public static void Measure(NoteMonth month, Func<SeriesRun, double> adjusted)
    {
        var order = month.Notes.ClassOrder;
        foreach (var t in month.OfClass(order[0]))
            foreach (var (junior, pct) in t.Config.RequiredSubordination)
                t.Required[junior] = pct / 100.0 * adjusted(t);

        // Each class below the first holds up the class beneath it for the seniors' claim first.
        for (var rank = 1; rank + 1 < order.Count; rank++)
        {
            var key = order[rank];
            var below = order[rank + 1];
            var tranches = month.OfClass(key).ToList();
            var total = tranches.Sum(adjusted);
            var seniors = order.Take(rank).SelectMany(month.OfClass).ToList();
            var seniorClaimOnThis = seniors.Sum(s => s.Required.GetValueOrDefault(key));
            var seniorClaimBelow = seniors.Sum(s => s.Required.GetValueOrDefault(below));
            foreach (var t in tranches)
            {
                var share = total > 0 ? adjusted(t) / total : 0.0;
                var pct = t.Config.RequiredSubordination.GetValueOrDefault(below);
                var free = Math.Max(0.0, adjusted(t) - share * seniorClaimOnThis);
                t.Required[below] = adjusted(t) > 0 ? share * seniorClaimBelow + pct / 100.0 * free : 0.0;
            }
        }
    }

    public static double Unused(SeriesRun t, string junior)
    {
        return Math.Max(0.0, t.Required.GetValueOrDefault(junior) - t.Usage.GetValueOrDefault(junior));
    }

    public static double Unused(SeriesRun t, IEnumerable<string> juniors)
    {
        return juniors.Sum(j => Unused(t, j));
    }

    /// <summary>What a junior class may still pay out and leave its seniors' unused amounts covered.</summary>
    public static double DepositRoom(NoteMonth month, string key)
    {
        var senior = month.Notes.ClassOrder.TakeWhile(k => k != key).ToList();

        // The seniors' required amounts as they stand after this month's senior deposits.
        Measure(month, t => t.Retired ? 0.0 : t.AdjustedInvested);
        var claim = senior
            .Select(k => month.OfClass(k).Sum(t => Unused(t, key)))
            .DefaultIfEmpty(0.0)
            .Max();
        return Math.Max(0.0, month.NominalLiquidation(key) - claim);
    }

    /// <summary>
    ///     An amount a tranche could not meet, carried by its juniors in the order given, each up to
    ///     what the tranche has unused of it. What a class takes it may pass further down.
    /// </summary>
    /// <returns>what no junior could take.</returns>
    public static double Bear(NoteMonth month, SeriesRun tranche, double amount, IReadOnlyList<string> juniors)
    {
        foreach (var junior in juniors)
        {
            var take = Math.Min(amount, Math.Min(Unused(tranche, junior), month.NominalLiquidation(junior)));
            if (take <= Dust)
                continue;

            tranche.Usage[junior] = tranche.Usage.GetValueOrDefault(junior) + take;
            tranche.UsageThisMonth[junior] = tranche.UsageThisMonth.GetValueOrDefault(junior) + take;
            ReduceClass(month, junior, take);
            amount -= take;
        }

        return amount;
    }

    /// <summary>
    ///     Reduces a class pro rata by nominal liquidation amount. A class with one below it first
    ///     passes as much as its own unused amount allows, tranche by tranche.
    /// </summary>
    private static void ReduceClass(NoteMonth month, string key, double amount)
    {
        var tranches = month.OfClass(key).ToList();
        var total = tranches.Sum(t => t.AdjustedInvested);
        if (total <= 0)
            return;

        var below = Below(month, key);
        foreach (var t in tranches)
        {
            var share = amount * t.AdjustedInvested / total;
            var left = below == null ? share : Bear(month, t, share, new[] { below });
            t.Classes[0].WriteDown(left);
        }
    }

    private static string? Below(NoteMonth month, string key)
    {
        var order = month.Notes.ClassOrder;
        var rank = order.ToList().IndexOf(key);
        return rank + 1 < order.Count ? order[rank + 1] : null;
    }

    /// <summary>
    ///     The default finance left uncovered, charged to every tranche pro rata to its numerator.
    ///     Each class's own charge first spends its seniors' unused amounts in proportion, then each
    ///     senior tranche's charge moves down: to the lowest class it may, then the next. Whatever
    ///     cannot move reduces the tranche it fell on.
    /// </summary>
    public static void AllocateCharges(NoteMonth month, double uncovered)
    {
        month.UncoveredDefault = uncovered;
        if (uncovered <= 0)
            return;

        var numerators = month.Tranches.Sum(t => t.Numerator);
        if (numerators <= 0)
            return;

        var order = month.Notes.ClassOrder;
        foreach (var key in order.Reverse())
        {
            var rank = order.ToList().IndexOf(key);
            var charge = month.OfClass(key).Select(t => (Tranche: t, Amount: uncovered * t.Numerator / numerators))
                .ToList();
            if (rank > 0)
                ShareAmongSeniors(month, key, rank, charge.Sum(c => c.Amount));

            foreach (var (tranche, amount) in charge)
            {
                var juniors = order.Skip(rank + 1).Reverse().ToList();
                var left = Bear(month, tranche, amount, juniors);
                tranche.Classes[0].WriteDown(left);
            }
        }
    }

    /// <summary>A class's own charge takes from what it had available to each senior tranche.</summary>
    private static void ShareAmongSeniors(NoteMonth month, string key, int rank, double charge)
    {
        var total = month.NominalLiquidation(key);
        if (total <= 0)
            return;

        foreach (var senior in month.Notes.ClassOrder.Take(rank).SelectMany(month.OfClass))
        {
            var take = Math.Min(Unused(senior, key), Unused(senior, key) / total * charge);
            if (take <= Dust)
                continue;

            senior.Usage[key] = senior.Usage.GetValueOrDefault(key) + take;
            senior.UsageThisMonth[key] = senior.UsageThisMonth.GetValueOrDefault(key) + take;
        }
    }

    /// <summary>Reimbursing a class gives its seniors' usage of it back, in proportion to the deficit.</summary>
    public static void ReleaseUsage(NoteMonth month, string key, double paid, double deficit)
    {
        if (deficit <= 0)
            return;

        foreach (var t in month.Tranches.Where(t => t.Usage.ContainsKey(key)))
        {
            var back = Math.Min(t.Usage[key], t.Usage[key] / deficit * paid);
            t.Usage[key] -= back;
            t.UsageThisMonth[key] = t.UsageThisMonth.GetValueOrDefault(key) - back;
        }
    }
}
