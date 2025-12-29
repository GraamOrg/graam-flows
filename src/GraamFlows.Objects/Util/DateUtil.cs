using System.Globalization;

namespace GraamFlows.Objects.Util;

public static class DateUtil
{
    private static readonly int RefYear = 1990;
    public static DateTime RefDate;

    static DateUtil()
    {
        RefDate = new DateTime(RefYear, 1, 1);
    }

    public static int CalcAbsT(int date)
    {
        return CalcAbsT(date / 10000, date % 10000 / 100);
    }

    public static int CalcAbsTFromYearMonth(int yyyymm)
    {
        return CalcAbsT(yyyymm / 100, yyyymm % 100);
    }

    public static int CalcAbsT(DateTime date)
    {
        return CalcAbsT(date.Year, date.Month);
    }

    public static int CalcAbsT(int year, int monthOfYear)
    {
        return (year - RefYear) * 12 + (monthOfYear - 1);
    }

    public static int CalcYearMonthFromAbsT(int absT)
    {
        if (absT >= 0)
            return (RefYear + absT / 12) * 100 + absT % 12 + 1;
        return (RefYear + (absT + 1) / 12 - 1) * 100 + (absT + 1200) % 12 + 1;
    }

    public static int CalcYearMonthDayFromAbsT(int absT)
    {
        return CalcYearMonthFromAbsT(absT) * 100 + 1;
    }

    public static DateTime CalcDate(int absT)
    {
        return RefDate.AddMonths(absT);
    }

    public static DateTime CalcDateTime(int absT)
    {
        return RefDate.AddMonths(absT);
    }

    public static int Month(int absT)
    {
        return absT % 12 + 1;
    }

    public static DateTime LocalDateFromYearMonthDay(int yyyymmdd)
    {
        return new DateTime(yyyymmdd / 10000, yyyymmdd / 100 % 100, yyyymmdd % 100);
    }

    public static DateTime TryParseDate(string dateString)
    {
        if (DateTime.TryParseExact(dateString, "yyyy-MM-dd", null, DateTimeStyles.None, out var date))
            return date;

        if (DateTime.TryParseExact(dateString, "yyyyMMdd", null, DateTimeStyles.None, out date))
            return date;

        if (DateTime.TryParseExact(dateString, "MM/dd/yy", null, DateTimeStyles.None, out date))
            return date;

        if (DateTime.TryParseExact(dateString, "MM/dd/yyyy", null, DateTimeStyles.None, out date))
            return date;

        if (DateTime.TryParseExact(dateString, "MM/d/yyyy", null, DateTimeStyles.None, out date))
            return date;

        if (DateTime.TryParseExact(dateString, "M/d/yyyy", null, DateTimeStyles.None, out date))
            return date;

        if (DateTime.TryParseExact(dateString, "MM/yyyy", null, DateTimeStyles.None, out date))
            return date;

        if (DateTime.TryParseExact(dateString, "MM/yy", null, DateTimeStyles.None, out date))
            return date;

        if (DateTime.TryParseExact(dateString, "MMyyyy", null, DateTimeStyles.None, out date))
            return date;

        date = DateTime.Parse(dateString);
        return date;
    }
}