using GraamFlows.Objects.DataObjects;
using GraamFlows.Triggers;
using GraamFlows.Waterfall;
using GraamFlows.Waterfall.Structures;

namespace GraamFlows.RulesEngine;

public class PayRuleExecutor : IPayRuleExecutor
{
    private readonly IFormulaExecutor _formulaExecutor;
    private readonly Dictionary<IPayRule, string> _ruleNameCache = new();

    public PayRuleExecutor(IFormulaExecutor formulaExecutor, BaseStructure waterfall)
    {
        Waterfall = waterfall;
        _formulaExecutor = formulaExecutor;
    }

    public BaseStructure Waterfall { get; }

    public CashflowAllocs ExecutePayRule(IPayRule payRule, List<TriggerValue> triggerResults, DynamicGroup dynGroup,
        PeriodCashflows periodCf)
    {
        var payRuleClass = dynGroup.ClassesByNameOrTag(payRule.ClassGroupName);

        if (payRuleClass == null || !payRuleClass.Any())
            if (!payRule.ClassGroupName.StartsWith("GROUP_"))
                return CashflowAllocs.Empty();

        var rulesResults = new RulesResults();
        _formulaExecutor.Reset(rulesResults, triggerResults, dynGroup, periodCf, payRuleClass);
        ExecuteRuleInternal(_formulaExecutor, payRule);
        var totalPmtNew = rulesResults.PaidAmount;

        if (rulesResults.PaidAmount > 0 && payRuleClass != null)
            // When pay balance exceeds tranche capacity for PayFrom = Rule, fall back to sequential distribution
            foreach (var dynClass in payRuleClass)
                /*if (dynClass.DealStructure.PayFromEnum == PayFromEnum.ProRata)
                   Waterfall.PayProRataClassSchedPrin(dynGroup, dynGroup.DynamicClasses, periodCf.CashflowDate, rulesResults.PaidAmount, periodCf);
                else if (dynClass.DealStructure.PayFromEnum == PayFromEnum.Sequential)*/
                Waterfall.PaySequentialClass(dynGroup, new[] { dynClass }, periodCf.CashflowDate, totalPmtNew, 0);
        /*else
               dynClass.Pay(periodCf.CashflowDate, totalPmtNew, 0);*/
        return new CashflowAllocs(0, totalPmtNew, 0, 0, 0);
    }

    private void ExecuteRuleInternal(IFormulaExecutor formulaExecutor, IPayRule rule)
    {
        if (!_ruleNameCache.TryGetValue(rule, out var ruleName))
        {
            ruleName = RulesBuilder.GetRuleName(rule);
            _ruleNameCache.Add(rule, ruleName);
        }

        formulaExecutor.EvaluateUnknown(ruleName);
    }
}