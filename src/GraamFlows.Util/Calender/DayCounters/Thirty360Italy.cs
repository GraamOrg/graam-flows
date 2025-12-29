namespace GraamFlows.Util.Calender.DayCounters;

public class Thirty360Italy : DayCounter
{
    #region Overrides of DayCounter

    public override string Name => "30E/360 (Italian)";

    public override int DayCount(DateTime start, DateTime end)
    {
        var dd1 = start.Day;
        var dd2 = end.Day;
        var mm1 = start.Month;
        var mm2 = end.Month;
        var yy1 = start.Year;
        var yy2 = end.Year;

        if (mm1 == 2 && dd1 > 27) dd1 = 30;
        if (mm2 == 2 && dd2 > 27) dd2 = 30;

        return 360 * (yy2 - yy1) + 30 * (mm2 - mm1 - 1) + Math.Max(0, 30 - dd1) + Math.Min(30, dd2);
    }

    public override double YearFraction(DateTime start, DateTime end, DateTime refStart, DateTime refEnd)
    {
        return DayCount(start, end) / 360.0;
    }

    protected override DateTime GuessDate(DateTime start, double yearFraction)
    {
        return start.AddDays((int)(yearFraction * 360));
    }

    #endregion
}