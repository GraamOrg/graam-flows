using GraamFlows.Factories;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Triggers;

namespace GraamFlows.Util;

public static class TriggerExtensions
{
    public static DateTime EarliestMandatoryDateRedemption(this IEnumerable<IDealTrigger> redemps, string group)
    {
        var dateTermination = "DATE_TERMINATION";
        var mandatoryRedemps = redemps
            .Where(redemp => redemp.TriggerType == dateTermination && redemp.IsMandatory && redemp.GroupNum == group)
            .Select(p => Convert.ToDateTime(p.TriggerParam)).OrderBy(p => p).ToList();
        if (mandatoryRedemps.Any())
            return mandatoryRedemps.First();
        return DateTime.MaxValue;
    }

    public static IList<ITrigger> LoadTriggers(this IEnumerable<IDealTrigger> redemps, IDeal deal,
        IAssumptionMill assumps, string groupNum, IEnumerable<PeriodCashflows> periodCashflows)
    {
        return redemps.Where(redemp => redemp.GroupNum == groupNum)
            .Select(redemp => TriggerFactory.GetTrigger(deal, redemp, assumps, periodCashflows)).ToList();
    }
}