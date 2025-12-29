using GraamFlows.Objects.DataObjects;
using GraamFlows.Util.Collections;

namespace GraamFlows.Util.TermStructure;

public class TermStructure : ITermStructure
{
    public TermStructure(DateTime settleDate, IEnumerable<IInterestRate> rates)
    {
        SettleDate = settleDate;
        Curve = new List<IInterestRate>(rates);
    }

    public DateTime SettleDate { get; }
    public List<IInterestRate> Curve { get; }

    public IInterestRate GetRate(double t)
    {
        var rate = Curve.BinarySearch(c => c.Term, t, .09);
        return rate;
    }

    public IInterestRate GetRate(IDayCounter dayCounter, DateTime rateDate)
    {
        var yearFrac = dayCounter.YearFraction(SettleDate, rateDate);
        return GetRate(yearFrac);
    }
}