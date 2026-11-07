namespace GraamFlows.Revolving;

/// <summary>
///     Pools the investor finance of a group's series and hands it back in priority order: monthly
///     interest, then default amounts, then fees, then write-downs not yet reimbursed. If the pool
///     runs out inside one of those, the series share what is left of it pro rata to their claims
///     under that clause, and nothing below it is paid. Any balance goes pro rata to current invested
///     amounts.
///
///     This reproduces every series' printed Reallocated Investor Finance Charge Collections on the
///     American Express trust for both months read. Funding account earnings and reserve releases
///     stay with their own series.
/// </summary>
internal static class GroupFinanceReallocation
{
    public static void Apply(IReadOnlyList<SeriesRun> running)
    {
        foreach (var group in running.GroupBy(s => s.Config.Group))
            Reallocate(group.ToList());
    }

    private static void Reallocate(List<SeriesRun> series)
    {
        var remaining = series.Sum(s => s.InvestorFinance);
        var given = series.ToDictionary(s => s, _ => 0.0);

        var clauses = new Func<SeriesRun, double>[]
        {
            s => s.Classes.Sum(c => c.InterestDue),
            s => s.Classes.Sum(c => c.DefaultDue),
            s => s.FeesDue,
            s => s.Classes.Sum(c => c.WrittenDown)
        };
        foreach (var claim in clauses)
            remaining -= Give(series, given, claim, remaining);

        Give(series, given, s => Math.Max(0.0, s.TotalInvested), remaining);

        foreach (var s in series)
        {
            s.Finance += given[s] - s.InvestorFinance;
            s.ReallocatedFinance = given[s];
        }
    }

    /// <summary>Pays a clause in full if it can, otherwise pro rata to the claims. Returns what it paid.</summary>
    private static double Give(List<SeriesRun> series, Dictionary<SeriesRun, double> given,
        Func<SeriesRun, double> claim, double available)
    {
        var total = series.Sum(claim);
        if (total <= 0)
            return 0.0;

        var paid = Math.Min(available, total);
        foreach (var s in series)
            given[s] += paid * claim(s) / total;
        return paid;
    }
}
