using GraamFlows.Objects.DataObjects;
using GraamFlows.Triggers;
using GraamFlows.Util;
using GraamFlows.Waterfall;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.RulesEngine;

public class GenericExecutor : IFormulaExecutor
{
    private readonly IDeal _deal;
    private readonly dynamic _rulesEngine;
    private bool _ignoreFormulaReset;

    public GenericExecutor(IDeal deal)
    {
        _deal = deal;
        _rulesEngine = RulesBuilder.CreateRulesInstance(deal);
    }

    public void Reset(RulesResults rulesResults, List<TriggerValue> triggerResults, DynamicGroup dynGroup,
        PeriodCashflows periodCf, IEnumerable<DynamicClass> payRuleClass)
    {
        _rulesEngine.Reset(rulesResults, triggerResults, dynGroup, periodCf, payRuleClass);
    }

    public void ResetTrancheFormulas(DynamicTranche dynamicTranche, IRateProvider rateProvider, DateTime cfDate,
        IEnumerable<DynamicTranche> allTranches)
    {
        if (_ignoreFormulaReset)
            return;
        try
        {
            _rulesEngine.ResetTrancheFormulas(dynamicTranche, rateProvider, cfDate, allTranches);
        }
        catch (Exception)
        {
            _ignoreFormulaReset = true;
            // do nothing, backwards compatibility
        }
    }

    public double EvaluateDouble(string funcName)
    {
        var ruleScriptType = (Type)_rulesEngine.GetType();
        var ruleMethod = ruleScriptType.GetMethod(funcName);
        if (ruleMethod == null)
            throw new DealModelingException(_deal.DealName, $"Unable to execute rule {funcName}");
        var result = (double)ruleMethod.Invoke((object)_rulesEngine, null);
        return result;
    }

    public object EvaluateUnknown(string functionName)
    {
        var ruleScriptType = (Type)_rulesEngine.GetType();
        var ruleMethod = ruleScriptType.GetMethod(functionName);
        if (ruleMethod == null)
            throw new DealModelingException(_deal.DealName, $"Unable to execute rule {functionName}");
        var result = ruleMethod.Invoke((object)_rulesEngine, null);
        return result;
    }
}