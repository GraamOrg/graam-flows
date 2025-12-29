using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Util.Calender.DayCounters;
using GraamFlows.Util.MathUtil.Interpolations;

namespace GraamFlows.Assumptions;

public class DefaultYieldCurveAssumptions : IYieldCurveAssumptions
{
    public double DurationShockSize = 50;

    public DefaultYieldCurveAssumptions()
    {
        // set up default mr instruments
        YieldCurveInstruments = DefaultMakretDataInst();
        Interpolator = DefaultInterpolator();
        DayCounter = new Actual360();
        CompoundingType = CompoundingTypeEnum.Compounded;
        Frequency = FrequencyTypeEnum.Semiannual;
    }

    public List<MarketDataInstEnum> YieldCurveInstruments { get; set; }
    public IInterpolation2D Interpolator { get; set; }
    public IDayCounter DayCounter { get; set; }
    public CompoundingTypeEnum CompoundingType { get; set; }
    public FrequencyTypeEnum Frequency { get; set; }

    public List<MarketDataInstEnum> DefaultMakretDataInst()
    {
        var list = new List<MarketDataInstEnum>();
        list.Add(MarketDataInstEnum.Libor1M);
        list.Add(MarketDataInstEnum.Libor3M);
        list.Add(MarketDataInstEnum.Swap2Y);
        list.Add(MarketDataInstEnum.Swap3Y);
        list.Add(MarketDataInstEnum.Swap4Y);
        list.Add(MarketDataInstEnum.Swap5Y);
        list.Add(MarketDataInstEnum.Swap6Y);
        list.Add(MarketDataInstEnum.Swap7Y);
        list.Add(MarketDataInstEnum.Swap8Y);
        list.Add(MarketDataInstEnum.Swap9Y);
        list.Add(MarketDataInstEnum.Swap10Y);
        list.Add(MarketDataInstEnum.Swap12Y);
        list.Add(MarketDataInstEnum.Swap15Y);
        list.Add(MarketDataInstEnum.Swap20Y);
        list.Add(MarketDataInstEnum.Swap25Y);
        list.Add(MarketDataInstEnum.Swap30Y);
        return list;
    }

    public IInterpolation2D DefaultInterpolator()
    {
        return new CubicSpline();
    }
}