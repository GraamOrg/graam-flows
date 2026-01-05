using System.Diagnostics;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.RulesEngine;
using GraamFlows.Triggers;
using GraamFlows.Util;
using GraamFlows.Waterfall.MarketTranche;
using GraamFlows.Waterfall.Structures;

namespace GraamFlows.Waterfall.MarketTranche
{
    public class TrancheAllocator : ITrancheAllocator
    {
        public void AllocateTranches(IWaterfall waterfall, IFormulaExecutor formulaExecutor,
            IList<DynamicGroup> dynGroups, IRateProvider rateProvider, DateTime cfDate,
            List<TriggerValue> triggerValues, IList<PeriodCashflows> periodCfs, IList<DynamicClass> payFromAllocator)
        {
            if (!dynGroups.Any())
                return;

            // Principal allocation now handled directly by DynamicClass.Pay() which allocates to child tranches.
            // AllocatePrincipal is no longer needed and would cause double allocation.

            var deal = dynGroups.First().Deal;
            if (deal.InterestTreatmentEnum == InterestTreatmentEnum.Collateral)
                AllocateCollateralInterest(dynGroups, rateProvider, cfDate, periodCfs);
            else if (deal.InterestTreatmentEnum == InterestTreatmentEnum.Guaranteed)
                foreach (var dynGroup in dynGroups)
                {
                    var periodCf = periodCfs.SingleOrDefault(p => p.GroupNum == dynGroup.GroupNum);
                    if (periodCf != null)
                        formulaExecutor.Reset(null, triggerValues, dynGroup, periodCf, null);
                    AllocateInterestGuaranteedTranches(dynGroup, rateProvider, cfDate);
                }
            else
                throw new DealModelingException(deal.DealName,
                    $"Interest treatment {deal.InterestTreatmentEnum} is not supported!");
        }

        public List<InterestPayment> GetInterestCollateralTranches(IList<DynamicGroup> dynGroups,
            IRateProvider rateProvider, DateTime cfDate, IList<PeriodCashflows> periodCfs)
        {
            var payList = new List<InterestPayment>();
            var dynTranCross = new HashSet<DynamicTranche>();
            double availableInterest = 0;
            double serviceFee = 0;

            var allTrans = new HashSet<DynamicTranche>();
            var expenses = dynGroups.SelectMany(dg => dg.DynamicClasses.SelectMany(dc => dc.DynamicTranches))
                .Where(dc => dc.DealStructure.PayFromEnum == PayFromEnum.Expense).Distinct()
                .Sum(dc => dc.GetCashflow(cfDate).Expense);

            foreach (var dynGroup in dynGroups)
            {
                var periodCf = periodCfs.SingleOrDefault(p => p.GroupNum == dynGroup.GroupNum);
                if (periodCf == null)
                    continue;

                availableInterest += periodCf.NetInterest;
                serviceFee += periodCf.ServiceFee;
                var reserveFund = dynGroup.FundsAccount;
                if (reserveFund != null)
                    availableInterest -= reserveFund.Debits();

                foreach (var crossDynTran in dynGroup.DynamicClasses.SelectMany(dc => dc.DynamicTranches).Where(dc =>
                             dc.DealStructure.GroupNum == "0" &&
                             dc.Tranche.TrancheTypeEnum != TrancheTypeEnum.Certificate)) dynTranCross.Add(crossDynTran);

                foreach (var dynTran in dynGroup.DynamicClasses.SelectMany(dc => dc.DynamicTranches))
                    allTrans.Add(dynTran);
            }

            availableInterest -= expenses;
            var amtRemaining = availableInterest;
            foreach (var dynGroup in dynGroups)
            {
                var periodCf = periodCfs.SingleOrDefault(p => p.GroupNum == dynGroup.GroupNum);
                if (periodCf == null)
                    continue;
                amtRemaining = CalculateGroupInterest(payList, dynGroup, rateProvider, cfDate, periodCf, amtRemaining);
            }

            // allocate tranches that are between groups
            amtRemaining -= CalculateTrancheInterest(payList, cfDate, rateProvider, dynTranCross.ToList(), amtRemaining,
                allTrans.ToList(), serviceFee);

            // anything left goes to the residual
            if (amtRemaining > .01)
            {
                var residTranche =
                    allTrans.SingleOrDefault(dt => dt.Tranche.TrancheTypeEnum == TrancheTypeEnum.ResidualInterest);
                if (residTranche != null)
                {
                    var tranCashflow = residTranche.GetCashflow(cfDate);
                    payList.Add(new InterestPayment(residTranche, tranCashflow, rateProvider, null, allTrans,
                        amtRemaining));
                    amtRemaining = 0;
                }
                else
                {
                    var residClass =
                        allTrans.SingleOrDefault(dc => dc.DealStructure.PayFromEnum == PayFromEnum.Residual);
                    if (residClass != null)
                    {
                        var tranCashflow = residClass.GetCashflow(cfDate);
                        payList.Add(new InterestPayment(residClass, tranCashflow, rateProvider, null, allTrans,
                            amtRemaining));
                        amtRemaining = 0;
                    }
                    else
                    {
                        // For Auto ABS deals, excess interest goes to the OC/Modeling tranche (balance writeup)
                        var modelingTranche =
                            allTrans.SingleOrDefault(dc => dc.Tranche.TrancheTypeEnum == TrancheTypeEnum.Certificate);
                        if (modelingTranche != null)
                        {
                            var tranCashflow = modelingTranche.GetCashflow(cfDate);
                            // For OC tranches, excess interest is applied as principal writeup, not interest payment
                            tranCashflow.ExcessInterest = amtRemaining;
                            amtRemaining = 0;
                        }
                    }
                }
            }

            if (amtRemaining > 1)
                Exceptions.InterestUnproperlyDistributedException(cfDate, amtRemaining,
                    availableInterest - amtRemaining);
            return payList;
        }

