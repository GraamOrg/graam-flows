using GraamFlows.Objects.DataObjects;
using GraamFlows.RulesEngine;
using GraamFlows.Util;
using GraamFlows.Waterfall;

namespace GraamFlows.Triggers;

public class TriggerFormulaEvaluator : Trigger
{
    private readonly dynamic _rulesInstance;

    public TriggerFormulaEvaluator(IDeal deal, IDealTrigger trigger, IAssumptionMill assumps) : base(deal, trigger,
        assumps)
    {
        _rulesInstance = RulesBuilder.CreateRulesInstance(deal);
    }

    public override TriggerValue TestTrigger(DynamicGroup dynGroup, DateTime cashflowDate, PeriodCashflows periodCf)
    {
        var ruleScriptType = (Type)_rulesInstance.GetType();
        var funcName = RulesBuilder.GetTriggerName(DealTrigger);
        var ruleMethod = ruleScriptType.GetMethod(funcName);
        if (ruleMethod == null)
            throw new DealModelingException(DealTrigger.DealName, $"Unable to execute rule {DealTrigger.TriggerName}");

        var rulesResults = new RulesResults();
        _rulesInstance.Reset(rulesResults, null, dynGroup, periodCf, null);
        var returnValue = ruleMethod.Invoke((object)_rulesInstance, null);
        if (returnValue == null)
            return new TriggerValue(DealTrigger.TriggerName);
        if (returnValue is bool boolVal)
            return new TriggerValue(DealTrigger.TriggerName, boolVal);
        if (double.TryParse(returnValue.ToString(), out var doubVal))
            return new TriggerValue(DealTrigger.TriggerName, true, doubVal);
        return new TriggerValue(DealTrigger.TriggerName, true, returnValue.ToString());
    }
}