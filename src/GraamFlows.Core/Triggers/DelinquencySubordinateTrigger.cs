using GraamFlows.Objects.DataObjects;
using GraamFlows.Util;
using GraamFlows.Util.Collections;
using GraamFlows.Waterfall;

namespace GraamFlows.Triggers;

public class DelinquencySubordinateTrigger : Trigger
{
    private readonly Dictionary<DateTime, double> _dqAvg;
    private readonly string _seniorTranche;
    private readonly string _threshold;

    public DelinquencySubordinateTrigger(IDeal deal, IDealTrigger trigger, IAssumptionMill assumps, int monthsAvg,
        IEnumerable<PeriodCashflows> cashflows) : base(deal, trigger, assumps)
    {
        var queue = new FixedSizedQueue<double>(monthsAvg);
        _dqAvg = new Dictionary<DateTime, double>();

        // get current dq rate
        var fieldName = $"delinq_rate_{monthsAvg}m";
        var dqRateField = deal.DealFieldFieldValueByName(trigger.GroupNum, fieldName);
        if (dqRateField != null)
            for (var i = 0; i != monthsAvg; ++i)
                queue.Enqueue(dqRateField.ValueNum);

        // compute avg dq's
        foreach (var periodCf in cashflows)
        {
            queue.Enqueue(periodCf.DelinqBalance);
            _dqAvg.Add(periodCf.CashflowDate, queue.Average());
        }

        // get trigger params
        _threshold = trigger.TriggerParam;
        _seniorTranche = trigger.TriggerParam2;
    }

    public override TriggerValue TestTrigger(DynamicGroup group, DateTime cashflowDate, PeriodCashflows periodCf)
    {
        double subBalance;
        if (_seniorTranche != null)
        {
            var seniorTranche = group.ClassByName(_seniorTranche);
            subBalance = seniorTranche.SubordinateBalance();
        }
        else
        {
            subBalance = group.BeginningBalance;
        }

        var dqAvg = _dqAvg[cashflowDate];
        var threshold = GetTriggerThreshold(group, cashflowDate);

        var denom = subBalance - periodCf.CollateralLoss;
        if (denom <= 0)
            return new TriggerValue(TriggerName, true, 0, threshold);

        var result = dqAvg / denom;
        return new TriggerValue(TriggerName, result < threshold, result, threshold);
    }

    private double GetTriggerThreshold(DynamicGroup dynGroup, DateTime cfDate)
    {
        if (double.TryParse(_threshold, out var value))
            return value;

        return dynGroup.GetVariable(_threshold, cfDate);
    }
}