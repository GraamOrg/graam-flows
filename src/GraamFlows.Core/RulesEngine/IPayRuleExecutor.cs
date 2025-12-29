using GraamFlows.Objects.DataObjects;
using GraamFlows.Triggers;
using GraamFlows.Waterfall;
using GraamFlows.Waterfall.Structures;

namespace GraamFlows.RulesEngine;

public interface IPayRuleExecutor
{
    BaseStructure Waterfall { get; }

    CashflowAllocs ExecutePayRule(IPayRule payRule, List<TriggerValue> triggerResults, DynamicGroup dynGroup,
        PeriodCashflows periodCf);
}