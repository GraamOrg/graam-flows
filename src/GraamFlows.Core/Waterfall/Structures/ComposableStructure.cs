using GraamFlows.Objects.DataObjects;
using GraamFlows.RulesEngine;
using GraamFlows.Triggers;
using GraamFlows.Util;
using GraamFlows.Waterfall.MarketTranche;
using GraamFlows.Waterfall.Structures.PayableStructures;

namespace GraamFlows.Waterfall.Structures;

/// <summary>
///     Composable waterfall structure with step-based execution driven by ExecutionOrder.
///     Unlike UnifiedStructure, this structure:
///     - Uses ExecutionOrder from deal JSON to determine step sequence
///     - Pays interest via IPayable.PayInterest (no TrancheAllocator)
///     - Tracks available funds through the waterfall
///     Step types:
///     - EXPENSE: Pay deal expenses from available interest
///     - INTEREST: Interest distribution via InterestPayable
///     - PRINCIPAL_SCHEDULED: Scheduled principal via ScheduledPayable
///     - PRINCIPAL_UNSCHEDULED: Prepay principal via PrepayPayable
///     - PRINCIPAL_RECOVERY: Recovery principal via RecoveryPayable
///     - RESERVE: Reserve principal via ReservePayable
///     - WRITEDOWN: Loss allocation via WritedownPayable
///     - EXCESS: Excess cashflow via ExcessPayable
/// </summary>
public class ComposableStructure : BaseStructure
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

        // Get execution order from deal or use default
        var executionOrder = deal.ExecutionOrder?.ToList() ?? GetDefaultExecutionOrder();

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

                // Execute pay rules - this sets up the payable structures
                ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf);

                // Validate required payables are set
                ValidateRequiredPayables(deal, dynGroup);

                // Run composable waterfall period with step-based execution
                RunComposablePeriod(deal, rateProvider, dynGroup, adjPeriodCf, triggerValues, formulaExecutor,
                    payRuleExecutor, executionOrder);

                dynGroup.Advance(adjPeriodCf.CashflowDate);
                periodCf.EffectiveWac = adjPeriodCf.EffectiveWac;
                triggerValueList.AddRange(triggerValues);
                periodCfList.Add(adjPeriodCf);

                // Verify collateral/tranches are paying down the same
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

            // Note: No exchangeables or TrancheAllocator in ComposableStructure
            // Interest is paid via InterestPayable.PayInterest

            triggerValueList.Clear();
            periodCfList.Clear();
        }

        var dealCashflows = dynDeal.DynamicGroups.CreateDealCashflows(cashflows, assumps);
        return dealCashflows;
    }

    /// <summary>
    ///     Gets the default execution order when none is specified.
    /// </summary>
    private static List<string> GetDefaultExecutionOrder()
    {
        return new List<string>
        {
            "EXPENSE",
            "INTEREST",
            "PRINCIPAL_SCHEDULED",
            "PRINCIPAL_UNSCHEDULED",
            "PRINCIPAL_RECOVERY",
            "RESERVE",
            "WRITEDOWN",
            "EXCESS"
        };
    }

    /// <summary>
    ///     Validates that all required payable structures are set.
    /// </summary>
    private void ValidateRequiredPayables(IDeal deal, DynamicGroup dynGroup)
    {
        if (dynGroup.InterestPayable == null)
            throw new DealModelingException(deal.DealName,
                "ComposableStructure requires INTEREST step in waterfall. Add SET_INTEREST_STRUCT rule.");

        if (dynGroup.ScheduledPayable == null)
            throw new DealModelingException(deal.DealName,
                "ComposableStructure requires PRINCIPAL (scheduled) step in waterfall. Add SET_SCHED_STRUCT rule.");

        if (dynGroup.WritedownPayable == null)
            throw new DealModelingException(deal.DealName,
                "ComposableStructure requires WRITEDOWN step in waterfall. Add SET_WRITEDOWN_STRUCT rule.");
    }

    /// <summary>
    ///     Runs the composable waterfall for a single period using step-based execution.
    ///     Payment order follows the ExecutionOrder from the deal model.
    /// </summary>
    private void RunComposablePeriod(IDeal deal, IRateProvider rateProvider, DynamicGroup dynGroup,
        PeriodCashflows adjPeriodCf, List<TriggerValue> triggerValues, IFormulaExecutor formulaExecutor,
        IPayRuleExecutor payRuleExecutor, List<string> executionOrder)
    {
        if (dynGroup.Balance() < .001)
            return;

        var cfAlloc = BeginPeriod(deal, dynGroup, adjPeriodCf);

        // Track available funds through the waterfall
        var availableInterest = adjPeriodCf.NetInterest;
        var allTranches = dynGroup.DynamicClasses.SelectMany(dc => dc.DynamicTranches).ToList();

        // Execute steps in order
        foreach (var step in executionOrder)
        {
            switch (step.ToUpperInvariant())
            {
                case "EXPENSE":
                    availableInterest = PayExpensesStep(formulaExecutor, dynGroup, adjPeriodCf, triggerValues,
                        availableInterest);
                    break;

                case "INTEREST":
                    availableInterest = PayInterestStep(dynGroup, rateProvider, adjPeriodCf, availableInterest,
                        allTranches);
                    break;

                case "PRINCIPAL_SCHEDULED":
                    PayScheduledPrincipalStep(deal, dynGroup, adjPeriodCf, cfAlloc, triggerValues, payRuleExecutor);
                    break;

                case "PRINCIPAL_UNSCHEDULED":
                    PayUnscheduledPrincipalStep(deal, dynGroup, adjPeriodCf, cfAlloc, triggerValues, payRuleExecutor);
                    break;

                case "PRINCIPAL_RECOVERY":
                    PayRecoveryPrincipalStep(deal, dynGroup, adjPeriodCf, cfAlloc, triggerValues, payRuleExecutor);
                    break;

                case "RESERVE":
                    PayReserveStep(deal, dynGroup, adjPeriodCf, cfAlloc, triggerValues, payRuleExecutor);
                    break;

                case "WRITEDOWN":
                    PayWritedownStep(dynGroup, adjPeriodCf, cfAlloc.Writedown);
                    break;

                case "EXCESS":
                    PayExcessStep(dynGroup, adjPeriodCf, availableInterest);
                    break;
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
    ///     Pay expenses from available interest, returning remaining funds.
    /// </summary>
    private double PayExpensesStep(IFormulaExecutor formulaExecutor, DynamicGroup dynGroup,
        PeriodCashflows periodCf, List<TriggerValue> triggerValues, double availableInterest)
    {
        var netInterest = availableInterest;

        var expenses = dynGroup.ExpenseClasses.SelectMany(dc => dc.DynamicTranches).OrderBy(e => e.Tranche.TrancheName)
            .Sum(ec =>
            {
                var functionName = RulesBuilder.GetTrancheCpnFormulaName(ec.Tranche);
                formulaExecutor.Reset(null, triggerValues, dynGroup, periodCf, Enumerable.Repeat(ec, 1));
                var expense = formulaExecutor.EvaluateDouble(functionName);
                if (expense > netInterest)
                {
                    var shortfall = netInterest - expense;
                    expense = netInterest;
                    ec.PayExpense(periodCf.CashflowDate, netInterest, shortfall);
                    netInterest = 0;
                }
                else
                {
                    ec.PayExpense(periodCf.CashflowDate, expense, 0);
                    netInterest -= expense;
                }

                return expense;
            });

        // Compute effective WAC after expenses
        var wac = 1200 * (periodCf.Interest + periodCf.UnAdvancedInterest - periodCf.ServiceFee - expenses) /
                  periodCf.BeginBalance;
        periodCf.Expenses = expenses;
        periodCf.EffectiveWac = wac;

        return netInterest;
    }

    /// <summary>
    ///     Pay interest via InterestPayable, returning remaining funds.
    /// </summary>
    private double PayInterestStep(DynamicGroup dynGroup, IRateProvider rateProvider,
        PeriodCashflows periodCf, double availableInterest, List<DynamicTranche> allTranches)
    {
        if (dynGroup.InterestPayable == null)
            return availableInterest;

        var interestPaid = dynGroup.InterestPayable.PayInterest(null, periodCf.CashflowDate,
            availableInterest, rateProvider, allTranches);

        return availableInterest - interestPaid;
    }

    /// <summary>
    ///     Pay scheduled principal via ScheduledPayable.
    /// </summary>
    private void PayScheduledPrincipalStep(IDeal deal, DynamicGroup dynGroup, PeriodCashflows adjPeriodCf,
        CashflowAllocs cfAlloc, List<TriggerValue> triggerValues, IPayRuleExecutor payRuleExecutor)
    {
        if (dynGroup.ScheduledPayable == null || cfAlloc.SchedPrin < 0.01)
            return;

        dynGroup.ScheduledPayable.PaySp(null, adjPeriodCf.CashflowDate, cfAlloc.SchedPrin,
            () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
    }

    /// <summary>
    ///     Pay unscheduled (prepay) principal via PrepayPayable.
    /// </summary>
    private void PayUnscheduledPrincipalStep(IDeal deal, DynamicGroup dynGroup, PeriodCashflows adjPeriodCf,
        CashflowAllocs cfAlloc, List<TriggerValue> triggerValues, IPayRuleExecutor payRuleExecutor)
    {
        if (dynGroup.PrepayPayable == null || cfAlloc.PrepayPrin < 0.01)
            return;

        dynGroup.PrepayPayable.PayUsp(null, adjPeriodCf.CashflowDate, cfAlloc.PrepayPrin,
            () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
    }

    /// <summary>
    ///     Pay recovery principal via RecoveryPayable.
    /// </summary>
    private void PayRecoveryPrincipalStep(IDeal deal, DynamicGroup dynGroup, PeriodCashflows adjPeriodCf,
        CashflowAllocs cfAlloc, List<TriggerValue> triggerValues, IPayRuleExecutor payRuleExecutor)
    {
        if (dynGroup.RecoveryPayable == null || cfAlloc.RecovPrin < 0.01)
            return;

        dynGroup.RecoveryPayable.PayRp(null, adjPeriodCf.CashflowDate, cfAlloc.RecovPrin,
            () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
    }

    /// <summary>
    ///     Pay reserve principal via ReservePayable.
    /// </summary>
    private void PayReserveStep(IDeal deal, DynamicGroup dynGroup, PeriodCashflows adjPeriodCf,
        CashflowAllocs cfAlloc, List<TriggerValue> triggerValues, IPayRuleExecutor payRuleExecutor)
    {
        if (dynGroup.ReservePayable == null || cfAlloc.ReservePrin < 0.01)
            return;

        dynGroup.ReservePayable.PayRp(null, adjPeriodCf.CashflowDate, cfAlloc.ReservePrin,
            () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
    }

    /// <summary>
    ///     Pay writedowns via WritedownPayable.
    /// </summary>
    private void PayWritedownStep(DynamicGroup dynGroup, PeriodCashflows periodCf, double writedownAmt)
    {
        if (writedownAmt <= 0 || dynGroup.WritedownPayable == null)
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
    ///     Pay excess cashflow via ExcessPayable (accretes to OC/residual tranches).
    /// </summary>
    private void PayExcessStep(DynamicGroup dynGroup, PeriodCashflows periodCf, double remainingInterest)
    {
        if (dynGroup.ExcessPayable == null || remainingInterest <= 0)
            return;

        // Excess spread accretes (increases balance) to OC/residual tranches
        // Use negative principal to INCREASE balance
        AccreteExcess(dynGroup.ExcessPayable, periodCf.CashflowDate, remainingInterest);
    }

    /// <summary>
    ///     Accretes excess spread to the target payable structure.
    ///     This INCREASES the balance by using negative principal payments.
    /// </summary>
    private void AccreteExcess(IPayable excessPayable, DateTime cfDate, double excessAmount)
    {
        var leaves = excessPayable.Leafs();
        foreach (var leaf in leaves)
        {
            if (leaf is DynamicClass dynClass)
            {
                // Use negative scheduled principal to INCREASE balance
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
