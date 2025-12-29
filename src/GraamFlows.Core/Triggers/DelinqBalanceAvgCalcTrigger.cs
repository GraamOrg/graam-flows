using GraamFlows.Objects.DataObjects;
using GraamFlows.RulesEngine;
using GraamFlows.Waterfall;

namespace GraamFlows.Triggers;

public class DelinqBalanceAvgCalcTrigger : Trigger
{
    private readonly int _months;
    private readonly dynamic _rulesInstance;
    private readonly string _varName;
    private readonly string _varNameQueue;

    public DelinqBalanceAvgCalcTrigger(IDeal deal, IDealTrigger trigger, IAssumptionMill assumps) : base(deal,
        trigger, assumps)
    {
        _months = trigger.TriggerParam == null ? 6 : int.Parse(trigger.TriggerParam);
        _varName = trigger.TriggerName;
        _varNameQueue = $"{_varName}Queue";
        _rulesInstance = RulesBuilder.CreateRulesInstance(deal);
    }

    public override TriggerValue TestTrigger(DynamicGroup dynGroup, DateTime cashflowDate, PeriodCashflows periodCf)
    {
        var value = dynGroup.GetVariableObj(_varNameQueue);
        if (value.GetType() != typeof(Queue<double>))
        {
            value = new Queue<double>(_months);
            dynGroup.SetVariable(_varNameQueue, value);
        }

        var queue = (Queue<double>)value;
        if (queue.Count >= _months)
            queue.Dequeue();
        queue.Enqueue(periodCf.DelinqBalance);
        var result = queue.Average();
        dynGroup.SetVariable(_varName, result);

        return new TriggerValue(DealTrigger.TriggerName, true, result);
    }
}