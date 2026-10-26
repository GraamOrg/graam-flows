namespace GraamFlows.Util.Calender.DayCounters;

/// <summary>
///     30/360 as US securities count it (the SIA rule, "30/360 US"): the last day of February is
///     day 30. <see cref="Thirty360Us" /> is Bond Basis and has no February rule, so a note whose
///     first period runs from a closing on February 28 accrued 17 days to March 15 where its
///     trustee paid 15 (Exeter Automobile Receivables Trust 2023-1's Class A-2: 135,000,000 x
///     5.61% x 15/360 = 315,562.50; Bond Basis gave 357,637.50).
///
///     Opt-in ("30/360 SIA"): every period that starts on the 1st or on a pay day before the 28th
///     counts the same as Bond Basis; only a period starting or ending on the last day of
///     February moves. A subclass of <see cref="Thirty360Us" />, so the waterfall treats it as
///     30/360 everywhere it asks (pay-day normalisation, unrolled cash-flow dates).
///
///     The rules, in order: if D1 and D2 are both the last day of February, D2 = 30; if D1 is the
///     last day of February, D1 = 30; if D2 is 31 and D1 is 30 or 31, D2 = 30; if D1 is 31,
///     D1 = 30. Days = 360 (Y2 - Y1) + 30 (M2 - M1) + (D2 - D1).
/// </summary>
public class Thirty360Sia : Thirty360Us
{
    public override string Name => "30/360 (SIA, US securities)";

    public override int DayCount(DateTime start, DateTime end)
    {
        var dd1 = start.Day;
        var dd2 = end.Day;

        if (IsLastDayOfFebruary(start) && IsLastDayOfFebruary(end))
            dd2 = 30;
        if (IsLastDayOfFebruary(start))
            dd1 = 30;
        if (dd2 == 31 && dd1 >= 30)
            dd2 = 30;
        if (dd1 == 31)
            dd1 = 30;

        return 360 * (end.Year - start.Year) + 30 * (end.Month - start.Month) + (dd2 - dd1);
    }

    private static bool IsLastDayOfFebruary(DateTime d)
    {
        return d.Month == 2 && d.Day == DateTime.DaysInMonth(d.Year, 2);
    }
}
