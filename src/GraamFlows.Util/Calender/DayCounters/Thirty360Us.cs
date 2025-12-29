namespace GraamFlows.Util.Calender.DayCounters;

public class Thirty360Us : DayCounter
{
    #region Overrides of DayCounter

    public override string Name => "30/360  (Bond Basis)";

    public override int DayCount(DateTime start, DateTime end)
    {
        var dd1 = start.Day;
        var dd2 = end.Day;
        var mm1 = start.Month;
        var mm2 = end.Month;
        var yy1 = start.Year;
        var yy2 = end.Year;

        if (dd2 == 31 && dd1 < 30)
        {
            dd2 = 1;
            mm2++;
        }

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