        private void AllocateCollateralInterest(IList<DynamicGroup> dynGroups, IRateProvider rateProvider,
            DateTime cfDate, IList<PeriodCashflows> periodCfs)
        {
            var payList = GetInterestCollateralTranches(dynGroups, rateProvider, cfDate, periodCfs);
            foreach (var item in payList)
                item.DynamicTranche.PayInterest(item.TrancheCashflow, item.RateProvider, item.Assumps, item.AllTranches,
                    item.Interest);
        }

        private void AllocateInterestGuaranteedTranches(DynamicGroup dynGroup, IRateProvider rateProvider,
            DateTime cfDate)
        {
            var allTrans = dynGroup.DynamicClasses.SelectMany(dc => dc.DynamicTranches).ToList();
            foreach (var dynTran in allTrans.Where(t => t.Tranche.CouponTypeEnum != CouponType.TrancheWac))
            {
                var cfWritedown = dynTran.GetCashflow(cfDate);
                dynTran.PayInterest(cfWritedown, rateProvider, null, allTrans);
            }

            foreach (var dynTran in allTrans.Where(t => t.Tranche.CouponTypeEnum == CouponType.TrancheWac))
            {
                var cfWritedown = dynTran.GetCashflow(cfDate);
                dynTran.PayInterest(cfWritedown, rateProvider, null, allTrans);
            }
        }

        private double CalculateTrancheInterest(List<InterestPayment> payList, DateTime cfDate,
            IRateProvider rateProvider, IEnumerable<DynamicTranche> dynTrans,
            double availableInterest, IList<DynamicTranche> allTrans, double serviceFee)
        {
            double intPaid = 0;

            foreach (var dynTranPrioGroup in dynTrans.GroupBy(dc => dc.Tranche.InterestPriority).OrderBy(dc => dc.Key))
            {
                double shortfallAmt = 1;
                var totalExpectedInterest = dynTranPrioGroup.Sum(dc =>
                    dc.Interest(dc.GetCashflow(cfDate), rateProvider, allTrans) +
                    dc.GetCashflow(cfDate).AccumInterestShortfall);
                if (totalExpectedInterest - availableInterest > 1)
                    shortfallAmt = availableInterest / totalExpectedInterest;

                foreach (var dynTran in dynTranPrioGroup)
                {
                    var cfWritedown = dynTran.GetCashflow(cfDate);
                    if (availableInterest < .01)
                    {
                        payList.Add(new InterestPayment(dynTran, cfWritedown, rateProvider, null, allTrans, 0));
                        continue;
                    }

                    double interest;
                    if (dynTran.Tranche.CouponTypeEnum == CouponType.ResidualInterest)
                    {
                        interest = availableInterest;
                    }
                    else if (dynTran.DealStructure.PayFromEnum == PayFromEnum.ExcessServicing)
                    {
                        // excess servicing should not reduce interest to remaining classes 
                        var servicingFee = dynTran.Interest(cfWritedown, rateProvider, allTrans);
                        var serviceFeeAdj = Math.Min(servicingFee, serviceFee);
                        payList.Add(new InterestPayment(dynTran, cfWritedown, rateProvider, null, allTrans,
                            serviceFeeAdj));
                        continue;
                    }
                    else
                    {
                        interest = (dynTran.Interest(cfWritedown, rateProvider, allTrans) +
                                    cfWritedown.AccumInterestShortfall) * shortfallAmt;
                    }

                    availableInterest -= interest;
                    payList.Add(new InterestPayment(dynTran, cfWritedown, rateProvider, null, allTrans, interest));
                    intPaid += interest;
                }
            }

            return intPaid;
        }

