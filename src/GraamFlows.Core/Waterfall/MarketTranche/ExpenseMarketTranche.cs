using GraamFlows.Objects.DataObjects;
using GraamFlows.RulesEngine;

namespace GraamFlows.Waterfall.MarketTranche;

public class ExpenseMarketTranche : DynamicTranche
{
    public ExpenseMarketTranche(IFormulaExecutor formulaExecutor, DynamicGroup dynamicGroup, ITranche tranche,
        DateTime settleDate) :
        base(formulaExecutor, dynamicGroup, tranche, settleDate)
    {
    }

    public override bool RecievesPrincipal()
    {
        return false;
    }
}