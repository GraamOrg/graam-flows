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

        // Start reserve period tracking at beginning of waterfall (before any draws)
        dynGroup.FundsAccount?.StartPeriod();

        // Note: UpdateCertificateBalance is called AFTER principal payments to ensure
        // both pool and note balances are at end-of-period values for correct OC calculation.

        // Calculate OC release BEFORE principal allocation
        // This reduces principal available to notes so OC can be released to CERTIFICATE
        var ocRelease = CalculateOcRelease(deal, dynGroup, adjPeriodCf,
            availableSchedPrin + availablePrepayPrin + availableRecovPrin);
        if (ocRelease > 0)
        {
            // Reduce principal available to notes proportionally
            var totalPrin = availableSchedPrin + availablePrepayPrin + availableRecovPrin;
            if (totalPrin > 0)
            {
                var reduction = Math.Min(ocRelease, totalPrin);
                var ratio = (totalPrin - reduction) / totalPrin;
                availableSchedPrin *= ratio;
                availablePrepayPrin *= ratio;
                availableRecovPrin *= ratio;

                // Store OC release for EXCESS_TURBO step to pay to CERTIFICATE
                dynGroup.SetVariable("oc_release_amount", reduction);
            }
        }
        else
        {
            dynGroup.SetVariable("oc_release_amount", 0.0);
        }

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

                case "RESERVE_DEPOSIT":
                    availableInterest = PayReserveDepositStep(dynGroup, adjPeriodCf, availableInterest);
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

        // Update Certificate tranche balance to reflect current OC (Pool - Notes)
        // Called AFTER all principal payments so both pool and note balances are at end-of-period values
        dynGroup.UpdateCertificateBalance(adjPeriodCf.Balance, adjPeriodCf.CashflowDate);
    }

    /// <summary>
    /// Draw from reserve account to cover a shortfall.
    /// Returns the amount actually drawn (may be less than shortfall if reserve insufficient).
    /// </summary>
    private double DrawFromReserve(DynamicGroup dynGroup, double shortfall)
    {
        if (shortfall <= 0) return 0;
        var reserve = dynGroup.FundsAccount;
        if (reserve == null) return 0;
        return reserve.Debit(shortfall);
    }

    /// <summary>
    ///     Pay expenses from available interest (and reserve if needed), returning remaining funds.
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
                var expenseDue = formulaExecutor.EvaluateDouble(functionName);

                // Pay from available interest first
                var paidFromInterest = Math.Min(expenseDue, netInterest);
                netInterest -= paidFromInterest;

                // Cover shortfall from reserve if needed
                var shortfall = expenseDue - paidFromInterest;
                var paidFromReserve = DrawFromReserve(dynGroup, shortfall);

                var totalPaid = paidFromInterest + paidFromReserve;
                var remainingShortfall = expenseDue - totalPaid;

                ec.PayExpense(periodCf.CashflowDate, totalPaid, remainingShortfall);

                return totalPaid;
            });

        // Compute effective WAC after expenses
        var wac = 1200 * (periodCf.Interest + periodCf.UnAdvancedInterest - periodCf.ServiceFee - expenses) /
                  periodCf.BeginBalance;
        periodCf.Expenses = expenses;
        periodCf.EffectiveWac = wac;

        return netInterest;
    }

    /// <summary>
    ///     Pay interest via InterestPayable (with reserve draw for shortfalls), returning remaining funds.
    /// </summary>
    private double PayInterestStep(DynamicGroup dynGroup, IRateProvider rateProvider,
        PeriodCashflows periodCf, double availableInterest, List<DynamicTranche> allTranches)
    {
        if (dynGroup.InterestPayable == null)
            return availableInterest;

        // Calculate total interest due
        var interestDue = dynGroup.InterestPayable.InterestDue(periodCf.CashflowDate, rateProvider, allTranches);

        // Pay from available interest first
        var paidFromAvailable = Math.Min(availableInterest, interestDue);
        var shortfall = interestDue - paidFromAvailable;

        // Draw from reserve to cover shortfall
        var paidFromReserve = DrawFromReserve(dynGroup, shortfall);

        // Pay interest with augmented funds
        var totalFundsForInterest = paidFromAvailable + paidFromReserve;
        dynGroup.InterestPayable.PayInterest(null, periodCf.CashflowDate,
            totalFundsForInterest, rateProvider, allTranches);

        // Return remaining available interest (reserve draw doesn't add to remaining)
        return availableInterest - paidFromAvailable;
    }

    /// <summary>
    /// Cover note balance exceeding pool balance by drawing from reserve.
    /// Per prospectus: reserve can cover "principal payments needed to prevent
    /// aggregate principal amount of notes from exceeding Pool Balance"
    /// </summary>
    private void CoverNoteExcessFromReserve(DynamicGroup dynGroup, PeriodCashflows periodCf)
    {
        var poolBalance = dynGroup.GetVariable("collat_balance");
        var noteBalance = dynGroup.Balance();

        if (noteBalance <= poolBalance)
            return;

        var excess = noteBalance - poolBalance;
        var reserveDraw = DrawFromReserve(dynGroup, excess);

        if (reserveDraw > 0 && dynGroup.ScheduledPayable != null)
        {
            // Pay down notes with reserve funds (sequential)
            dynGroup.ScheduledPayable.PaySp(null, periodCf.CashflowDate, reserveDraw, () => { });
        }
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

        var noteBalanceBefore = dynGroup.Balance();
        dynGroup.ScheduledPayable.PaySp(null, adjPeriodCf.CashflowDate, availableSchedPrin,
            () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
        var noteBalanceAfter = dynGroup.Balance();

        // After scheduled principal, check if reserve draw needed for note > pool
        CoverNoteExcessFromReserve(dynGroup, adjPeriodCf);

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

        var noteBalanceBefore = dynGroup.Balance();
        dynGroup.PrepayPayable.PayUsp(null, adjPeriodCf.CashflowDate, availablePrepayPrin,
            () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
        var noteBalanceAfter = dynGroup.Balance();

        // After unscheduled principal, check if reserve draw needed for note > pool
        CoverNoteExcessFromReserve(dynGroup, adjPeriodCf);

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

        var noteBalanceBefore = dynGroup.Balance();
        dynGroup.RecoveryPayable.PayRp(null, adjPeriodCf.CashflowDate, availableRecovPrin,
            () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
        var noteBalanceAfter = dynGroup.Balance();

        // After recovery principal, check if reserve draw needed for note > pool
        CoverNoteExcessFromReserve(dynGroup, adjPeriodCf);

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
    /// Calculate how much OC should be released to CERTIFICATE.
    /// This is called BEFORE principal allocation to reserve funds for OC release.
    /// </summary>
    private double CalculateOcRelease(IDeal deal, DynamicGroup dynGroup, PeriodCashflows periodCf, double totalPrincipal)
    {
        var ocConfig = deal.OcTargetConfig;
        if (ocConfig == null)
            return 0;

        var poolBalance = periodCf.Balance; // End of period pool balance
        var noteBalance = dynGroup.Balance(); // Current note balance

        // After paying notes totalPrincipal, note balance will be:
        // noteBalance - totalPrincipal (assuming all goes to notes)
        // Pool balance is already at end-of-period value

        // Calculate what OC would be if all principal went to notes
        var projectedNoteBalance = Math.Max(0, noteBalance - totalPrincipal);
        var projectedOc = poolBalance - projectedNoteBalance;

        // Calculate target OC
        // Use initial pool balance if specified (static OC target from cut-off date)
        // Otherwise use current pool balance (dynamic OC target)
        var targetPoolBalance = ocConfig.UseInitialBalance ? ocConfig.InitialPoolBalance!.Value : poolBalance;
        var targetOc = Math.Max(ocConfig.TargetPct * targetPoolBalance, ocConfig.FloorAmt);

        // If projected OC exceeds target, release the excess
        if (projectedOc > targetOc)
        {
            return projectedOc - targetOc;
        }

        return 0;
    }

    /// <summary>
    ///     Pay Excess cashflows to Excess Structure
    /// Two Routes:
    /// 1. If OC below target: Pay excess interest to notes (turbo paydown) to build OC
    /// 2. If OC above target: Pay the pre-calculated OC release to certificates
    /// Returns remaining available interest after turbo/release.
    /// </summary>
    private double PayExcessTurboStep(IDeal deal, DynamicGroup dynGroup, PeriodCashflows periodCf,
        double availableInterest)
    {
        // Read OC config directly from deal
        var ocConfig = deal.OcTargetConfig;
        if (ocConfig == null)
            return availableInterest; // No OC target configured

        // Get pool and note balances
        var poolBalance = dynGroup.GetVariable("collat_balance");
        var noteBalance = dynGroup.Balance();
        var currentOc = poolBalance - noteBalance;

        // Calculate target OC = MAX(TargetPct * PoolBalance, FloorAmt)
        // Use initial pool balance if specified (static OC target from cut-off date)
        var targetPoolBalance = ocConfig.UseInitialBalance ? ocConfig.InitialPoolBalance!.Value : poolBalance;
        var targetOc = Math.Max(ocConfig.TargetPct * targetPoolBalance, ocConfig.FloorAmt);

        // Check if we have a pre-calculated OC release amount (from principal allocation)
        var ocReleaseAmount = dynGroup.GetVariable("oc_release_amount");

        if (ocReleaseAmount > 0 && dynGroup.ReleasePayable != null)
        {
            // Pay the OC release to CERTIFICATE
            // This amount was already held back from notes in RunComposablePeriod
            dynGroup.ReleasePayable.PaySp(null, periodCf.CashflowDate, ocReleaseAmount, () => { });
        }
        else if (currentOc < targetOc && availableInterest > 0 && dynGroup.TurboPayable != null)
        {
            // OC below target - turbo pay notes to build OC
            var shortfall = targetOc - currentOc;
            var turboAmount = Math.Min(availableInterest, shortfall);

            if (turboAmount > 0)
            {
                // Pay down notes (reduces note balance, increases OC)
                dynGroup.TurboPayable.PaySp(null, periodCf.CashflowDate, turboAmount, () => { });
                availableInterest -= turboAmount;
            }
        }

        return availableInterest;
    }

    /// <summary>
    /// Deposit to reserve account to reach target amount.
    /// Priority 18 in EART231 waterfall.
    /// Returns remaining available funds after deposit.
    /// </summary>
    private double PayReserveDepositStep(DynamicGroup dynGroup, PeriodCashflows periodCf,
        double availableInterest)
    {
        var reserve = dynGroup.FundsAccount;
        if (reserve == null)
            return availableInterest;

        var poolBalance = dynGroup.GetVariable("collat_balance");
        var noteBalance = dynGroup.Balance();

        // Calculate deposit needed to reach target
        var depositNeeded = reserve.DepositNeeded(poolBalance, noteBalance);
        var deposit = Math.Min(availableInterest, depositNeeded);

        if (deposit > 0)
            reserve.Credit(deposit);

        // Release any excess above effective target back to available funds
        var excess = reserve.ExcessBalance(poolBalance, noteBalance);
        if (excess > 0)
        {
            reserve.Debit(excess);
            availableInterest += excess;
        }

        // Record reserve cashflow for the period
        reserve.RecordCashflow(periodCf.CashflowDate);

        return availableInterest - deposit;
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
