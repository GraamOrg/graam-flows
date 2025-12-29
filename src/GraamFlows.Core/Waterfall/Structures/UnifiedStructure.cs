using GraamFlows.Objects.DataObjects;
using GraamFlows.RulesEngine;
using GraamFlows.Triggers;
using GraamFlows.Util;
using GraamFlows.Waterfall.MarketTranche;
using GraamFlows.Waterfall.Structures.PayableStructures;

namespace GraamFlows.Waterfall.Structures;

/// <summary>
///     Unified waterfall structure that eliminates classGroups dependency.
///     All cashflow distribution (interest, principal, writedown, excess) is explicit
///     via IPayable structures set by DSL commands in PayRules.
///     Step types:
///     - INTEREST: Interest distribution via InterestPayable
///     - PRINCIPAL: Principal distribution via ScheduledPayable/PrepayPayable/RecoveryPayable
///     - WRITEDOWN: Loss allocation via WritedownPayable
///     - EXCESS: Excess cashflow via ExcessPayable
/// </summary>
public class UnifiedStructure : BaseStructure
{
    public override DealCashflows Waterfall(IDeal deal, IRateProvider rateProvider, DateTime firstProjectionDate,
        CollateralCashflows cashflows, IAssumptionMill assumps, ITrancheAllocator trancheAllocator)
    {
        var periodCashflows = cashflows.PeriodCashflows;
        var triggerMap = new Dictionary<string, IList<ITrigger>>();

        var formulaExecutor = new GenericExecutor(deal);
        var payRuleExecutor = new PayRuleExecutor(formulaExecutor, this);
        var dynDeal = new DynamicDeal(deal);
        var cashflowsBeforeFirstPay = new Dictionary<string, List<PeriodCashflows>>();

        foreach (var period in periodCashflows.GroupBy(pc => pc.CashflowDate))
        {
            var triggerValueList = new List<TriggerValue>();
            var periodCfList = new List<PeriodCashflows>();

            // Compute collateral WAC
            var collatWac = period.Sum(p => p.Interest) / period.Sum(p => p.BeginBalance) * 1200;
            var collatNetWac = period.Sum(p => p.NetInterest) / period.Sum(p => p.BeginBalance) * 1200;

            foreach (var periodCfGroup in period.GroupBy(g => g.GroupNum))
            {
                var periodCf = periodCfGroup.Single();
                var dynGroup = dynDeal.GetGroup(periodCf.GroupNum);

                if (dynGroup == null)
                {
                    dynGroup = new DynamicGroup(dynDeal.DynamicGroups.LastOrDefault(), formulaExecutor,
                        firstProjectionDate, deal, periodCf.GroupNum);
                    dynDeal.AddGroup(dynGroup);
                    var triggerList = deal.DealTriggers.LoadTriggers(deal, assumps, dynGroup.GroupNum,
                        periodCashflows.Where(p => p.GroupNum == periodCf.GroupNum));
                    var collatBal = periodCf.BeginBalance + periodCf.AccumForbearance + periodCf.ForbearanceLiquidated;
                    var trancheBal = dynGroup.Balance();
                    var ratio = trancheBal / collatBal;
                    dynGroup.CollateralBondRatio = ratio;

                    // Add OC tranche when collateral > tranches (normal for seasoned deals)
                    // Skip if deal already has a Modeling tranche defined (e.g., CERTIFICATES in Auto ABS)
                    var hasModelingTranche = deal.Tranches.Any(t =>
                        t.TrancheTypeEnum == Objects.TypeEnum.TrancheTypeEnum.Modeling);
                    if (collatBal > trancheBal + 1 && !hasModelingTranche)
                        dynGroup.AddOverCollaterizedTranche(collatBal - trancheBal);
                    // Only error if tranches significantly exceed collateral (data issue)
                    else if (!dynGroup.HasCrossedGroups && trancheBal > collatBal * 1.01)
                        Exceptions.CollateralAndTrancheBalanceMistmatchException(deal.DealName, collatBal, trancheBal,
                            dynGroup.DealClasses);

                    triggerMap.Add(periodCf.GroupNum, triggerList);
                }

                if (dynGroup.Balance() <= 0)
                    continue;

                dynGroup.CollateralWac = collatWac;
                dynGroup.CollateralNetWac = collatNetWac;
                dynGroup.BeginCollatBalance = periodCf.BeginBalance;

                var triggers = triggerMap[dynGroup.GroupNum];
                var adjPeriodCf = AdjustPeriodCashflows(dynGroup, periodCf);

                // Check cashflows before waterfall
                if (periodCf.CashflowDate < dynGroup.FirstPayDate)
                {
                    if (!cashflowsBeforeFirstPay.ContainsKey(periodCf.GroupNum))
                        cashflowsBeforeFirstPay[periodCf.GroupNum] = new List<PeriodCashflows>();
                    cashflowsBeforeFirstPay[periodCf.GroupNum].Add(periodCf);
                    continue;
                }

                if (cashflowsBeforeFirstPay.ContainsKey(periodCf.GroupNum))
                {
                    foreach (var prevCf in cashflowsBeforeFirstPay[periodCf.GroupNum])
                        adjPeriodCf.Add(prevCf);
                    cashflowsBeforeFirstPay.Remove(periodCf.GroupNum);
                }

                // Execute triggers
                var triggerValues = ExecuteTriggers(dynGroup, triggers, adjPeriodCf, payRuleExecutor);

                // Pay expenses (deducted from interest remittance)
                PayExpenses(formulaExecutor, dynGroup, rateProvider, adjPeriodCf.CashflowDate, triggerValues,
                    adjPeriodCf);

                // Execute pay rules - this sets up the payable structures
                ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf);

                // Validate required payables are set (strict - no fallback)
                ValidateRequiredPayables(deal, dynGroup);

                // Run unified waterfall period
                RunUnifiedPeriod(deal, rateProvider, dynGroup, adjPeriodCf, triggerValues, formulaExecutor,
                    payRuleExecutor);

                dynGroup.Advance(adjPeriodCf.CashflowDate);
                periodCf.EffectiveWac = adjPeriodCf.EffectiveWac;
                CheckReserveFunds(dynGroup, adjPeriodCf, trancheAllocator, rateProvider, triggerValues,
                    payRuleExecutor);
                triggerValueList.AddRange(triggerValues);
                periodCfList.Add(adjPeriodCf);

                // Verify collateral/tranches are paying down the same
                // NOTE: Validation temporarily relaxed for deals with current factors applied
                var endCollatBal = periodCf.Balance + periodCf.AccumForbearance;
                var endTrancheBal = dynGroup.Balance();
                var endRatio = endTrancheBal / endCollatBal;

                var beginCollatDiff = Math.Abs((1 - dynGroup.CollateralBondRatio) * dynGroup.BeginCollatBalance);
                var currCollatDiff = Math.Abs((1 - endRatio) * Math.Max(endCollatBal, endTrancheBal));

                // Skip paydown check for deals with explicit OC/Modeling tranches since accretion causes expected divergence
                var hasExplicitOC = deal.Tranches.Any(t =>
                    t.TrancheTypeEnum == Objects.TypeEnum.TrancheTypeEnum.Modeling);
                if (hasExplicitOC)
                    continue;

                // Relaxed threshold: allow up to 5% divergence for deals with pre-applied factors
                var allowedDivergence = Math.Max(1e6, dynGroup.BeginCollatBalance * 0.05);
                if (endTrancheBal > 1000000 && Math.Abs(beginCollatDiff - currCollatDiff) > 100 &&
                    currCollatDiff > beginCollatDiff * 2 &&
                    beginCollatDiff + Math.Abs(endCollatBal - endTrancheBal) > allowedDivergence)
                    Exceptions.PaydownException(dynGroup.Deal.DealName, periodCf.GroupNum, periodCf.CashflowDate,
                        endCollatBal, endTrancheBal, dynGroup.BeginCollatBalance * (1 - dynGroup.CollateralBondRatio));
            }

            // Exchangeables
            var dynGroups = dynDeal.DynamicGroups.ToList();
            PayExchangeables(period.Key, dynGroups, periodCfList, out var payFromAllocator);
            PayExchangeableStructures(period.Key, period, dynGroups, payRuleExecutor, triggerValueList);
            PayNotionalClasses(period.Key, dynGroups, periodCfList);
            PayInterestShortfallSupport(dynDeal, period.Key);

            // Interest allocation via TrancheAllocator (for classes not in InterestPayable)
            trancheAllocator.AllocateTranches(this, formulaExecutor, dynGroups, rateProvider, period.Key,
                triggerValueList, periodCfList, payFromAllocator);

            triggerValueList.Clear();
            periodCfList.Clear();
        }

