using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Util.Calender;

public class Period
{
    public enum TimeUnit
    {
        Day,
        Week,
        Month,
        Quarter,
        Year
    }

    public Period()
    {
        Length = 0;
        Unit = TimeUnit.Day;
    }

    public Period(int n, TimeUnit unit)
    {
        Length = n;
        Unit = unit;
    }

    public Period(FrequencyTypeEnum f)
    {
        switch (f)
        {
            case FrequencyTypeEnum.NoFrequency:
                // same as Period()
                Unit = TimeUnit.Day;
                Length = 0;
                break;
            case FrequencyTypeEnum.Once:
                Unit = TimeUnit.Year;
                Length = 0;
                break;
            case FrequencyTypeEnum.Annual:
                Unit = TimeUnit.Year;
                Length = 1;
                break;
            case FrequencyTypeEnum.Semiannual:
            case FrequencyTypeEnum.EveryFourthMonth:
            case FrequencyTypeEnum.Quarterly:
            case FrequencyTypeEnum.Bimonthly:
            case FrequencyTypeEnum.Monthly:
                Unit = TimeUnit.Month;
                Length = 12 / (int)f;
                break;
            case FrequencyTypeEnum.EveryFourthWeek:
            case FrequencyTypeEnum.Biweekly:
            case FrequencyTypeEnum.Weekly:
                Unit = TimeUnit.Week;
                Length = 52 / (int)f;
                break;
            case FrequencyTypeEnum.Daily:
                Unit = TimeUnit.Day;
                Length = 1;
                break;
            default:
                throw new NotImplementedException("unknown frequency " + f);
        }
    }

    public int Length { get; private set; }

    public TimeUnit Unit { get; private set; }

    public override bool Equals(object o)
    {
        return this == (Period)o;
    }

    public FrequencyTypeEnum ToFrequency()
    {
        if (Length == 0)
        {
            if (Unit == TimeUnit.Year) return FrequencyTypeEnum.Once;
            return FrequencyTypeEnum.NoFrequency;
        }

        switch (Unit)
        {
            case TimeUnit.Year:
                if (Length == 1)
                    return FrequencyTypeEnum.Annual;
                return FrequencyTypeEnum.OtherFrequency;
            case TimeUnit.Month:
                if (12 % Length == 0 && Length <= 12)
                    return (FrequencyTypeEnum)(12 / Length);
                return FrequencyTypeEnum.OtherFrequency;
            case TimeUnit.Week:
                if (Length == 1)
                    return FrequencyTypeEnum.Weekly;
                if (Length == 2)
                    return FrequencyTypeEnum.Biweekly;
                if (Length == 4)
                    return FrequencyTypeEnum.EveryFourthWeek;
                return FrequencyTypeEnum.OtherFrequency;
            case TimeUnit.Day:
                if (Length == 1)
                    return FrequencyTypeEnum.Daily;
                return FrequencyTypeEnum.OtherFrequency;
            default:
                throw new NotImplementedException("unknown TimeUnit " + Unit);
        }
    }

    public static Period operator *(Period p, int n)
    {
        return new Period(n * p.Length, p.Unit);
    }

    public static Period operator *(int n, Period p)
    {
        return new Period(n * p.Length, p.Unit);
    }

    public void Normalize()
    {
        if (Length != 0)
            switch (Unit)
            {
                case TimeUnit.Day:
                    if (Length % 7 == 0)
                    {
                        Length /= 7;
                        Unit = TimeUnit.Week;
                    }

                    break;
                case TimeUnit.Month:
                    if (Length % 12 == 0)
                    {
                        Length /= 12;
                        Unit = TimeUnit.Year;
                    }

                    break;
                case TimeUnit.Week:
                case TimeUnit.Year:
                    break;
                default:
                    throw new ArgumentException("Unknown TimeUnit: " + Unit);
            }
    }

    public static bool operator ==(Period p1, Period p2)
    {
        return !(p1 < p2 || p2 < p1);
    }

    public static bool operator !=(Period p1, Period p2)
    {
        return !(p1 == p2);
    }

