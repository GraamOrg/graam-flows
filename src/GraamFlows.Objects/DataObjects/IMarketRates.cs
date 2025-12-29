namespace GraamFlows.Objects.DataObjects;

public interface IMarketRates
{
    MarketData MarketData { get; set; }
    ITermStructure ParCurve { get; set; }
    ITermStructure ImpliedSpotCurve { get; set; }
    Dictionary<string, ITermStructure> TermStructure { get; }
    ITermStructure GetTermStructure(CurveType curve);
}

public enum CurveType
{
    InterpolatedYieldCurve, // I Spread
    ImpliedSpotCurve, // Z Spread or OAS
    DiscountMargin
}