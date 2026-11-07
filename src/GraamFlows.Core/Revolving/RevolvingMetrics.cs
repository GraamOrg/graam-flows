namespace GraamFlows.Revolving;

/// <summary>
///     The monthly series rates the trigger tests read. Yield annualizes on the monthly period's
///     days and base rate on the interest period's days, which differ when a distribution date rolls
///     past a weekend. Both reproduce printed American Express figures.
/// </summary>
public static class RevolvingMetrics
{
    /// <param name="netFinance">finance allocated to the series, less its investor default amount.</param>
    /// <param name="invested">invested amount at the start of the month.</param>
    /// <param name="periodDays">days in the monthly period.</param>
    public static double PortfolioYieldPct(double netFinance, double invested, int periodDays)
    {
        return Annualized(netFinance, invested, periodDays);
    }

    /// <param name="interestAndFees">the month's accrued class interest and fees.</param>
    /// <param name="invested">invested amount at the start of the month.</param>
    /// <param name="accrualDays">days between the two distribution dates.</param>
    public static double BaseRatePct(double interestAndFees, double invested, int accrualDays)
    {
        return Annualized(interestAndFees, invested, accrualDays);
    }

    /// <summary>
    ///     A note series' printed finance yield: annualized on the days of the period's own year, so a
    ///     leap year prints 366. Chase's yield prints this way and its net losses do not.
    /// </summary>
    public static double FinanceYieldPct(double finance, double numerator, RevolvingPeriodDates dates)
    {
        var year = DateTime.IsLeapYear(dates.PeriodEnd.Year) ? 366.0 : 365.0;
        return numerator > 0 ? finance / numerator * year / dates.PeriodEnd.Day * 100.0 : 0.0;
    }

    /// <summary>A month's dollars over the numerator, times 12.</summary>
    public static double MonthlyAnnualizedPct(double amount, double numerator)
    {
        return numerator > 0 ? amount / numerator * 1200.0 : 0.0;
    }

    /// <summary>The weighted average of class rates by outstanding amount.</summary>
    internal static double WeightedCouponPct(IEnumerable<ClassRun> classes)
    {
        var rows = classes.ToList();
        var outstanding = rows.Sum(c => c.BeginOutstanding);
        return outstanding > 0 ? rows.Sum(c => c.BeginOutstanding * c.RatePct) / outstanding : 0.0;
    }

    /// <summary>The fee rate a note series states: the same on every tranche.</summary>
    internal static double FeePct(IEnumerable<SeriesRun> tranches)
    {
        return tranches.SelectMany(t => t.Config.Fees).Select(f => f.RatePct).DefaultIfEmpty(0.0).Max();
    }

    /// <summary>
    ///     A paid-off series can leave floating-point dust invested, and a series awaiting its first
    ///     period has no interest days yet. Neither has a rate.
    /// </summary>
    private static double Annualized(double amount, double invested, int days)
    {
        return invested >= SeriesRun.Cent && days > 0 ? amount / invested * 365.0 / days * 100.0 : 0.0;
    }
}
