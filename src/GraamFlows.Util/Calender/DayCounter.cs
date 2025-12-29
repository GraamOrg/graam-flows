using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Util.Calender;

public abstract class DayCounter : IDayCounter
{
    public abstract string Name { get; }

    public virtual int DayCount(DateTime start, DateTime end)
    {
        // default implementation, to be overridden in special day coutn conventions
        return (end - start).Days;
    }

    public virtual double YearFraction(DateTime start, DateTime end)
    {
        return YearFraction(start, end, start, end);
    }

    public abstract double YearFraction(DateTime start, DateTime end, DateTime refStart, DateTime refEnd);

    public DateTime GetFirstDateFromYearFraction(DateTime start, double yearFraction)
    {
        var date = GuessDate(start, yearFraction);
        var yf = YearFraction(start, date);

        // make sure we go past the dates we look for on the way down
        while (yf >= yearFraction)
        {
            date = date.AddDays(-1);
            yf = YearFraction(start, date);
        }

        // backtrack (up) until the first date that matches the yearFraction
        while (yf < yearFraction)
        {
            date = date.AddDays(1);
            yf = YearFraction(start, date);
        }

        return date;
    }

    public DateTime GetLastDateFromYearFraction(DateTime start, double yearFraction)
    {
        var date = GuessDate(start, yearFraction);
        var yf = YearFraction(start, date);

        // make sure we go past the dates we look for
        while (yf <= yearFraction)
        {
            date = date.AddDays(1);
            yf = YearFraction(start, date);
        }

        // backtrack until the first date that matches the yearFraction
        while (yf > yearFraction)
        {
            date = date.AddDays(-1);
            yf = YearFraction(start, date);
        }

        return date;
    }

    protected abstract DateTime GuessDate(DateTime start, double yearFraction);
}