using GraamFlows.Objects.DataObjects;
using GraamFlows.RulesEngine;

namespace GraamFlows.Waterfall.MarketTranche;

public class PrincipalAndInterestMarketTranche : DynamicTranche
{
    public PrincipalAndInterestMarketTranche(IFormulaExecutor formulaExecutor, DynamicGroup dynamicGroup,
        ITranche tranche, DateTime settleDate) :
        base(formulaExecutor, dynamicGroup, tranche, settleDate)
    {
    }

    public override bool RecievesPrincipal()
    {
        return true;
    }
}