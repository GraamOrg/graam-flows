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

    /// <summary>A paid-off series can leave floating-point dust invested, which has no rate.</summary>
    private static double Annualized(double amount, double invested, int days)
    {
        return invested >= SeriesRun.Cent ? amount / invested * 365.0 / days * 100.0 : 0.0;
    }
}
