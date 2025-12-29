using GraamFlows.Objects.DataObjects;
using GraamFlows.Util;
using GraamFlows.Waterfall;

namespace GraamFlows.Triggers;

public class TriggerStickyCondition : TriggerFormulaEvaluator
{
    public TriggerStickyCondition(IDeal deal, IDealTrigger trigger, IAssumptionMill assumps) : base(deal, trigger,
        assumps)
    {
    }

    public override TriggerValue TestTrigger(DynamicGroup dynGroup, DateTime cashflowDate, PeriodCashflows periodCf)
    {
        if (dynGroup.FailedStickyTriggers.Contains(DealTrigger.TriggerName))
            return new TriggerValue(DealTrigger.TriggerName, false);
        var triggerResult = base.TestTrigger(dynGroup, cashflowDate, periodCf);
        if (triggerResult.TriggerResultType != TriggerValueType.BooleanValue)
            throw new DealModelingException(dynGroup.Deal.DealName,
                $"Trigger {DealTrigger.TriggerName} must be a condition!");

        if (!triggerResult.TriggerResult)
            dynGroup.FailedStickyTriggers.Add(DealTrigger.TriggerName);
        return triggerResult;
    }
}