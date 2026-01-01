using GraamFlows.Objects.DataObjects;
using GraamFlows.Util.Calender;
using GraamFlows.Waterfall;

namespace GraamFlows.Util;

public static class TrancheExtension
{
    public static IDealStructure GetDealStructure(this ITranche tranche)
    {
        var dealStructures = tranche.Deal.DealStructures.Where(ds => ds.ClassGroupName == tranche.ClassReference)
            .ToList();
        if (dealStructures.Count > 1)
            throw new Exception(
                $"Cannot have more than one deal structures attached to a tranche! Tranche {tranche.TrancheName} has deal structures " +
                $"{string.Join(",", dealStructures.Select(ds => ds.ClassGroupName))}");

        return dealStructures.SingleOrDefault();
    }

    public static Calendar GetCalendar(this ITranche tranche)
    {
        return CalendarFactory.GetUSCalendar(tranche.HolidayCalendar);
    }

    public static DayCounter GetDayCounter(this ITranche tranche)
    {
        return CalendarFactory.GetDayCounter(tranche.DayCount);
    }
}