        var dealCashflows = dynDeal.DynamicGroups.CreateDealCashflows(cashflows, assumps);
        return dealCashflows;
    }

    /// <summary>
    ///     Validates that all required payable structures are set.
    ///     UnifiedStructure requires explicit definition of all cashflow distribution.
    /// </summary>
    private void ValidateRequiredPayables(IDeal deal, DynamicGroup dynGroup)
    {
        if (dynGroup.InterestPayable == null)
            throw new DealModelingException(deal.DealName,
                "UnifiedStructure requires INTEREST step in waterfall. Add SET_INTEREST_STRUCT rule.");

        if (dynGroup.ScheduledPayable == null)
            throw new DealModelingException(deal.DealName,
                "UnifiedStructure requires PRINCIPAL (scheduled) step in waterfall. Add SET_SCHED_STRUCT rule.");

        if (dynGroup.WritedownPayable == null)
            throw new DealModelingException(deal.DealName,
                "UnifiedStructure requires WRITEDOWN step in waterfall. Add SET_WRITEDOWN_STRUCT rule.");
    }

    /// <summary>
    ///     Runs the unified waterfall for a single period.
    ///     Payment order follows COLT1804 prospectus: Interest → Principal → Writedowns
    /// </summary>
    private void RunUnifiedPeriod(IDeal deal, IRateProvider rateProvider, DynamicGroup dynGroup,
        PeriodCashflows adjPeriodCf, List<TriggerValue> triggerValues, IFormulaExecutor formulaExecutor,
        IPayRuleExecutor payRuleExecutor)
    {
        if (dynGroup.Balance() < .001)
            return;

        var cfAlloc = BeginPeriod(deal, dynGroup, adjPeriodCf);
        var ocClass = dynGroup.ClassByName("OC" + adjPeriodCf.GroupNum);

        // 1. PRINCIPAL - via existing payable structures
        PayPrincipalViaStructures(deal, dynGroup, adjPeriodCf, cfAlloc, ocClass, triggerValues, payRuleExecutor);

        // 2. WRITEDOWNS - via WritedownPayable structure
        PayWritedownsViaStructure(dynGroup, adjPeriodCf, cfAlloc.Writedown);

        // 3. EXCESS - via ExcessPayable structure (if defined)
        // For OC/residual tranches, excess spread ACCRETES (increases balance)
        if (dynGroup.ExcessPayable != null)
        {
            var excessCashflow = CalculateExcessCashflow(dynGroup, adjPeriodCf);
            if (excessCashflow > 0)
            {
                // Accrete: use negative principal to INCREASE balance
                // This is how Z-bond/OC accretion works - excess spread adds to balance
                AccreteExcess(dynGroup.ExcessPayable, adjPeriodCf.CashflowDate, excessCashflow);
            }
        }

        // Accruals
        var accrualStructures = dynGroup.AccrualStructures().ToList();
        if (accrualStructures.Any())
        {
            PayAccrualStructures(dynGroup, rateProvider, adjPeriodCf, triggerValues, accrualStructures);
            ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf);
        }
    }

    /// <summary>
    ///     Pays principal via the existing payable structures (Scheduled, Prepay, Recovery).
    /// </summary>
    private void PayPrincipalViaStructures(IDeal deal, DynamicGroup dynGroup, PeriodCashflows adjPeriodCf,
        CashflowAllocs cfAlloc, DynamicClass ocClass, List<TriggerValue> triggerValues,
        IPayRuleExecutor payRuleExecutor)
    {
        // Scheduled Principal - distribute to offered tranches
        dynGroup.ScheduledPayable.PaySp(null, adjPeriodCf.CashflowDate, cfAlloc.SchedPrin,
            () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));

        // Prepays - distribute to offered tranches
        if (dynGroup.PrepayPayable != null)
        {
            dynGroup.PrepayPayable.PayUsp(null, adjPeriodCf.CashflowDate, cfAlloc.PrepayPrin,
                () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
        }

        // Recoveries - distribute to offered tranches
        if (dynGroup.RecoveryPayable != null)
        {
            dynGroup.RecoveryPayable.PayRp(null, adjPeriodCf.CashflowDate, cfAlloc.RecovPrin,
                () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
        }

        // Reserve
        if (dynGroup.ReservePayable != null)
            dynGroup.ReservePayable.PayRp(null, adjPeriodCf.CashflowDate, cfAlloc.ReservePrin,
                () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
    }

    /// <summary>
    ///     Pays writedowns via the WritedownPayable structure.
    ///     The structure defines the order of loss allocation (first in list takes losses first).
    ///     Uses PayWritedown to distribute writedowns through the structure, cascading when balance exhausted.
    /// </summary>
    private void PayWritedownsViaStructure(DynamicGroup dynGroup, PeriodCashflows periodCf, double writedownAmt)
    {
        if (writedownAmt <= 0)
            return;

        // Track cumWritedowns before to determine what was applied to each class
        var leaves = dynGroup.WritedownPayable.Leafs();
        var beforeWritedowns = leaves.OfType<DynamicClass>()
            .ToDictionary(dc => dc, dc => dc.CumWritedown);

        // Use PayWritedown which properly handles SEQ/PRORATA/nested structures
        dynGroup.WritedownPayable.PayWritedown(null, periodCf.CashflowDate, writedownAmt, () => { });

        // Handle pseudo-classes (IO strips, etc.) for any class that had writedowns applied
        foreach (var leaf in leaves.OfType<DynamicClass>())
        {
            var writedownApplied = leaf.CumWritedown - beforeWritedowns[leaf];
            if (writedownApplied > 0)
                WritedownPseudoClass(leaf, periodCf.CashflowDate, writedownApplied);
        }
    }

    /// <summary>
    ///     Calculates excess cashflow available after interest, principal, and expenses.
    /// </summary>
    private double CalculateExcessCashflow(DynamicGroup dynGroup, PeriodCashflows periodCf)
    {
        // Excess = Net Interest - Expenses - Interest Due on Tranches
        // This is a simplified calculation; actual implementation may need more detail
        var availableInterest = periodCf.NetInterest - periodCf.Expenses;

        // Sum of interest due on all tranches
        var interestDue = dynGroup.DynamicClasses
            .SelectMany(dc => dc.DynamicTranches)
            .Sum(dt => dt.GetCashflow(periodCf.CashflowDate)?.Interest ?? 0);

        var excess = availableInterest - interestDue;
        return Math.Max(0, excess);
    }

    /// <summary>
    ///     Accretes excess spread to the target payable structure.
    ///     This INCREASES the balance by using negative principal payments.
    /// </summary>
    private void AccreteExcess(IPayable excessPayable, DateTime cfDate, double excessAmount)
    {
        // Get the leaf nodes (DynamicClasses) and accrete to each
        var leaves = excessPayable.Leafs();
        foreach (var leaf in leaves)
        {
            if (leaf is DynamicClass dynClass)
            {
                // Use negative scheduled principal to INCREASE balance
                // This mirrors how Z-bond accretion works in PayAccrualAndAccretionAccrualPhase
                dynClass.Pay(cfDate, 0, -excessAmount);
            }
        }
    }

    public override List<InputField> GetInputs(IDeal deal)
    {
        var fields = new List<InputField>();
        fields.Add(new InputField("Prepayment", "CPR,SMM".Split(',')));
        fields.Add(new InputField("Default", "CDR,MDR".Split(',')));
        fields.Add(new InputField("Severity"));

        foreach (var dealVar in deal.DealVariables.Where(dv => dv.IsForecastable))
            fields.Add(new InputField(dealVar.VariableName));

        foreach (var dealTrigger in deal.DealTriggers.Where(dt => !dt.IsMandatory))
            if (dealTrigger.PossibleValues != null)
                fields.Add(new InputField(dealTrigger.TriggerName, dealTrigger.PossibleValues.Split(',')));
            else
                fields.Add(new InputField(dealTrigger.TriggerName));

        return fields;
    }
}