    public static bool operator <=(Period p1, Period p2)
    {
        return !(p1 > p2);
    }

    public static bool operator >=(Period p1, Period p2)
    {
        return !(p1 < p2);
    }

    public static bool operator >(Period p1, Period p2)
    {
        return p2 < p1;
    }

    public static bool operator <(Period p1, Period p2)
    {
        // special cases
        if (p1.Length == 0) return p2.Length > 0;
        if (p2.Length == 0) return p1.Length < 0;

        // exact comparisons
        if (p1.Unit == p2.Unit) return p1.Length < p2.Length;
        if (p1.Unit == TimeUnit.Month && p2.Unit == TimeUnit.Year) return p1.Length < 12 * p2.Length;
        if (p1.Unit == TimeUnit.Year && p2.Unit == TimeUnit.Month) return 12 * p1.Length < p2.Length;
        if (p1.Unit == TimeUnit.Day && p2.Unit == TimeUnit.Week) return p1.Length < 7 * p2.Length;
        if (p1.Unit == TimeUnit.Week && p2.Unit == TimeUnit.Day) return 7 * p1.Length < p2.Length;

        // inexact comparisons (handled by converting to days and using limits)
        pair p1lim = new(p1), p2lim = new(p2);
        if (p1lim.hi < p2lim.lo || p2lim.hi < p1lim.lo)
            return p1lim.hi < p2lim.lo;
        throw new ArgumentException("Undecidable comparison between " + p1 + " and " + p2);
    }

    public override int GetHashCode()
    {
        return 0;
    }

    public static DateTime operator +(DateTime d, Period p)
    {
        if (p.Length == 0)
            return d;
        switch (p.Unit)
        {
            case TimeUnit.Day:
                return d.AddDays(p.Length);
            case TimeUnit.Week:
                return d.AddDays(p.Length * 7);
            case TimeUnit.Month:
                return d.AddMonths(p.Length);
            case TimeUnit.Quarter:
                return d.AddMonths(p.Length * 3);
            case TimeUnit.Year:
                return d.AddYears(p.Length);
            default:
                throw new ArgumentException("Unknown TimeUnit: " + p.Unit);
        }
    }

    public static DateTime operator -(DateTime d, Period p)
    {
        return d + new Period(-p.Length, p.Unit);
    }

    public int InMonths()
    {
        switch (Unit)
        {
            case TimeUnit.Month:
                return Length;
            case TimeUnit.Quarter:
                return Length * 3;
            case TimeUnit.Year:
                return Length * 12;
            default:
                throw new ApplicationException("unit (" + Unit + ") cannot be expressed in months");
        }
    }

    public string ToShortString()
    {
        var result = "";
        var n = Length;
        var m = 0;
        switch (Unit)
        {
            case TimeUnit.Day:
                if (n >= 7)
                {
                    m = n / 7;
                    result += m + "W";
                    n = n % 7;
                }

                if (n != 0 || m == 0)
                    return result + n + "D";
                return result;
            case TimeUnit.Week:
                return result + n + "W";
            case TimeUnit.Month:
                if (n >= 12)
                {
                    m = n / 12;
                    result += n / 12 + "Y";
                    n = n % 12;
                }

                if (n != 0 || m == 0)
                    return result + n + "M";
                return result;
            case TimeUnit.Year:
                return result + n + "Y";
            default:
                throw new ApplicationException("unknown time unit (" + Unit + ")");
        }
    }

    // required by operator <
    private struct pair
    {
        public readonly int lo;
        public readonly int hi;

        public pair(Period p)
        {
            switch (p.Unit)
            {
                case TimeUnit.Day:
                    lo = hi = p.Length;
                    break;
                case TimeUnit.Week:
                    lo = hi = 7 * p.Length;
                    break;
                case TimeUnit.Month:
                    lo = 28 * p.Length;
                    hi = 31 * p.Length;
                    break;
                case TimeUnit.Year:
                    lo = 365 * p.Length;
                    hi = 366 * p.Length;
                    break;
                default:
                    throw new ArgumentException("Unknown TimeUnit: " + p.Unit);
            }
        }
    }
}