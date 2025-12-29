namespace GraamFlows.Util.Calender.DayCounters;

public class ActualActualISMA : DayCounter
{
    #region Overrides of DayCounter

    public override string Name => "Actual/Actual (ISMA)";

    public override double YearFraction(DateTime start, DateTime end, DateTime refStart, DateTime refEnd)
    {
        // this is f'ed up.
        // the by default the refDates are the start and end -> the default will always return some faction as nb of months/12
        if (start == end) return 0;
        if (start > end) return -YearFraction(end, start, refStart, refEnd);

        if (!(refEnd > refStart && refEnd > start))
            throw new Exception("Invalid reference period: date 1: " + start + ", date 2: " + end +
                                ", reference period start: " + refStart + ", reference period end: " + refEnd);

        // estimate roughly the length in months of a period
        var months = (int)(0.5 + 12 * (refEnd - refStart).TotalDays / 365.0);

        // for short periods...
        if (months == 0)
        {
            // ...take the reference period as 1 year from start
            refStart = start;
            refEnd = start + new Period(1, Period.TimeUnit.Year);
            months = 12;
        }

        var period = months / 12.0;

        if (end <= refEnd)
        {
            // here refEnd is a future (notional?) payment date
            if (start >= refStart)
                return period * DayCount(start, end) / DayCount(refStart, refEnd);

            // here refStart is the next (maybe notional) payment date and refEnd is the second next (maybe notional) payment date.
            // start < refStart < refEnd
            // AND end <= refEnd
            // this case is long first coupon

            // the last notional payment date
            var previousRef = refStart - new Period(months, Period.TimeUnit.Month);
            if (end > refStart)
                return YearFraction(start, refStart, previousRef, refStart) +
                       YearFraction(refStart, end, refStart, refEnd);
            return YearFraction(start, end, previousRef, refStart);
        }

        // here refEnd is the last (notional?) payment date
        // start < refEnd < end AND refStart < refEnd
        if (!(refStart <= start)) throw new ArgumentException("invalid dates: start < refStart < refEnd < end");

        // now it is: refStart <= start < refEnd < end

        // the part from start to refEnd
        var sum = YearFraction(start, refEnd, refStart, refEnd);

        // the part from refEnd to end
        // count how many regular periods are in [refEnd, end], then add the remaining time
        var i = 0;
        DateTime newRefStart, newRefEnd;
        while (true)
        {
            newRefStart = refEnd + new Period(months * i, Period.TimeUnit.Month);
            newRefEnd = refEnd + new Period(months * (i + 1), Period.TimeUnit.Month);
            if (end < newRefEnd)
                break;
            sum += period;
            i++;
        }

        sum += YearFraction(newRefStart, end, newRefStart, newRefEnd);
        return sum;
    }

    protected override DateTime GuessDate(DateTime start, double yearFraction)
    {
        return start.AddDays((int)(yearFraction * 365));
    }

    #endregion
}