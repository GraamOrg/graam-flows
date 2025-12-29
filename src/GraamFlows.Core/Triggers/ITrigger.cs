using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall;

namespace GraamFlows.Triggers;

public interface ITrigger
{
    TriggerValue TestTrigger(DynamicGroup group, DateTime cashflowDate, PeriodCashflows periodCf);
}

public abstract class Trigger : ITrigger
{
    protected Trigger(IDeal deal, IDealTrigger trigger, IAssumptionMill assumps)
    {
        Deal = deal;
        DealTrigger = trigger;
        Assumps = assumps;
    }

    public IDeal Deal { get; }
    public IDealTrigger DealTrigger { get; }
    public IAssumptionMill Assumps { get; }
    public string TriggerName => DealTrigger.TriggerName;
    public abstract TriggerValue TestTrigger(DynamicGroup group, DateTime cashflowDate, PeriodCashflows periodCf);
}