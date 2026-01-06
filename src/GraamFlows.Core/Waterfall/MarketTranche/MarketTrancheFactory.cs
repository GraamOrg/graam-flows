using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.RulesEngine;

namespace GraamFlows.Waterfall.MarketTranche;

public static class MarketTrancheFactory
{
    public static DynamicTranche GetDynamicMarketTranche(IFormulaExecutor formulaExecutor, DynamicGroup dynamicGroup,
        ITranche tranche, DateTime settleDate)
    {
        switch (tranche.CashflowTypeEnum)
        {
            case CashflowType.InterestOnly:
                return new InterestOnlyMarketTranche(formulaExecutor, dynamicGroup, tranche, settleDate);
            case CashflowType.PrincipalOnly:
                return new PrincipalOnlyMarketTranche(formulaExecutor, dynamicGroup, tranche, settleDate);
            case CashflowType.PrincipalAndInterest:
                return new PrincipalAndInterestMarketTranche(formulaExecutor, dynamicGroup, tranche, settleDate);
            case CashflowType.Expense:
                return new ExpenseMarketTranche(formulaExecutor, dynamicGroup, tranche, settleDate);
            case CashflowType.Reserve:
                // Reserve tranches use DynamicFundsAccount for logic; use PI tranche as container
                return new PrincipalAndInterestMarketTranche(formulaExecutor, dynamicGroup, tranche, settleDate);
            default:
                throw new ArgumentException(
                    $"Deal {tranche.Deal.DealName}, Tranche {tranche.TrancheName} with cashflow type {tranche.CashflowType} does not have a market tranche!");
        }
    }
}