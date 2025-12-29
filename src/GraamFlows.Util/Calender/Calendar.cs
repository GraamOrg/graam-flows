using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Util.Calender;

public class Calendar
{
    protected Calendar _calendar;

    public Calendar()
    {
        AddedHolidays = new List<DateTime>();
        RemovedHolidays = new List<DateTime>();
    }

    public Calendar(Calendar c)
    {
        _calendar = c;
        AddedHolidays = new List<DateTime>();
        RemovedHolidays = new List<DateTime>();
    }

    public List<DateTime> AddedHolidays { get; set; }
    public List<DateTime> RemovedHolidays { get; set; }

    public Calendar calendar
    {
        get => _calendar;
        set => _calendar = value;
    }

    // Wrappers for interface
    // <summary>
    // This method is used for output and comparison between
    // calendars. It is <b>not</b> meant to be used for writing
    // switch-on-type code.
    // </summary>
    // <returns>
    // The name of the calendar.
    // </returns>
    public virtual string Name()
    {
        return calendar.Name();
    }

    // <param name="d">DateTime</param>
    // <returns>Returns <tt>true</tt> iff the DateTime is a business day for the
    // given market.</returns>
    public virtual bool IsBusinessDay(DateTime d)
    {
        if (calendar.AddedHolidays.Contains(d))
            return false;
        if (calendar.RemovedHolidays.Contains(d))
            return true;
        return calendar.IsBusinessDay(d);
    }

    //<summary>
    // Returns <tt>true</tt> iff the weekday is part of the
    // weekend for the given market.
    //</summary>
    public virtual bool IsWeekend(DayOfWeek w)
    {
        return calendar.IsWeekend(w);
    }

    // other functions
    // <summary>
    // Returns whether or not the calendar is initialized
    // </summary>
    public bool Empty()
    {
        return (object)calendar == null;
    } //!  Returns whether or not the calendar is initialized

    /// <summary>
    ///     Returns <tt>true</tt> iff the DateTime is a holiday for the given
    ///     market.
    /// </summary>
    public bool IsHoliday(DateTime d)
    {
        return !IsBusinessDay(d);
    }

    public bool IsEndOfMonth(DateTime d)
    {
        return d.Month != Adjust(d.AddDays(1)).Month;
    }

    private DateTime EndOfMonth(DateTime date)
    {
        return new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
    }

    public DateTime Adjust(DateTime d, int day, BusinessDayConvention c = BusinessDayConvention.Following)
    {
        var adjD = new DateTime(d.Year, d.Month, day);
        return Adjust(adjD, c);
    }

    /// <summary>
    ///     Adjusts a non-business day to the appropriate near business day  with respect
    ///     to the given convention.
    /// </summary>
    public DateTime Adjust(DateTime d, BusinessDayConvention c = BusinessDayConvention.Following)
    {
        if (d == null) throw new ArgumentException("null DateTime");
        if (c == BusinessDayConvention.Unadjusted) return d;

        var d1 = d;
        if (c == BusinessDayConvention.Following || c == BusinessDayConvention.ModifiedFollowing ||
            c == BusinessDayConvention.HalfMonthModifiedFollowing)
        {
            while (IsHoliday(d1)) d1 = d1.AddDays(1);
            if (c == BusinessDayConvention.ModifiedFollowing || c == BusinessDayConvention.HalfMonthModifiedFollowing)
            {
                if (d1.Month != d.Month)
                    return Adjust(d, BusinessDayConvention.Preceding);
                if (c == BusinessDayConvention.HalfMonthModifiedFollowing)
                    if (d.Day <= 15 && d1.Day > 15)
                        return Adjust(d, BusinessDayConvention.Preceding);
            }
        }
        else if (c == BusinessDayConvention.Preceding || c == BusinessDayConvention.ModifiedPreceding)
        {
            while (IsHoliday(d1))
                d1 = d1.AddDays(-1);
            if (c == BusinessDayConvention.ModifiedPreceding && d1.Month != d.Month)
                return Adjust(d);
        }
        else if (c == BusinessDayConvention.Nearest)
        {
            var d2 = d;
            while (IsHoliday(d1) && IsHoliday(d2))
            {
                d1 = d1.AddDays(1);
                d2 = d2.AddDays(-1);
            }

            if (IsHoliday(d1))
                return d2;
            return d1;
        }
        else
        {
            throw new ArgumentException($"unknown business-day convention {c}");
        }

        return d1;
    }

