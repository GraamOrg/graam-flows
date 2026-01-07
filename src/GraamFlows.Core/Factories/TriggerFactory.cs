using GraamFlows.Objects.DataObjects;
using GraamFlows.Triggers;
using GraamFlows.Util;

namespace GraamFlows.Factories;

public static class TriggerFactory
{
    public static ITrigger GetTrigger(IDeal deal, IDealTrigger dealTrigger, IAssumptionMill assumps,
        IEnumerable<PeriodCashflows> periodCashflows)
    {
        if (dealTrigger.TriggerType.StartsWith("DELINQ_TRIGGER_SUB_"))
        {
            var months = Convert.ToInt32(dealTrigger.TriggerType.Replace("DELINQ_TRIGGER_SUB_", ""));
            return new DelinquencySubordinateTrigger(deal, dealTrigger, assumps, months, periodCashflows);
        }

        // DELINQ_TRIGGER without sub-months defaults to 6 months
        if (dealTrigger.TriggerType == "DELINQ_TRIGGER")
        {
            return new DelinquencySubordinateTrigger(deal, dealTrigger, assumps, 6, periodCashflows);
        }

        switch (dealTrigger.TriggerType)
        {
            case "DATE_TERMINATION":
                return new DateTerminationTrigger(deal, dealTrigger, assumps);
            case "COLLATERAL_VALUE":
                return new CollateralValueTerminationTrigger(deal, dealTrigger, assumps);
            case "CREDIT_ENHANCEMENT":
                return new MinimumCreditEnhancementTrigger(deal, dealTrigger, assumps);
            case "FORMULA_VOID":
            case "FORMULA_CONDITION":
            case "FORMULA_VALUE":
            case "FORMULA_VALUE_STR":
                return new TriggerFormulaEvaluator(deal, dealTrigger, assumps);
            case "FORMULA_CONDITION_STICKY":
                return new TriggerStickyCondition(deal, dealTrigger, assumps);

            default:
                throw new DealModelingException(dealTrigger.DealName,
                    $"{dealTrigger.TriggerName} is not a known trigger!");
        }
    }
}