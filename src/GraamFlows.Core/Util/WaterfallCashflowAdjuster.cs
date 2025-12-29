using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Util;

public static class WaterfallCashflowAdjuster
{
    public static void AdjustCashflows(IDeal deal, DealCashflows cashflows)
    {
        foreach (var tcf in cashflows.TrancheCashflows)
        {
            var dealVar = deal.DealVarByName("Original_Face_Adjustment", tcf.Key.TrancheName, "1");
            if (dealVar == null)
                continue;

            if (!tcf.Value.Cashflows.Any())
                continue;

            if (double.TryParse(dealVar.VariableValue, out var adjFactor) &&
                DateTime.TryParse(dealVar.VariableValue2, out var adjDate))
            {
                var firstCfDate = tcf.Value.Cashflows.Keys.Min();
                if (firstCfDate <= adjDate)
                    continue;
                AdjustCashflows(tcf.Value, adjFactor);
            }
        }
    }

    public static void AdjustCashflows(TrancheCashflows tcf, double adjFactor)
    {
        foreach (var cf in tcf.Cashflows)
        {
            cf.Value.Balance *= adjFactor;
            cf.Value.UnscheduledPrincipal *= adjFactor;
            cf.Value.ScheduledPrincipal *= adjFactor;
            cf.Value.BeginBalance *= adjFactor;
            cf.Value.Interest *= adjFactor;
            cf.Value.Expense *= adjFactor;
            cf.Value.ExpenseShortfall *= adjFactor;
            cf.Value.Writedown *= adjFactor;
            cf.Value.CumWritedown *= adjFactor;
            cf.Value.InterestShortfall *= adjFactor;
            cf.Value.AccumInterestShortfall *= adjFactor;
            cf.Value.InterestShortfallPayback *= adjFactor;
        }
    }
}