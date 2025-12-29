namespace GraamFlows.Util.Calender.DayCounters;

public class Actual360 : DayCounter
{
    #region Overrides of DayCounter

    public override string Name => "Actual/360";

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