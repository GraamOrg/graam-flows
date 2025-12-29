using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Util.TermStructure;

public static class MarketRateBuilder
{
    public static IMarketRates Create(DateTime settleDate, MarketData marketData, IYieldCurveAssumptions ycAssumps)
    {
        var terms = new List<double>();
        var rates = new List<double>();

        foreach (var inst in ycAssumps.YieldCurveInstruments)
        {
            MarketDataQuote quote;
            if (!marketData.Quotes.TryGetValue(inst, out quote))
                throw new Exception(
                    $"No quote for instrument {inst} but it is specified as a key rate. MR date is {marketData.MarketRateDate:d}");

            terms.Add(quote.Term);
            rates.Add(quote.Value * .01);
        }

        ycAssumps.Interpolator.Interpolate(terms.ToArray(), rates.ToArray(), 5000, out var interplatedTerms,
            out var interplatedParRates);
        var parCurve = new List<IInterestRate>(interplatedParRates.Length);
        for (var i = 0; i != interplatedParRates.Length; ++i)
        {
            var r = interplatedParRates[i];
            var t = interplatedTerms[i];
            parCurve.Add(new InterestRate(t, r, ycAssumps.DayCounter, ycAssumps.CompoundingType, ycAssumps.Frequency));
        }

        var parTermStructure = new TermStructure(settleDate, parCurve);

        return new MarketRates(marketData, parTermStructure, null);
    }
}