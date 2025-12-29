using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Util.TermStructure;

public class MarketRates : IMarketRates
{
    public MarketRates(MarketData marketData, ITermStructure parCurve, ITermStructure impliedSpotCurve)
    {
        MarketData = marketData;
        ParCurve = parCurve;
        ImpliedSpotCurve = impliedSpotCurve;
        TermStructure = new Dictionary<string, ITermStructure>();
    }

    public MarketRates()
    {
        TermStructure = new Dictionary<string, ITermStructure>();
    }

    public MarketData MarketData { get; set; }
    public ITermStructure ParCurve { get; set; }
    public ITermStructure ImpliedSpotCurve { get; set; }
    public Dictionary<string, ITermStructure> TermStructure { get; }

    public ITermStructure GetTermStructure(CurveType curve)
    {
        switch (curve)
        {
            case CurveType.InterpolatedYieldCurve:
                return ParCurve;
            case CurveType.ImpliedSpotCurve:
                return ImpliedSpotCurve;
            default:
                throw new ArgumentException($"{curve} is not recognized as a valid curve!");
        }
    }
}