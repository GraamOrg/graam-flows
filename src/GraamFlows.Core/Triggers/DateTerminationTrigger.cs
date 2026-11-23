using GraamFlows.Objects.DataObjects;
using GraamFlows.Util;
using GraamFlows.Waterfall;

namespace GraamFlows.Triggers;

public class DateTerminationTrigger : Trigger
{
    public DateTerminationTrigger(IDeal deal, IDealTrigger trigger, IAssumptionMill assumps) : base(deal, trigger,
        assumps)
    {
        SetParams();
        Redemption = RedemptionInterestParser.Parse(trigger);
    }

    /// <summary>What this call's Redemption Price pays beyond par and the Payment Date's interest.</summary>
    public RedemptionInterest Redemption { get; }

    public DateTime DateTriggerParam { get; private set; }

    public override TriggerValue TestTrigger(DynamicGroup group, DateTime cashflowDate, PeriodCashflows periodCf)
    {
        // check trigger
        var isTriggered = cashflowDate >= DateTriggerParam;
        if (!isTriggered)
            return null;

        var adjCfDate = cashflowDate;
        var anyTran = group.DynamicClasses.SelectMany(dc => dc.DynamicTranches).FirstOrDefault();
        if (anyTran != null)
            adjCfDate = anyTran.AdjustedCashflowDate(cashflowDate);

        if (adjCfDate < group.EarliestTerminationDate)
            group.EarliestTerminationDate = adjCfDate;

        var triggerForecast = Assumps.GetTriggerForecast(DealTrigger.TriggerName, group.GroupNum);
        if (triggerForecast == null && !DealTrigger.IsMandatory)
            return null;

        if (DealTrigger.IsMandatory || (triggerForecast != null && triggerForecast.AlwaysTrigger))
            return new TriggerValue(TriggerName, new TerminationTriggerExecuter(Redemption));

        if (triggerForecast == null)
            return null;

        if (triggerForecast.HasCustomParam &&
            DateTime.TryParse(triggerForecast.CustomParam, out var customTriggerParam))
            if (cashflowDate >= customTriggerParam)
                return new TriggerValue(TriggerName, new TerminationTriggerExecuter(Redemption));
        return null;
    }

    private void SetParams()
    {
        if (!DateTime.TryParse(DealTrigger.TriggerParam, out var param))
            throw new DealModelingException(DealTrigger.DealName,
                $"{DealTrigger.TriggerParam} is not valid for DateTerminationTrigger");
        DateTriggerParam = new DateTime(param.Year, param.Month, 01);
    }
}