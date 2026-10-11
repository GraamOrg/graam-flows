namespace GraamFlows.Objects.Util;

/// <summary>
///     The deal's distribution calendar, read off a tranche's <c>PayFrequency</c> (payments a year).
///
///     The amortizer projects collateral MONTHLY. A deal that distributes monthly spends each
///     collateral month on its own date. A deal that distributes less often — a quarterly managed
///     pool — collects over a Collection Period of several months and distributes them together on
///     its Payment Date, so its notes accrue the whole period and receive principal only on the
///     pay dates. Before this the engine distributed every collateral month regardless, so a
///     quarterly deal paid principal monthly, its notes retired early and accrued less interest.
/// </summary>
public static class PayCalendar
{
    /// <summary>
    ///     Months between distributions for a payments-a-year figure. Anything that does not divide
    ///     a year into whole months (including 0, the unset value a database row may carry) is
    ///     MONTHLY — the historical behaviour, so an unset field can never change a deal.
    /// </summary>
    public static int MonthsPerPeriod(int payFrequency) =>
        payFrequency is 1 or 2 or 3 or 4 or 6 ? 12 / payFrequency : 1;

    /// <summary>
    ///     True when <paramref name="date" /> falls in a distribution month: the first pay month or a
    ///     whole number of periods after it. A month before the first pay month is never one.
    /// </summary>
    /// <summary>
    ///     The deal's distribution months, on the same rule the waterfall distributes by: the note
    ///     classes' period (expense rows follow the deal, never state it) anchored at the first
    ///     note's <c>FirstPayDate</c>.
    /// </summary>
    public static Func<DateTime, bool> DistributionDates(GraamFlows.Objects.DataObjects.IDeal deal)
    {
        var notes = deal.Tranches
            .Where(t => t.CashflowTypeEnum != GraamFlows.Objects.TypeEnum.CashflowType.Expense)
            .ToList();
        var months = notes.Select(t => MonthsPerPeriod(t.PayFrequency)).DefaultIfEmpty(1).First();
        var first = notes.FirstOrDefault()?.FirstPayDate ?? DateTime.MinValue;
        return date => IsPayMonth(date, first, months);
    }

    public static bool IsPayMonth(DateTime date, DateTime firstPayDate, int monthsPerPeriod)
    {
        var since = (date.Year - firstPayDate.Year) * 12 + (date.Month - firstPayDate.Month);
        return since >= 0 && since % Math.Max(1, monthsPerPeriod) == 0;
    }
}
