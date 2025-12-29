using GraamFlows.Objects.TypeEnum;
using GraamFlows.Util.Calender.Calendars;
using GraamFlows.Util.Calender.DayCounters;

namespace GraamFlows.Util.Calender;

public static class CalendarFactory
{
    public static BusinessDayConvention GetBusinessDayConvention(string businessDayConvention)
    {
        BusinessDayConvention busDayConv;
        if (Enum.TryParse(businessDayConvention, out busDayConv))
            return busDayConv;

        throw new ArgumentException($"{businessDayConvention} is not valid!");
    }

    public static DayCounter GetDayCounter(string dayCounter)
    {
        if (string.IsNullOrEmpty(dayCounter))
            return null;

        switch (dayCounter.ToLower())
        {
            case "actual360":
            case "actual/360":
            case "act360":
            case "act/360":
                return new Actual360();
            case "actual365":
            case "actual/365":
            case "act365":
            case "act/365":
                return new Actual365();
            case "actualactual":
            case "actual/actual":
            case "actualactualisda":
            case "actual/actualisda":
            case "actact":
            case "act/act":
            case "actactisda":
            case "act/actisda":
                return new ActualActualISDA();
            case "actualactualisma":
            case "actual/actualisma":
            case "actactisma":
            case "act/actisma":
                return new ActualActualISMA();
            case "thirty360":
            case "thirty360us":
            case "30/360":
            case "30360us":
                return new Thirty360Us();
            case "thirty360e":
            case "30/360e":
            case "30360e":
                return new Thirty360E();
            case "thirty360italy":
            case "30/360italy":
            case "30360italy":
                return new Thirty360Italy();
            default:
                throw new ArgumentException($"{dayCounter} is not valid!");
        }
    }

    public static Calendar GetUSCalendar(string calendarName)
    {
        UnitedStates.Market market;
        if (Enum.TryParse(calendarName, out market))
            return new UnitedStates(market);
        throw new ArgumentException($"{calendarName} is not a valid US calendar!");
    }
}