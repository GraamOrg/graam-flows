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

        // Get execution order from deal or use default (handle both null and empty list)
        var executionOrder = (deal.ExecutionOrder == null || !deal.ExecutionOrder.Any())
            ? GetDefaultExecutionOrder()
            : deal.ExecutionOrder.ToList();

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
                    var collatBal = periodCf.BeginBalance + periodCf.AccumForbearance + periodCf.ForbearanceLiquidated;
                    dynGroup = new DynamicGroup(dynDeal.DynamicGroups.LastOrDefault(), formulaExecutor,
                        firstProjectionDate, deal, periodCf.GroupNum, collatBal);
                    dynDeal.AddGroup(dynGroup);
                    var triggerList = deal.DealTriggers.LoadTriggers(deal, assumps, dynGroup.GroupNum,
                        periodCashflows.Where(p => p.GroupNum == periodCf.GroupNum));
                    var trancheBal = dynGroup.Balance();
                    var ratio = trancheBal / collatBal;
                    dynGroup.CollateralBondRatio = ratio;
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
                RunComposablePeriod(deal, rateProvider, dynGroup, adjPeriodCf, triggerValues, formulaExecutor, payRuleExecutor, executionOrder);

                dynGroup.Advance(adjPeriodCf.CashflowDate);
                periodCf.EffectiveWac = adjPeriodCf.EffectiveWac;
                triggerValueList.AddRange(triggerValues);
                periodCfList.Add(adjPeriodCf);
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
            "EXCESS_TURBO",
            "EXCESS_RELEASE"
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
        var availableInterest = cfAlloc.Interest;
        var availableSchedPrin = cfAlloc.SchedPrin;
        var availablePrepayPrin = cfAlloc.PrepayPrin;
        var availableRecovPrin = cfAlloc.RecovPrin;
        var allTranches = dynGroup.DynamicClasses.SelectMany(dc => dc.DynamicTranches).ToList();

        // Set collateral balance variables for use in steps (e.g., OC turbo calculation)
        dynGroup.SetVariable("collat_balance", adjPeriodCf.Balance);
        dynGroup.SetVariable("collat_begin_balance", adjPeriodCf.BeginBalance);

        // Update Certificate tranche balance to reflect current OC (Pool - Notes)
        dynGroup.UpdateCertificateBalance(adjPeriodCf.Balance, adjPeriodCf.CashflowDate);

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
                    availableSchedPrin = PayScheduledPrincipalStep(deal, dynGroup, adjPeriodCf, cfAlloc,
                        triggerValues, payRuleExecutor, availableSchedPrin);
                    break;

                case "PRINCIPAL_UNSCHEDULED":
                    availablePrepayPrin = PayUnscheduledPrincipalStep(deal, dynGroup, adjPeriodCf, cfAlloc,
                        triggerValues, payRuleExecutor, availablePrepayPrin);
                    break;

                case "PRINCIPAL_RECOVERY":
                    availableRecovPrin = PayRecoveryPrincipalStep(deal, dynGroup, adjPeriodCf, cfAlloc,
                        triggerValues, payRuleExecutor, availableRecovPrin);
                    break;

                case "WRITEDOWN":
                    PayWritedownStep(dynGroup, adjPeriodCf, cfAlloc.Writedown);
                    break;

                case "EXCESS_TURBO":
                    availableInterest = PayExcessTurboStep(deal, dynGroup, adjPeriodCf, availableInterest);
                    break;

                case "EXCESS_RELEASE":
                    PayExcessReleaseStep(dynGroup, adjPeriodCf, availableInterest);
                    availableInterest = 0;
                    break;
            }
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
    ///     Returns the remaining unallocated scheduled principal.
    /// </summary>
    private double PayScheduledPrincipalStep(IDeal deal, DynamicGroup dynGroup, PeriodCashflows adjPeriodCf,
        CashflowAllocs cfAlloc, List<TriggerValue> triggerValues, IPayRuleExecutor payRuleExecutor,
        double availableSchedPrin)
    {
        if (dynGroup.ScheduledPayable == null || availableSchedPrin < 0.01)
            return availableSchedPrin;

        var noteBalanceBefore = dynGroup.NoteBalance();
        dynGroup.ScheduledPayable.PaySp(null, adjPeriodCf.CashflowDate, availableSchedPrin,
            () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
        var noteBalanceAfter = dynGroup.NoteBalance();

        var paidAmount = noteBalanceBefore - noteBalanceAfter;
        return availableSchedPrin - paidAmount;
    }

    /// <summary>
    ///     Pay unscheduled (prepay) principal via PrepayPayable.
    ///     Returns the remaining unallocated prepay principal.
    /// </summary>
    private double PayUnscheduledPrincipalStep(IDeal deal, DynamicGroup dynGroup, PeriodCashflows adjPeriodCf,
        CashflowAllocs cfAlloc, List<TriggerValue> triggerValues, IPayRuleExecutor payRuleExecutor,
        double availablePrepayPrin)
    {
        if (dynGroup.PrepayPayable == null || availablePrepayPrin < 0.01)
            return availablePrepayPrin;

        var noteBalanceBefore = dynGroup.NoteBalance();
        dynGroup.PrepayPayable.PayUsp(null, adjPeriodCf.CashflowDate, availablePrepayPrin,
            () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
        var noteBalanceAfter = dynGroup.NoteBalance();

        var paidAmount = noteBalanceBefore - noteBalanceAfter;
        return availablePrepayPrin - paidAmount;
    }

    /// <summary>
    ///     Pay recovery principal via RecoveryPayable.
    ///     Returns the remaining unallocated recovery principal.
    /// </summary>
    private double PayRecoveryPrincipalStep(IDeal deal, DynamicGroup dynGroup, PeriodCashflows adjPeriodCf,
        CashflowAllocs cfAlloc, List<TriggerValue> triggerValues, IPayRuleExecutor payRuleExecutor,
        double availableRecovPrin)
    {
        if (dynGroup.RecoveryPayable == null || availableRecovPrin < 0.01)
            return availableRecovPrin;

        var noteBalanceBefore = dynGroup.NoteBalance();
        dynGroup.RecoveryPayable.PayRp(null, adjPeriodCf.CashflowDate, availableRecovPrin,
            () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
        var noteBalanceAfter = dynGroup.NoteBalance();

        var paidAmount = noteBalanceBefore - noteBalanceAfter;
        return availableRecovPrin - paidAmount;
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
    ///     Pay Excess cashflows to Excess Structure
    /// Two Routes:
    /// Pay excess to notes up to OC shortfall amount (turbo paydown).
    /// Returns remaining available interest after turbo payment.
    /// </summary>
    private double PayExcessTurboStep(IDeal deal, DynamicGroup dynGroup, PeriodCashflows periodCf,
        double availableInterest)
    {
        if (dynGroup.TurboPayable == null || availableInterest <= 0)
            return availableInterest;

        // Read OC config directly from deal
        var ocConfig = deal.OcTargetConfig;
        if (ocConfig == null)
            return availableInterest; // No OC target configured

        // Get pool and note balances (NoteBalance excludes Certificate tranches)
        var poolBalance = dynGroup.GetVariable("collat_balance");
        var noteBalance = dynGroup.NoteBalance();
        var currentOc = poolBalance - noteBalance;

        // Calculate target OC = MAX(TargetPct * PoolBalance, FloorAmt)
        var targetOc = Math.Max(ocConfig.TargetPct * poolBalance, ocConfig.FloorAmt);

        // Calculate turbo amount
        var shortfall = Math.Max(0, targetOc - currentOc);
        var turboAmount = Math.Min(availableInterest, shortfall);

        if (turboAmount > 0)
        {
            // Pay down notes (positive principal reduces balance)
            dynGroup.TurboPayable.PaySp(null, periodCf.CashflowDate, turboAmount, () => { });
        }

        return availableInterest - turboAmount;
    }

    /// <summary>
    /// Release remaining excess to certificateholders.
    /// </summary>
    private void PayExcessReleaseStep(DynamicGroup dynGroup, PeriodCashflows periodCf,
        double availableInterest)
    {
        if (dynGroup.ReleasePayable == null || availableInterest <= 0)
            return;

        // Release to certificates
        dynGroup.ReleasePayable.PaySp(null, periodCf.CashflowDate, availableInterest, () => { });
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
