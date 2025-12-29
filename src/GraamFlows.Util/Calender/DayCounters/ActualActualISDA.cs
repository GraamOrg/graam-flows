namespace GraamFlows.Util.Calender.DayCounters;

public class ActualActualISDA : DayCounter
{
    #region Overrides of DayCounter

    public override string Name => "Actual/Actual (ISDA)";

    public override double YearFraction(DateTime start, DateTime end, DateTime refStart, DateTime refEnd)
    {
        if (start == end) return 0;
        if (start > end) return -YearFraction(end, start, end, start);

        int y1 = start.Year, y2 = end.Year;
        double dib1 = DateTime.IsLeapYear(y1) ? 366 : 365,
            dib2 = DateTime.IsLeapYear(y2) ? 366 : 365;

        double sum = y2 - y1 - 1;
        sum += DayCount(start, new DateTime(y1 + 1, 1, 1)) / dib1;
        sum += DayCount(new DateTime(y2, 1, 1), end) / dib2;
        return sum;
    }

    protected override DateTime GuessDate(DateTime start, double yearFraction)
    {
        return start.AddDays((int)(yearFraction * 365));
    }

    #endregion
}