    /// <summary>
    ///     Advances the given DateTime of the given number of business days and
    ///     returns the result.
    /// </summary>
    /// <remarks>The input DateTime is not modified</remarks>
    public DateTime Advance(DateTime d, int n, Period.TimeUnit unit,
        BusinessDayConvention c = BusinessDayConvention.Following, bool endOfMonth = false)
    {
        if (d == null) throw new ArgumentException("null DateTime");
        if (n == 0)
            return Adjust(d, c);
        if (unit == Period.TimeUnit.Day)
        {
            var d1 = d;
            if (n > 0)
                while (n > 0)
                {
                    d1 = d1.AddDays(1);
                    while (IsHoliday(d1))
                        d1 = d1.AddDays(1);
                    n--;
                }
            else
                while (n < 0)
                {
                    d1 = d1.AddDays(-1);
                    while (IsHoliday(d1))
                        d1 = d1.AddDays(-1);
                    n++;
                }

            return d1;
        }

        if (unit == Period.TimeUnit.Week)
        {
            var d1 = d + new Period(n, unit);
            return Adjust(d1, c);
        }
        else
        {
            var d1 = d + new Period(n, unit);
            if (endOfMonth && (unit == Period.TimeUnit.Month || unit == Period.TimeUnit.Year) && IsEndOfMonth(d))
                return EndOfMonth(d1);
            return Adjust(d1, c);
        }
    }

    /// <summary>
    ///     Advances the given DateTime as specified by the given period and
    ///     returns the result.
    /// </summary>
    /// <remarks>The input DateTime is not modified.</remarks>
    public DateTime Advance(DateTime d, Period p, BusinessDayConvention c = BusinessDayConvention.Following,
        bool endOfMonth = false)
    {
        return Advance(d, p.Length, p.Unit, c, endOfMonth);
    }

    public DateTime AdvanceMonth(DateTime d, int day, int months = 1,
        BusinessDayConvention c = BusinessDayConvention.Following)
    {
        d = d.AddMonths(months);
        var date = new DateTime(d.Year, d.Month, day);
        return Adjust(date, c);
    }

    /// <summary>
    ///     Calculates the number of business days between two given
    ///     DateTimes and returns the result.
    /// </summary>
    public int BusinessDaysBetween(DateTime from, DateTime to, bool includeFirst = true, bool includeLast = false)
    {
        var wd = 0;
        if (from != to)
        {
            if (from < to)
            {
                // the last one is treated separately to avoid incrementing DateTime::maxDateTime()
                for (var d = from; d < to; d = d.AddDays(1))
                    if (IsBusinessDay(d))
                        ++wd;
                if (IsBusinessDay(to))
                    ++wd;
            }
            else
            {
                for (var d = to; d < from; d = d.AddDays(1))
                    if (IsBusinessDay(d))
                        ++wd;
                if (IsBusinessDay(from))
                    ++wd;
            }

            if (IsBusinessDay(from) && !includeFirst)
                wd--;
            if (IsBusinessDay(to) && !includeLast)
                wd--;

            if (from > to)
                wd = -wd;
        }

        return wd;
    }

    /// <summary>
    ///     Adds a DateTime to the set of holidays for the given calendar.
    /// </summary>
    public void AddHoliday(DateTime d)
    {
        // if d was a genuine holiday previously removed, revert the change
        calendar.RemovedHolidays.Remove(d);
        // if it's already a holiday, leave the calendar alone.
        // Otherwise, add it.
        if (IsBusinessDay(d))
            calendar.AddedHolidays.Add(d);
    }

    /// <summary>
    ///     Removes a DateTime from the set of holidays for the given calendar.
    /// </summary>
    public void RemoveHoliday(DateTime d)
    {
        // if d was an artificially-added holiday, revert the change
        calendar.AddedHolidays.Remove(d);
        // if it's already a business day, leave the calendar alone.
        // Otherwise, add it.
        if (!IsBusinessDay(d))
            calendar.RemovedHolidays.Add(d);
    }

