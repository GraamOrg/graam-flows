using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Util.Calender;

namespace GraamFlows.Util.TermStructure;

public class InterestRate : IInterestRate
{
    public InterestRate(double t, double r, IDayCounter dc, CompoundingTypeEnum comp, FrequencyTypeEnum freq)
    {
        Term = t;
        Rate = r;
        DayCounter = dc;
        Compounding = comp;
        Frequency = freq;
    }

    public InterestRate(double r, IDayCounter dc, CompoundingTypeEnum comp, FrequencyTypeEnum freq)
    {
        Term = double.NaN;
        Rate = r;
        DayCounter = dc;
        Compounding = comp;
        Frequency = freq;
    }

    public double Term { get; }
    public double Rate { get; }
    public CompoundingTypeEnum Compounding { get; }
    public FrequencyTypeEnum Frequency { get; }
    public IDayCounter DayCounter { get; }

    public double DiscountFactor(double t, double s)
    {
        return 1.0 / CompoundFactor(t, s + Rate, Frequency, Compounding);
    }

    public double DiscountFactor(double t)
    {
        return DiscountFactor(t, 0);
    }

    public double DiscountFactor(DateTime d1, DateTime d2)
    {
        var t = DayCounter.YearFraction(d1, d2);
        return DiscountFactor(t, 0);
    }

    public double DiscountFactor(DateTime d1, DateTime d2, double s)
    {
        var t = DayCounter.YearFraction(d1, d2);
        return DiscountFactor(t, s);
    }

    public double CompoundFactor(double t)
    {
        return CompoundFactor(t, Rate, Frequency, Compounding);
    }

    public IInterestRate EquivalentRate(CompoundingTypeEnum comp, FrequencyTypeEnum freq, double t)
    {
        return ImpliedRate(CompoundFactor(t), DayCounter, comp, freq, t);
    }

    public IInterestRate EquivalentRate(IDayCounter dayCounter, CompoundingTypeEnum comp, FrequencyTypeEnum freq,
        DateTime d1, DateTime d2)
    {
        var t1 = DayCounter.YearFraction(d1, d2);
        var t2 = dayCounter.YearFraction(d1, d2);
        return ImpliedRate(CompoundFactor(t1), dayCounter, comp, freq, t2);
    }

    public static double CompoundFactor(double t, double r, FrequencyTypeEnum frequency,
        CompoundingTypeEnum compounding)
    {
        if (frequency < 0)
            throw new ArgumentException($"Frequency {frequency} is invalid!");
        var freq = (int)frequency;
        switch (compounding)
        {
            case CompoundingTypeEnum.Simple:
                return 1.0 + r * t;
            case CompoundingTypeEnum.Compounded:
                return Math.Pow(1.0 + r / freq, freq * t);
            case CompoundingTypeEnum.Continuous:
                return Math.Exp(r * t);
            case CompoundingTypeEnum.SimpleThenCompounded:
                if (t <= 1.0 / freq)
                    return 1.0 + r * t;
                return Math.Pow(1.0 + r / freq, freq * t);
            default:
                throw new ArgumentException($"Compounding {compounding} is not known!");
        }
    }

    public static double DiscountFactor(double t, double r, double s, FrequencyTypeEnum frequency,
        CompoundingTypeEnum compounding)
    {
        return 1.0 / CompoundFactor(t, s + r, frequency, compounding);
    }

    public static IInterestRate ImpliedRate(double compound, IDayCounter dayCounter, CompoundingTypeEnum comp,
        FrequencyTypeEnum freq, double t)
    {
        var dfreq = (double)freq;
        double r;
        if (Math.Abs(compound - 1.0) < double.Epsilon)
            r = 0.0;
        else
            switch (comp)
            {
                case CompoundingTypeEnum.Simple:
                    r = (compound - 1.0) / t;
                    break;
                case CompoundingTypeEnum.Compounded:
                    r = (Math.Pow(compound, 1.0 / (dfreq * t)) - 1.0) * dfreq;
                    break;
                case CompoundingTypeEnum.Continuous:
                    r = Math.Log(compound) / t;
                    break;
                case CompoundingTypeEnum.SimpleThenCompounded:
                    if (t <= 1.0 / dfreq)
                        r = (compound - 1.0) / t;
                    else
                        r = (Math.Pow(compound, 1.0 / (dfreq * t)) - 1.0) * dfreq;
                    break;
                default:
                    throw new ArgumentException($"Compounding {comp} is invalid!");
            }

        return new InterestRate(t, r, dayCounter, comp, freq);
    }

    public static IInterestRate ImpliedRate(double compound, DayCounter dayCounter, CompoundingTypeEnum comp,
        FrequencyTypeEnum freq, DateTime d1, DateTime d2)
    {
        var t = dayCounter.YearFraction(d1, d2);
        return ImpliedRate(compound, dayCounter, comp, freq, t);
    }
}