using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall;

namespace GraamFlows.Util;

public static class DynamicClassExtensions
{
    public static DealCashflows CreateDealCashflows(this ICollection<DynamicGroup> dynamicGroups,
        CollateralCashflows collateralCashflows, IAssumptionMill assumps)
    {
        var dealCashflows = new DealCashflows();
        dealCashflows.AssetCashflows = collateralCashflows.AssetCashflows;
        CollateralCashflows.ComputeAggregates(collateralCashflows.PeriodCashflows);
        dealCashflows.CollateralCashflows = collateralCashflows.PeriodCashflows;
        dealCashflows.TriggerResults = dynamicGroups.SelectMany(dg => dg.TriggerResults).ToList();

        foreach (var dynGroup in dynamicGroups)
        {
            dealCashflows.EarliestTerminationDates.Add(dynGroup.GroupNum, dynGroup.EarliestTerminationDate);

            // Add FundsAccount (reserve) cashflows if present
            if (dynGroup.FundsAccount != null)
            {
                var reserve = dynGroup.FundsAccount;
                var reserveDayCounter = reserve.Tranche.GetDayCounter();
                var reserveStartAccPeriod = reserve.Cashflows.Any()
                    ? reserve.Cashflows.First().Key.AddMonths(-1)
                    : DateTime.MinValue;
                var reserveCashFlows = new TrancheCashflows(reserve.Tranche, reserveDayCounter, assumps,
                    reserve.Cashflows, reserveStartAccPeriod);
                dealCashflows.ClassCashflows.Add(reserve.Tranche, reserveCashFlows);
            }

            foreach (var dynClass in dynGroup.DynamicClasses)
            {
                if (dealCashflows.ClassCashflows.ContainsKey(dynClass.Tranche))
                    continue;
                var clDayCounter = dynClass.Tranche.GetDayCounter();
                var startAccPeriod = DateTime.MinValue;
                if (dynClass.Cashflows.Any())
                    startAccPeriod = dynClass.Cashflows.First().Key.AddMonths(-1);
                var classCashFlows = new TrancheCashflows(dynClass.Tranche, clDayCounter, assumps, dynClass.Cashflows,
                    startAccPeriod);
                dealCashflows.ClassCashflows.Add(dynClass.Tranche, classCashFlows);

                foreach (var dynTran in dynClass.DynamicTranches)
                {
                    var dayCounter = dynTran.Tranche.GetDayCounter();
                    if (dynClass.Cashflows.Any())
                        startAccPeriod = dynTran.AdjustedCashflowDate(dynClass.Cashflows.First().Key.AddMonths(-1));
                    if (dynTran.Cashflows.Any() && dynTran.AdjustedCashflowDate(dynTran.Tranche.FirstPayDate).Date ==
                        dynTran.Cashflows.First().Key.Date)
                        startAccPeriod = dynTran.Tranche.FirstSettleDate;

                    var tranCashFlows = new TrancheCashflows(dynTran.Tranche, dayCounter, assumps, dynTran.Cashflows,
                        startAccPeriod);
                    dealCashflows.TrancheCashflows.Add(dynTran.Tranche, tranCashFlows);
                }
            }

            var allTrans = dynGroup.DynamicClasses.SelectMany(dc => dc.DynamicTranches).ToList();
            foreach (var tran in allTrans)
            {
                if (!dealCashflows.ContributedGroups.TryGetValue(tran.Tranche.TrancheName, out var contributedGroups))
                {
                    contributedGroups = new HashSet<string>();
                    dealCashflows.ContributedGroups.Add(tran.Tranche.TrancheName, contributedGroups);
                }

                contributedGroups.Add(dynGroup.GroupNum);
            }
        }

        return dealCashflows;
    }
}