        private double CalculateGroupInterest(List<InterestPayment> payList, DynamicGroup dynGroup,
            IRateProvider rateProvider, DateTime cfDate, PeriodCashflows periodCf, double availableInterest)
        {
            if (availableInterest <= 0)
                return 0;

            var beginInterest = availableInterest;
            double intPaid = 0;

            var allOfferedTrans = dynGroup.DynamicClasses.SelectMany(dc => dc.DynamicTranches).Where(dc =>
                    (dc.DealStructure.GroupNum == dynGroup.GroupNum &&
                     dc.Tranche.TrancheTypeEnum == TrancheTypeEnum.Offered) ||
                    dc.Tranche.TrancheTypeEnum == TrancheTypeEnum.Reference ||
                    dc.Tranche.TrancheTypeEnum == TrancheTypeEnum.CapFundsReserve ||
                    dc.Tranche.TrancheTypeEnum == TrancheTypeEnum.OfferedCapFundsReserve)
                .OrderBy(dc => dc.DealStructure.SubordinationOrder).ToList();

            var allTrans = dynGroup.DynamicClasses.SelectMany(dc => dc.DynamicTranches)
                .Where(dc => dc.DealStructure.GroupNum == dynGroup.GroupNum)
                .OrderBy(dc => dc.DealStructure.SubordinationOrder).ToList();

            if (cfDate > dynGroup.FirstProjectionDate)
            {
                var prevDate = cfDate.AddMonths(-1);
                foreach (var tran in allTrans)
                {
                    var prevCashflow = tran.GetCashflow(prevDate);
                    var currCashflow = tran.GetCashflow(cfDate);
                    currCashflow.AccumInterestShortfall = prevCashflow.AccumInterestShortfall;
                }
            }

            /*Allocate offered tranches*/
            intPaid += CalculateTrancheInterest(payList, cfDate, rateProvider, allOfferedTrans, availableInterest,
                allTrans, periodCf.ServiceFee);
            availableInterest -= intPaid;

            if (intPaid > beginInterest + 1)
                Exceptions.InterestUnproperlyDistributedException(cfDate, availableInterest, intPaid);

            /*Allocate exchangable tranches*/
            var allExchTrans = dynGroup.DynamicClasses.SelectMany(dc => dc.DynamicTranches)
                .Where(dc => dc.Tranche.TrancheTypeEnum == TrancheTypeEnum.Exchanged).ToList();
            foreach (var exchTranche in allExchTrans)
            {
                if (exchTranche.DealStructure.ExchangableTranche == null)
                {
                    var interest = exchTranche.Interest(exchTranche.GetCashflow(cfDate), rateProvider, allTrans);
                    payList.Add(new InterestPayment(exchTranche, exchTranche.GetCashflow(cfDate), rateProvider, null,
                        allTrans, interest));
                    continue;
                }

                var parentClass = dynGroup.ClassesByNameOrTag(exchTranche.DealStructure.ExchangableTranche);
                var parentTranche = parentClass.SelectMany(dc => dc.DynamicTranches).ToList();
                var parentTotalShortfall = parentTranche.Sum(pc => pc.GetCashflow(cfDate).InterestShortfall);
                var parentTotalInterest = parentTranche.Sum(pc => pc.GetCashflow(cfDate).Interest);
                var shortfall = 1 - parentTotalShortfall / (parentTotalShortfall + parentTotalInterest);
                if (double.IsNaN(shortfall) || double.IsInfinity(shortfall))
                    shortfall = 1;
                var exchCashflow = exchTranche.GetCashflow(cfDate);
                var expectedInterest = exchTranche.Interest(exchCashflow, rateProvider, allTrans);
                payList.Add(new InterestPayment(exchTranche, exchCashflow, rateProvider, null, allTrans,
                    expectedInterest * shortfall));
            }

            return availableInterest;
        }
    }
}

public class InterestPayment
{
    public InterestPayment(DynamicTranche dynTran, TrancheCashflow trancheCashflow, IRateProvider rateProvider,
        IAssumptionMill assumps, IEnumerable<DynamicTranche> allTranches, double interest)
    {
        DynamicTranche = dynTran;
        TrancheCashflow = trancheCashflow;
        RateProvider = rateProvider;
        Assumps = assumps;
        AllTranches = allTranches;
        Interest = interest;
    }

    public DynamicTranche DynamicTranche { get; }
    public TrancheCashflow TrancheCashflow { get; }
    public IRateProvider RateProvider { get; }
    public IAssumptionMill Assumps { get; }
    public IEnumerable<DynamicTranche> AllTranches { get; }
    public double Interest { get; }

    public override string ToString()
    {
        return $"{DynamicTranche.Tranche.TrancheName} = {Interest:#,###}";
    }
}

/* Possible issues
 * 1) when allocating exchange tranches we're not taking into account the exchange shares
 */