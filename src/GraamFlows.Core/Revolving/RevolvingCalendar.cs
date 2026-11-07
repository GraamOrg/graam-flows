using GraamFlows.Util.Calender;
using GraamFlows.Util.Calender.Calendars;

namespace GraamFlows.Revolving;

/// <summary>A monthly period and the distribution date that pays it.</summary>
public sealed record RevolvingPeriodDates(
    int Period,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    int Days,
    DateTime DistributionDate,
    int AccrualDays);

/// <summary>
///     Calendar monthly periods, each paid in the following month on the distribution day, rolled to
///     the next business day. Interest accrues from one rolled date to the next.
/// </summary>
public static class RevolvingCalendar
{
    private static readonly Calendar Settlement = new UnitedStates();

    public static IReadOnlyList<RevolvingPeriodDates> Build(DateTime statementDate, int distributionDay, int periods)
    {
        var rows = new List<RevolvingPeriodDates>(periods);
        var priorDistribution = Distribution(statementDate, distributionDay);
        for (var t = 0; t < periods; t++)
        {
            var month = statementDate.AddMonths(t + 1);
            var end = new DateTime(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
            var start = new DateTime(month.Year, month.Month, 1);
            var distribution = Distribution(end, distributionDay);
            rows.Add(new RevolvingPeriodDates(t, start, end, end.Day, distribution,
                (distribution - priorDistribution).Days));
            priorDistribution = distribution;
        }

        return rows;
    }

    private static DateTime Distribution(DateTime periodEnd, int distributionDay)
    {
        var next = periodEnd.AddMonths(1);
        return Settlement.Adjust(new DateTime(next.Year, next.Month, distributionDay));
    }
}