    /// <summary>
    ///     Returns the holidays between two DateTimes
    /// </summary>
    public static List<DateTime> holidayList(Calendar calendar, DateTime from, DateTime to,
        bool includeWeekEnds = false)
    {
        var result = new List<DateTime>();

        for (var d = from; d <= to; d = d.AddDays(1))
            if (calendar.IsHoliday(d)
                && (includeWeekEnds || !calendar.IsWeekend(d.DayOfWeek)))
                result.Add(d);
        return result;
    }

    // Operators
    public static bool operator ==(Calendar c1, Calendar c2)
    {
        // If both are null, or both are same instance, return true.
        if (ReferenceEquals(c1, c2))
            return true;

        // If one is null, but not both, return false.
        if ((object)c1 == null || (object)c2 == null)
            return false;

        return (c1.Empty() && c2.Empty())
               || (!c1.Empty() && !c2.Empty() && c1.Name() == c2.Name());
    }

    public static bool operator !=(Calendar c1, Calendar c2)
    {
        return !(c1 == c2);
    }

    public override bool Equals(object o)
    {
        return this == (Calendar)o;
    }

    public override int GetHashCode()
    {
        return 0;
    }

    /// <summary>
    ///     This class provides the means of determining the Easter
    ///     Monday for a given year, as well as specifying Saturdays
    ///     and Sundays as weekend days.
    /// </summary>
    public class WesternImpl : Calendar
    {
        private readonly int[] EasterMonday =
        {
            98, 90, 103, 95, 114, 106, 91, 111, 102, // 1901-1909
            87, 107, 99, 83, 103, 95, 115, 99, 91, 111, // 1910-1919
            96, 87, 107, 92, 112, 103, 95, 108, 100, 91, // 1920-1929
            111, 96, 88, 107, 92, 112, 104, 88, 108, 100, // 1930-1939
            85, 104, 96, 116, 101, 92, 112, 97, 89, 108, // 1940-1949
            100, 85, 105, 96, 109, 101, 93, 112, 97, 89, // 1950-1959
            109, 93, 113, 105, 90, 109, 101, 86, 106, 97, // 1960-1969
            89, 102, 94, 113, 105, 90, 110, 101, 86, 106, // 1970-1979
            98, 110, 102, 94, 114, 98, 90, 110, 95, 86, // 1980-1989
            106, 91, 111, 102, 94, 107, 99, 90, 103, 95, // 1990-1999
            115, 106, 91, 111, 103, 87, 107, 99, 84, 103, // 2000-2009
            95, 115, 100, 91, 111, 96, 88, 107, 92, 112, // 2010-2019
            104, 95, 108, 100, 92, 111, 96, 88, 108, 92, // 2020-2029
            112, 104, 89, 108, 100, 85, 105, 96, 116, 101, // 2030-2039
            93, 112, 97, 89, 109, 100, 85, 105, 97, 109, // 2040-2049
            101, 93, 113, 97, 89, 109, 94, 113, 105, 90, // 2050-2059
            110, 101, 86, 106, 98, 89, 102, 94, 114, 105, // 2060-2069
            90, 110, 102, 86, 106, 98, 111, 102, 94, 114, // 2070-2079
            99, 90, 110, 95, 87, 106, 91, 111, 103, 94, // 2080-2089
            107, 99, 91, 103, 95, 115, 107, 91, 111, 103, // 2090-2099
            88, 108, 100, 85, 105, 96, 109, 101, 93, 112, // 2100-2109
            97, 89, 109, 93, 113, 105, 90, 109, 101, 86, // 2110-2119
            106, 97, 89, 102, 94, 113, 105, 90, 110, 101, // 2120-2129
            86, 106, 98, 110, 102, 94, 114, 98, 90, 110, // 2130-2139
            95, 86, 106, 91, 111, 102, 94, 107, 99, 90, // 2140-2149
            103, 95, 115, 106, 91, 111, 103, 87, 107, 99, // 2150-2159
            84, 103, 95, 115, 100, 91, 111, 96, 88, 107, // 2160-2169
            92, 112, 104, 95, 108, 100, 92, 111, 96, 88, // 2170-2179
            108, 92, 112, 104, 89, 108, 100, 85, 105, 96, // 2180-2189
            116, 101, 93, 112, 97, 89, 109, 100, 85, 105 // 2190-2199
        };

