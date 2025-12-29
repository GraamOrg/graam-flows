namespace GraamFlows.Util.Calender.DayCounters;

public class ActualActualISMAFixedRefDates : ActualActualISMA
{
    protected DateTime[] _refDates;
    protected double[] _yearFactions;

    public ActualActualISMAFixedRefDates(IEnumerable<DateTime> refDates, double yearFraction)
        : this(refDates, refDates.Skip(1).Select(i => yearFraction))
    {
    }

    public ActualActualISMAFixedRefDates(IEnumerable<DateTime> refDates, IEnumerable<double> yearFractions)
    {
        _refDates = refDates.Take(1000).ToArray(); // limit to 1000 in cse the enum is infinite
        _yearFactions = yearFractions.Take(999).ToArray();
        if (_refDates.Length < 2)
            throw new ArgumentException(@"must contain at least 2 dates", @"refDates");
        if (_refDates.Length != _yearFactions.Length + 1)
            throw new ArgumentException("you must provide n year factions and n+1 dates");
        if (yearFractions.Any(yf => yf <= 0))
            throw new ArgumentOutOfRangeException("yearFractions", @"Not all year faractions are >0");
    }

    #region Overrides of DayCounter

    public override string Name => "Actual/Actual (ISMA) Fixed Ref Dates";

    public override double YearFraction(DateTime start, DateTime end)
    {
        if (start == end) return 0;
        if (start > end) return -YearFraction(end, start);

        if (start < _refDates[0])
            throw new ArgumentOutOfRangeException("start", start,
                @"should be after the first reference date (" + _refDates[0] + ")");

        if (end > _refDates[_refDates.Length - 1])
            throw new ArgumentOutOfRangeException("end", end,
                @"should be before the last reference date (" + _refDates[_refDates.Length - 1] + ")");

        var iRefStart = Enumerable.Range(0, _refDates.Length).Last(i => start >= _refDates[i]);
        var iRefEnd = Enumerable.Range(iRefStart + 1, _refDates.Length - iRefStart - 1).First(i => end <= _refDates[i]);

        if (iRefEnd == iRefStart + 1)
            return YearFraction(start, end, _refDates[iRefStart], _refDates[iRefStart + 1]);

        return YearFraction(start, _refDates[iRefStart + 1], _refDates[iRefStart], _refDates[iRefStart + 1])
               + Enumerable.Range(iRefStart + 1, iRefEnd - iRefStart - 2).Sum(i => _yearFactions[i])
               + YearFraction(_refDates[iRefEnd - 1], end, _refDates[iRefEnd - 1], _refDates[iRefEnd]);
    }

    protected override DateTime GuessDate(DateTime start, double yearFraction)
    {
        return start.AddDays((int)(yearFraction * 365));
    }

    #endregion
}