using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;

namespace GraamFlows.Objects.DataObjects;

public interface IRateProvider
{
    double GetRate(MarketDataInstEnum inst, DateTime value);
    double GetRate(MarketDataInstEnum inst, int absT);
    double[] GetRates(MarketDataInstEnum inst, int startAbsT, int length);
}

public class ConstantRateProvider : IRateProvider
{
    private readonly MarketData _marketData;
    private readonly double _rate;

    public ConstantRateProvider(MarketData marketData)
    {
        _marketData = marketData;
    }

    public ConstantRateProvider(double rate)
    {
        _rate = rate;
    }

    public double GetRate(MarketDataInstEnum inst, DateTime value)
    {
        if (_marketData == null)
            return _rate;
        return _marketData.ValueForIndex(inst);
    }

    public double GetRate(MarketDataInstEnum inst, int absT)
    {
        return GetRate(inst, DateTime.MinValue);
    }

    public double[] GetRates(MarketDataInstEnum inst, int startAbsT, int length)
    {
        var rate = GetRate(inst, DateTime.MinValue);
        var d = new double[length];
        for (var i = 0; i < d.Length; i++) d[i] = rate;

        return d;
    }
}

public class KeyRateInstruments : IRateProvider
{
    public KeyRateInstruments()
    {
        RateVectors = new Dictionary<string, KeyRateVector>();
    }

    public Dictionary<string, KeyRateVector> RateVectors { get; }

    public double GetRate(MarketDataInstEnum inst, DateTime value)
    {
        var rateVector = GetRateVector(inst);
        if (rateVector.Rates.TryGetValue(value, out var rate))
            return rate;

        var firstOfMonth = new DateTime(value.Year, value.Month, 1);
        if (rateVector.Rates.TryGetValue(firstOfMonth, out rate))
            return rate;

        throw new ArgumentException($"Market data instrument {inst} requires value for {value}!");
    }

    public double GetRate(MarketDataInstEnum inst, int absT)
    {
        var date = DateUtil.CalcDate(absT);
        return GetRate(inst, date);
    }

    public double[] GetRates(MarketDataInstEnum inst, int startAbsT, int length)
    {
        var d = new double[length];
        var lastGoodRate = 0.0;
        for (var i = 0; i < d.Length; i++)
        {
            var date = DateUtil.CalcDate(startAbsT + i);
            var r = SafeGetRate(inst, date);
            if (r >= 0) lastGoodRate = r;
            d[i] = lastGoodRate;
        }

        return d;
    }

    private double SafeGetRate(MarketDataInstEnum inst, DateTime value)
    {
        var rateVector = GetRateVector(inst);
        if (rateVector.Rates.TryGetValue(value, out var rate))
            return rate;

        var firstOfMonth = new DateTime(value.Year, value.Month, 1);
        if (rateVector.Rates.TryGetValue(firstOfMonth, out rate))
            return rate;

        return -1;
    }

    private KeyRateVector GetRateVector(MarketDataInstEnum marketDataInst)
    {
        var instKey = marketDataInst.ToString();
        if (RateVectors.TryGetValue(instKey, out var rateVector))
            return rateVector;

        if (RateVectors.TryGetValue(instKey.ToLower(), out rateVector))
            return rateVector;

        if (RateVectors.TryGetValue(instKey.ToUpper(), out rateVector))
            return rateVector;

        throw new ArgumentException($"Market data instrument {marketDataInst} was not supplied!");
    }
}

public class KeyRateVector
{
    public KeyRateVector()
    {
        Rates = new Dictionary<DateTime, double>();
    }

    public KeyRateVector(Dictionary<DateTime, double> rates)
    {
        Rates = rates;
    }

    public Dictionary<DateTime, double> Rates { get; }
}