using GraamFlows.Objects.DataObjects;
using GraamFlows.Util;
using GraamFlows.Waterfall;

namespace GraamFlows.Triggers;

public class MinimumCreditEnhancementTrigger : Trigger
{
    public MinimumCreditEnhancementTrigger(IDeal deal, IDealTrigger trigger, IAssumptionMill assumps) : base(deal,
        trigger, assumps)
    {
        if (!double.TryParse(trigger.TriggerParam, out var ceValue))
            throw new DealModelingException(trigger.DealName,
                $"MinimumCreditEnhancementTrigger needs to have a numeric trigger value in param1, {trigger.TriggerParam} is invalid");

        var testClass = trigger.TriggerParam2;
        if (string.IsNullOrEmpty(testClass))
            throw new DealModelingException(trigger.DealName,
                "MinimumCreditEnhancementTrigger needs to have the Class Group to test in param2");

        var dealStructure = deal.DealStructures.SingleOrDefault(ds =>
            ds.ClassGroupName.Equals(testClass, StringComparison.InvariantCultureIgnoreCase));

        MinimumCreditEnhancementValue = ceValue;
        DealStructure = dealStructure ??
                        throw new DealModelingException(trigger.DealName, $"Class {testClass} is not valid!");
        ClassName = testClass;
    }

    public double MinimumCreditEnhancementValue { get; }
    public IDealStructure DealStructure { get; }
    public string ClassName { get; }


    public override TriggerValue TestTrigger(DynamicGroup group, DateTime cashflowDate, PeriodCashflows periodCf)
    {
        var dynClass = group.DynamicClasses.Single(ds => ds.DealStructure == DealStructure);
        var creditSupport = dynClass.CreditSupport();

        var passedCreditEnhancmentTest = Math.Round(creditSupport, 8) >= MinimumCreditEnhancementValue;
        return new TriggerValue(TriggerName, passedCreditEnhancmentTest, creditSupport, MinimumCreditEnhancementValue);
    }
}