        // Western calendars
        public WesternImpl()
        {
        }

        public WesternImpl(Calendar c) : base(c)
        {
        }

        public override bool IsWeekend(DayOfWeek w)
        {
            return w == DayOfWeek.Saturday || w == DayOfWeek.Sunday;
        }

        /// <summary>
        ///     Expressed relative to first day of year
        /// </summary>
        /// <param name="y"></param>
        /// <returns></returns>
        public int easterMonday(int y)
        {
            return EasterMonday[y - 1901];
        }
    }

    /// <summary>
    ///     This class provides the means of determining the Orthodox
    ///     Easter Monday for a given year, as well as specifying
    ///     Saturdays and Sundays as weekend days.
    /// </summary>
    public class OrthodoxImpl : Calendar
    {
        private readonly int[] EasterMonday =
        {
            105, 118, 110, 102, 121, 106, 126, 118, 102, // 1901-1909
            122, 114, 99, 118, 110, 95, 115, 106, 126, 111, // 1910-1919
            103, 122, 107, 99, 119, 110, 123, 115, 107, 126, // 1920-1929
            111, 103, 123, 107, 99, 119, 104, 123, 115, 100, // 1930-1939
            120, 111, 96, 116, 108, 127, 112, 104, 124, 115, // 1940-1949
            100, 120, 112, 96, 116, 108, 128, 112, 104, 124, // 1950-1959
            109, 100, 120, 105, 125, 116, 101, 121, 113, 104, // 1960-1969
            117, 109, 101, 120, 105, 125, 117, 101, 121, 113, // 1970-1979
            98, 117, 109, 129, 114, 105, 125, 110, 102, 121, // 1980-1989
            106, 98, 118, 109, 122, 114, 106, 118, 110, 102, // 1990-1999
            122, 106, 126, 118, 103, 122, 114, 99, 119, 110, // 2000-2009
            95, 115, 107, 126, 111, 103, 123, 107, 99, 119, // 2010-2019
            111, 123, 115, 107, 127, 111, 103, 123, 108, 99, // 2020-2029
            119, 104, 124, 115, 100, 120, 112, 96, 116, 108, // 2030-2039
            128, 112, 104, 124, 116, 100, 120, 112, 97, 116, // 2040-2049
            108, 128, 113, 104, 124, 109, 101, 120, 105, 125, // 2050-2059
            117, 101, 121, 113, 105, 117, 109, 101, 121, 105, // 2060-2069
            125, 110, 102, 121, 113, 98, 118, 109, 129, 114, // 2070-2079
            106, 125, 110, 102, 122, 106, 98, 118, 110, 122, // 2080-2089
            114, 99, 119, 110, 102, 115, 107, 126, 118, 103, // 2090-2099
            123, 115, 100, 120, 112, 96, 116, 108, 128, 112, // 2100-2109
            104, 124, 109, 100, 120, 105, 125, 116, 108, 121, // 2110-2119
            113, 104, 124, 109, 101, 120, 105, 125, 117, 101, // 2120-2129
            121, 113, 98, 117, 109, 129, 114, 105, 125, 110, // 2130-2139
            102, 121, 113, 98, 118, 109, 129, 114, 106, 125, // 2140-2149
            110, 102, 122, 106, 126, 118, 103, 122, 114, 99, // 2150-2159
            119, 110, 102, 115, 107, 126, 111, 103, 123, 114, // 2160-2169
            99, 119, 111, 130, 115, 107, 127, 111, 103, 123, // 2170-2179
            108, 99, 119, 104, 124, 115, 100, 120, 112, 103, // 2180-2189
            116, 108, 128, 119, 104, 124, 116, 100, 120, 112 // 2190-2199
        };

        // Orthodox calendars
        public OrthodoxImpl()
        {
        }

        public OrthodoxImpl(Calendar c) : base(c)
        {
        }

        public override bool IsWeekend(DayOfWeek w)
        {
            return w == DayOfWeek.Saturday || w == DayOfWeek.Sunday;
        }

        /// <summary>
        ///     expressed relative to first day of year
        /// </summary>
        /// <param name="y"></param>
        /// <returns></returns>
        public int easterMonday(int y)
        {
            return EasterMonday[y - 1901];
        }
    }
}