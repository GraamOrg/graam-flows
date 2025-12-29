namespace GraamFlows.Util.Calender.DayCounters;

public class Actual365 : DayCounter
{
    #region Overrides of DayCounter

    public override string Name => "Actual/365";

    public override double YearFraction(DateTime start, DateTime end, DateTime refStart, DateTime refEnd)
    {
        return DayCount(start, end) / 365.0;
    }

    protected override DateTime GuessDate(DateTime start, double yearFraction)
    {
        return start.AddDays((int)(yearFraction * 365));
    }

    #endregion
}