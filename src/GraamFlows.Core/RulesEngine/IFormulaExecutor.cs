using GraamFlows.Objects.DataObjects;
using GraamFlows.Triggers;
using GraamFlows.Waterfall;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.RulesEngine;

public interface IFormulaExecutor
{
    void Reset(RulesResults rulesResults, List<TriggerValue> triggerResults, DynamicGroup dynGroup,
        PeriodCashflows periodCf, IEnumerable<DynamicClass> payRuleClass);

    void ResetTrancheFormulas(DynamicTranche dynamicTranche, IRateProvider rateProvider, DateTime cfDate,
        IEnumerable<DynamicTranche> allTranches);

    double EvaluateDouble(string functionName);
    object EvaluateUnknown(string functionName);
}