using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.RulesEngine;
using GraamFlows.Triggers;
using GraamFlows.Util;
using GraamFlows.Waterfall.MarketTranche;
using GraamFlows.Waterfall.Structures.PayableStructures;
using WfOrder = GraamFlows.Objects.TypeEnum.WaterfallOrderEnum;

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
        // Re-time monthly collateral periods projected AHEAD of the first pay
        // date (pool projected from the closing/cutoff date, not the first pay
        // date) onto the deal's pay schedule, so each collateral month funds
        // exactly one distribution. Otherwise the first-pay fold below sums the
        // pre-first-pay stub month INTO the first paying period and the residual
        // (XS) sweeps ~2 months of collateral interest in period 0
        // (graam-harmony #2748). No-op when the pool already starts on the first
        // pay date (every WAL/pricing deal that isn't projected from closing).
        var periodCashflows = AlignStubPeriodsToPaySchedule(deal, cashflows.PeriodCashflows);
        var triggerMap = new Dictionary<string, IList<ITrigger>>();

        var formulaExecutor = new GenericExecutor(deal);
        var payRuleExecutor = new PayRuleExecutor(formulaExecutor, this);
        var dynDeal = new DynamicDeal(deal);
        var cashflowsBeforeFirstPay = new Dictionary<string, List<PeriodCashflows>>();

        // Get execution order from deal or use default (handle both null and empty list)
        var executionOrder = (deal.ExecutionOrder == null || !deal.ExecutionOrder.Any())
            ? GetDefaultExecutionOrder()
            : deal.ExecutionOrder.ToList();

        var dealTerminated = false;

        foreach (var period in periodCashflows.GroupBy(pc => pc.CashflowDate))
        {
            if (dealTerminated)
                break;

            // Compute collateral WAC
            var totalBeginBalance = period.Sum(p => p.BeginBalance);
            var collatWac = totalBeginBalance > 0
                ? period.Sum(p => p.Interest) / totalBeginBalance * 1200
                : 0;
            var collatNetWac = totalBeginBalance > 0
                ? period.Sum(p => p.NetInterest) / totalBeginBalance * 1200
                : 0;

            // Accumulate the adjusted per-group cashflows and trigger results for
            // this period so the exchangeable / notional overlay can run after all
            // groups' primary distribution completes (ported from MutableStructure).
            var periodCfList = new List<PeriodCashflows>();
            var periodTriggerValues = new List<TriggerValue>();

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

                // Test triggers and record results. Termination is deferred — ComposableStructure
                // runs the waterfall first (interest on begin balance) then terminates after.
                var triggerValues = TestAndRecordTriggers(dynGroup, triggers, adjPeriodCf);
                var terminated = triggerValues.Any(tv =>
                    tv.TriggerResultType == TriggerValueType.Executer &&
                    tv.TriggerExecuter?.TriggerExecType == TriggerExecutionType.Terminate);

                // Execute pay rules - this sets up the payable structures
                ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf);

                // Validate required payables are set
                ValidateRequiredPayables(deal, dynGroup);

                // Run composable waterfall period (interest accrues on begin balance even if terminating)
                RunComposablePeriod(deal, rateProvider, dynGroup, adjPeriodCf, triggerValues, formulaExecutor, payRuleExecutor, executionOrder);

                // Apply deferred termination: writedown losses and pay off all remaining balances
                if (terminated)
                {
                    ExecuteTermination(dynGroup, adjPeriodCf);
                    dealTerminated = true;
                }

                dynGroup.Advance(adjPeriodCf.CashflowDate);
                periodCf.EffectiveWac = adjPeriodCf.EffectiveWac;

                periodTriggerValues.AddRange(triggerValues);
                periodCfList.Add(adjPeriodCf);
            }

            // MACR / exchangeable + notional overlay (ported from MutableStructure).
            // After every group's primary distribution for this period, derive the
            // exchangeable (combined / recombinable) classes and notional classes as
            // proportional views of their component tranches' cashflows. These classes
            // have PayFrom=Exchange/Notional and are excluded from the cash-consuming
            // DealClasses, so they mirror — never double-count — the primaries.
            var periodDynGroups = dynDeal.DynamicGroups.ToList();
            PayExchangeables(period.Key, periodDynGroups, periodCfList, out _);
            PayExchangeableStructures(period.Key, periodCfList, periodDynGroups, payRuleExecutor, periodTriggerValues);
            PayNotionalClasses(period.Key, periodDynGroups, periodCfList);
        }

        var dealCashflows = dynDeal.DynamicGroups.CreateDealCashflows(cashflows, assumps);
        return dealCashflows;
    }

    /// <summary>
    ///     Re-times collateral periods that fall BEFORE the first pay date onto
    ///     the deal's pay schedule, one collateral month per distribution.
    ///
    ///     The pool is projected monthly starting at whatever date it is handed
    ///     (for pricing at/near closing this is the closing/cutoff date, which is
    ///     before — and often on a different day-of-month than — the first pay
    ///     date). The amortizer emits a FULL month of interest for every period,
    ///     so a pool projected one month ahead of the first pay date produces an
    ///     extra full "stub" month. The first-pay fold in <see cref="Waterfall" />
    ///     then adds that stub month INTO the first paying period, and because the
    ///     ResidualInterest (XS) class sweeps whatever interest is left after the
    ///     coupon classes, it collects ~2 months of collateral interest in the
    ///     first period — paying out more than the pool earned (graam-harmony
    ///     #2748).
    ///
    ///     Mapping the i-th monthly collateral period to the i-th pay date makes
    ///     the earliest (stub) period fund the first distribution and shifts the
    ///     rest forward one slot each — one month per distribution, nothing
    ///     double-counted and nothing dropped. This is a no-op for a pool that
    ///     already starts on the first pay date (the common case: all WAL and
    ///     scenario-screen deals), and only applies to monthly-pay deals since the
    ///     amortizer emits monthly collateral.
    /// </summary>
    private static IList<PeriodCashflows> AlignStubPeriodsToPaySchedule(
        IDeal deal, IList<PeriodCashflows> periodCashflows)
    {
        var firstTranche = deal.Tranches.FirstOrDefault();
        if (firstTranche == null || firstTranche.PayFrequency != 12 || periodCashflows.Count == 0)
            return periodCashflows;

        var firstPayActual = firstTranche.FirstPayDate;
        var firstPayMonth = new DateTime(firstPayActual.Year, firstPayActual.Month, 1);

        var result = new List<PeriodCashflows>(periodCashflows.Count);
        var changed = false;

        foreach (var group in periodCashflows.GroupBy(p => p.GroupNum))
        {
            var ordered = group.OrderBy(p => p.CashflowDate).ToList();

            // No stub — pool already starts on/after the first pay date. Leave
            // this group's dates untouched.
            if (ordered[0].CashflowDate >= firstPayMonth)
            {
                result.AddRange(ordered);
                continue;
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                var cf = ordered[i].Clone();
                cf.CashflowDate = firstPayActual.AddMonths(i);
                result.Add(cf);
            }

            changed = true;
        }

        return changed ? result : periodCashflows;
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

        // At most one ResidualInterest (XS / excess-spread) tranche per interest
        // group. The interest sweep (DynamicClass.PayInterest) gives the first
        // such tranche `availableFunds - interestPaid` — i.e. ALL remaining
        // interest — so a second residual in the same group is silently zeroed
        // (first-wins). Cash still conserves and nothing throws, so the error is
        // invisible at runtime; fail loudly at deal build instead. Mirrors the
        // SingleOrDefault contract the legacy TrancheAllocator relied on (which
        // threw on duplicates). Scoped per group, so a multi-group deal may
        // legitimately carry one residual each.
        var residualTranches = dynGroup.DynamicClasses
            .SelectMany(dc => dc.DynamicTranches)
            .Where(dt => dt.Tranche.CouponTypeEnum == CouponType.ResidualInterest)
            .ToList();
        if (residualTranches.Count > 1)
            throw new DealModelingException(deal.DealName,
                $"Interest group has {residualTranches.Count} ResidualInterest (excess-spread) tranches " +
                $"({string.Join(", ", residualTranches.Select(dt => dt.Tranche.TrancheName))}); at most one is " +
                "supported — the interest sweep pays all residual to the first, silently zeroing the rest.");
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

        // Notional (IO) tranches track the pool balance, not the principal
        // waterfall. Set their begin balance to the pool's begin balance BEFORE
        // interest so the effective coupon reflects the excess spread on the
        // pool (and never divides by a zero face).
        dynGroup.InitNotionalBalances(adjPeriodCf.BeginBalance, adjPeriodCf.CashflowDate);

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

        // OC release: reduce scheduled principal to notes when OC exceeds target,
        // maintaining OC at the target level. This matches auto ABS prospectus mechanics
        // where excess principal above the OC target is released to certificate holders.
        dynGroup.SetVariable("oc_release_amount", 0.0);
        if (deal.OcTargetConfig != null)
        {
            var ocRelease = CalculateOcRelease(deal, dynGroup, adjPeriodCf,
                availableSchedPrin + availablePrepayPrin + availableRecovPrin);
            if (ocRelease > 0)
            {
                var totalPrin = availableSchedPrin + availablePrepayPrin + availableRecovPrin;
                if (totalPrin > 0)
                {
                    var reduction = Math.Min(ocRelease, totalPrin);
                    var ratio = (totalPrin - reduction) / totalPrin;
                    availableSchedPrin *= ratio;
                    availablePrepayPrin *= ratio;
                    availableRecovPrin *= ratio;
                    dynGroup.SetVariable("oc_release_amount", reduction);
                }
            }
        }

        // Pay the excess-servicing IO strip (Class A-IO-S) out of the period
        // servicing fee before the offered-class waterfall. The fee is already
        // removed from collateral interest, so this neither appears in
        // `availableInterest` nor reduces it — it's a redirect of the servicing
        // fee, independent of the execution-order steps below.
        PayExcessServicingStep(dynGroup, rateProvider, adjPeriodCf, allTranches);

        // Execute steps in order
        var waterfallOrder = deal.WaterfallOrder;
        var interleavedDone = false;

        foreach (var step in executionOrder)
        {
            switch (step.ToUpperInvariant())
            {
                case "EXPENSE":
                    availableInterest = PayExpensesStep(formulaExecutor, dynGroup, adjPeriodCf, triggerValues,
                        availableInterest);
                    break;

                case "INTEREST":
                    if (waterfallOrder != WfOrder.Standard)
                    {
                        // Interleaved mode: handle INTEREST + all PRINCIPAL together on first encounter
                        if (!interleavedDone)
                        {
                            (availableInterest, availableSchedPrin, availablePrepayPrin, availableRecovPrin) =
                                PayInterleavedSteps(deal, dynGroup, rateProvider, adjPeriodCf, cfAlloc,
                                    triggerValues, payRuleExecutor, allTranches, waterfallOrder,
                                    availableInterest, availableSchedPrin, availablePrepayPrin, availableRecovPrin);
                            interleavedDone = true;
                        }
                        // Skip subsequent INTEREST/PRINCIPAL steps — already handled
                        break;
                    }
                    availableInterest = PayInterestStep(dynGroup, rateProvider, adjPeriodCf, availableInterest,
                        allTranches, deal.InterestTreatmentEnum);
                    break;

                case "PRINCIPAL_SCHEDULED":
                    if (waterfallOrder != WfOrder.Standard && interleavedDone) break;
                    availableSchedPrin = PayScheduledPrincipalStep(deal, dynGroup, adjPeriodCf, cfAlloc,
                        triggerValues, payRuleExecutor, availableSchedPrin);
                    break;

                case "PRINCIPAL_UNSCHEDULED":
                    if (waterfallOrder != WfOrder.Standard && interleavedDone) break;
                    availablePrepayPrin = PayUnscheduledPrincipalStep(deal, dynGroup, adjPeriodCf, cfAlloc,
                        triggerValues, payRuleExecutor, availablePrepayPrin);
                    break;

                case "PRINCIPAL_RECOVERY":
                    if (waterfallOrder != WfOrder.Standard && interleavedDone) break;
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

                case "EXCESS":
                case "EXCESS_RELEASE":
                    PayExcessReleaseStep(dynGroup, adjPeriodCf, availableInterest);
                    availableInterest = 0;
                    break;

                case "SUPPLEMENTAL_REDUCTION":
                    availableSchedPrin = PaySupplementalReductionStep(dynGroup, adjPeriodCf, availableSchedPrin);
                    break;

                case "CAP_CARRYOVER":
                    availableInterest = PayCapCarryoverStep(dynGroup, adjPeriodCf, availableInterest);
                    break;
            }
        }

        // Update Certificate tranche balance to reflect current OC (Pool - Notes)
        // Called AFTER all principal payments so both pool and note balances are at end-of-period values
        dynGroup.UpdateCertificateBalance(adjPeriodCf.Balance, adjPeriodCf.CashflowDate);

        // Settle notional (IO) tranches to the pool's end balance so the
        // notional amortizes with the pool (real WAL); the IO holder gets no
        // principal cash.
        dynGroup.SettleNotionalBalances(adjPeriodCf.Balance, adjPeriodCf.CashflowDate);
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
    ///     For Guaranteed interest treatment, each tranche gets its full coupon regardless of available funds.
    /// </summary>
    private double PayInterestStep(DynamicGroup dynGroup, IRateProvider rateProvider,
        PeriodCashflows periodCf, double availableInterest, List<DynamicTranche> allTranches,
        InterestTreatmentEnum interestTreatment = InterestTreatmentEnum.Collateral)
    {
        if (dynGroup.InterestPayable == null)
            return availableInterest;

        // Calculate total interest due
        var interestDue = dynGroup.InterestPayable.InterestDue(periodCf.CashflowDate, rateProvider, allTranches);

        if (interestTreatment == InterestTreatmentEnum.Guaranteed)
        {
            // Guaranteed: pay full coupon to every tranche regardless of available pool interest.
            // Shortfall is covered by the servicer/guarantor (e.g., Freddie Mac for STACR).
            dynGroup.InterestPayable.PayInterest(null, periodCf.CashflowDate,
                interestDue, rateProvider, allTranches);

            // Pool interest is still consumed — but any shortfall doesn't reduce available funds below zero
            return Math.Max(0, availableInterest - interestDue);
        }

        // Collateral: pay from available interest, draw reserve for shortfalls
        var paidFromAvailable = Math.Min(availableInterest, interestDue);
        var shortfall = interestDue - paidFromAvailable;

        // Draw from reserve to cover shortfall
        var paidFromReserve = DrawFromReserve(dynGroup, shortfall);

        // Pass ALL available interest (plus any reserve top-up) into the
        // structure — not just the coupon due — so a terminal ResidualInterest
        // tranche (XS / excess spread) can sweep whatever is left after the
        // coupon-bearing classes. Coupon classes still take only their due, so
        // with no residual sweeper the excess flows back unchanged via the
        // PayInterest return value (Payscen TrancheAllocator parity).
        var totalFundsForInterest = availableInterest + paidFromReserve;
        var paid = dynGroup.InterestPayable.PayInterest(null, periodCf.CashflowDate,
            totalFundsForInterest, rateProvider, allTranches);

        // Remaining = available pool interest not consumed (reserve top-up isn't
        // pool money, so exclude it from what we treat as "paid from available").
        var paidFromPool = Math.Max(0, paid - paidFromReserve);
        return availableInterest - paidFromPool;
    }

    /// <summary>
    /// Pay INTEREST and PRINCIPAL steps interleaved by seniority level.
    /// Walks the top-level children of each payable structure in lockstep.
    /// </summary>
    private (double interest, double sched, double prepay, double recov) PayInterleavedSteps(
        IDeal deal, DynamicGroup dynGroup, IRateProvider rateProvider,
        PeriodCashflows adjPeriodCf, CashflowAllocs cfAlloc,
        List<TriggerValue> triggerValues, IPayRuleExecutor payRuleExecutor,
        List<DynamicTranche> allTranches, WfOrder order,
        double availableInterest, double availableSchedPrin,
        double availablePrepayPrin, double availableRecovPrin)
    {
        var intChildren = dynGroup.InterestPayable?.GetChildren() ?? new List<IPayable>();
        var schedChildren = dynGroup.ScheduledPayable?.GetChildren() ?? new List<IPayable>();
        var prepayChildren = dynGroup.PrepayPayable?.GetChildren() ?? new List<IPayable>();
        var recovChildren = dynGroup.RecoveryPayable?.GetChildren() ?? new List<IPayable>();

        var maxChildren = new[] { intChildren.Count, schedChildren.Count, prepayChildren.Count, recovChildren.Count }.Max();

        for (var i = 0; i < maxChildren; i++)
        {
            if (order == WfOrder.InterestFirst)
            {
                availableInterest = PayInterestChild(dynGroup, rateProvider, adjPeriodCf, allTranches,
                    intChildren, i, availableInterest);
                (availableSchedPrin, availablePrepayPrin, availableRecovPrin) =
                    PayPrincipalChildren(deal, dynGroup, adjPeriodCf, triggerValues, payRuleExecutor,
                        schedChildren, prepayChildren, recovChildren, i,
                        availableSchedPrin, availablePrepayPrin, availableRecovPrin);
            }
            else // PrincipalFirst
            {
                (availableSchedPrin, availablePrepayPrin, availableRecovPrin) =
                    PayPrincipalChildren(deal, dynGroup, adjPeriodCf, triggerValues, payRuleExecutor,
                        schedChildren, prepayChildren, recovChildren, i,
                        availableSchedPrin, availablePrepayPrin, availableRecovPrin);
                availableInterest = PayInterestChild(dynGroup, rateProvider, adjPeriodCf, allTranches,
                    intChildren, i, availableInterest);
            }
        }

        CoverNoteExcessFromReserve(dynGroup, adjPeriodCf);

        return (availableInterest, availableSchedPrin, availablePrepayPrin, availableRecovPrin);
    }

    /// <summary>
    /// Pay interest for a single seniority level (one child of InterestPayable).
    /// </summary>
    private double PayInterestChild(DynamicGroup dynGroup, IRateProvider rateProvider,
        PeriodCashflows periodCf, List<DynamicTranche> allTranches,
        List<IPayable> intChildren, int index, double availableInterest)
    {
        if (index >= intChildren.Count || availableInterest < 0.01)
            return availableInterest;

        var child = intChildren[index];
        var due = child.InterestDue(periodCf.CashflowDate, rateProvider, allTranches);
        var paidFromAvailable = Math.Min(availableInterest, due);
        var paidFromReserve = DrawFromReserve(dynGroup, due - paidFromAvailable);
        // Pass all available interest (plus reserve top-up) so a ResidualInterest
        // child sweeps the remainder; coupon classes take only their due, so the
        // excess flows back unchanged when there's no sweeper. Mirrors
        // PayInterestStep (Payscen TrancheAllocator parity).
        var paid = child.PayInterest(null, periodCf.CashflowDate,
            availableInterest + paidFromReserve, rateProvider, allTranches);

        var paidFromPool = Math.Max(0, paid - paidFromReserve);
        return availableInterest - paidFromPool;
    }

    /// <summary>
    /// Pay scheduled, unscheduled, and recovery principal for a single seniority level.
    /// </summary>
    private (double sched, double prepay, double recov) PayPrincipalChildren(
        IDeal deal, DynamicGroup dynGroup, PeriodCashflows adjPeriodCf,
        List<TriggerValue> triggerValues, IPayRuleExecutor payRuleExecutor,
        List<IPayable> schedChildren, List<IPayable> prepayChildren, List<IPayable> recovChildren,
        int index, double availableSchedPrin, double availablePrepayPrin, double availableRecovPrin)
    {
        if (index < schedChildren.Count && availableSchedPrin > 0.01)
        {
            var child = schedChildren[index];
            var balBefore = child.CurrentBalance(adjPeriodCf.CashflowDate);
            child.PaySp(null, adjPeriodCf.CashflowDate, availableSchedPrin,
                () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
            availableSchedPrin -= (balBefore - child.CurrentBalance(adjPeriodCf.CashflowDate));
        }

        if (index < prepayChildren.Count && availablePrepayPrin > 0.01)
        {
            var child = prepayChildren[index];
            var balBefore = child.CurrentBalance(adjPeriodCf.CashflowDate);
            child.PayUsp(null, adjPeriodCf.CashflowDate, availablePrepayPrin,
                () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
            availablePrepayPrin -= (balBefore - child.CurrentBalance(adjPeriodCf.CashflowDate));
        }

        if (index < recovChildren.Count && availableRecovPrin > 0.01)
        {
            var child = recovChildren[index];
            var balBefore = child.CurrentBalance(adjPeriodCf.CashflowDate);
            child.PayRp(null, adjPeriodCf.CashflowDate, availableRecovPrin,
                () => ExecutePayRules(deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf));
            availableRecovPrin -= (balBefore - child.CurrentBalance(adjPeriodCf.CashflowDate));
        }

        return (availableSchedPrin, availablePrepayPrin, availableRecovPrin);
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

        // Excess-spread first-loss: a ResidualInterest (XS / excess-spread) class placed
        // in the writedown structure absorbs the period loss out of the excess spread it
        // swept this period, BEFORE any funded bond is written down. XS has no principal
        // (its notional balance is reset to the pool each period), so it can only absorb
        // via excess spread — its WritedownCapacity is 0, so the shortfall (loss beyond
        // this period's excess spread) cascades to the funded bonds below. When no XS sits
        // in the writedown structure this is a no-op and losses write down bonds directly.
        writedownAmt = AbsorbLossFromExcessSpread(dynGroup, periodCf, writedownAmt);
        if (writedownAmt <= 0.005)
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
    /// Absorb the period loss out of the excess spread swept by any ResidualInterest
    /// (XS) class that sits in the writedown structure — the excess-spread first-loss
    /// layer. Reduces the XS class's recorded interest for the period by the loss it
    /// absorbs (its cash shrinks with losses) and returns the shortfall — the loss
    /// beyond this period's excess spread — which the caller then cascades to the
    /// funded bonds. When no XS is present in the writedown structure the loss passes
    /// through unchanged.
    /// </summary>
    private double AbsorbLossFromExcessSpread(DynamicGroup dynGroup, PeriodCashflows periodCf, double loss)
    {
        if (dynGroup.WritedownPayable == null)
            return loss;

        foreach (var leaf in dynGroup.WritedownPayable.Leafs().OfType<DynamicClass>())
        {
            if (loss <= 0.005)
                break;
            if (!leaf.IsResidualInterest)
                continue;

            foreach (var dynTran in leaf.DynamicTranches)
            {
                var cf = dynTran.GetCashflow(periodCf.CashflowDate);
                if (cf == null)
                    continue;

                var excessSpread = cf.Interest;
                if (excessSpread <= 0)
                    continue;

                var absorbed = Math.Min(excessSpread, loss);
                cf.Interest = excessSpread - absorbed; // XS releases less; it funded the loss
                cf.Writedown += absorbed;              // report the loss XS absorbed via excess spread
                loss -= absorbed;
                if (loss <= 0.005)
                    break;
            }
        }

        return loss;
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

        // Calculate what OC would be if all principal went to notes
        var projectedNoteBalance = Math.Max(0, noteBalance - totalPrincipal);
        var projectedOc = poolBalance - projectedNoteBalance;

        // Calculate target OC using the configured formula
        var targetOc = ocConfig.CalculateTargetOc(poolBalance);

        // If projected OC exceeds target, release the excess
        if (projectedOc > targetOc)
            return projectedOc - targetOc;

        return 0;
    }

    /// <summary>
    ///     Pay Excess cashflows to Excess Structure
    /// Two Routes:
    /// 1. If OC below target: Pay excess interest to notes (turbo paydown) to build OC
    /// 2. If OC above target: Pay the pre-calculated OC release to certificates
    /// Returns remaining available interest after turbo/release.
    /// </summary>
    /// <summary>
    ///     Pay the excess-servicing IO strip (Class A-IO-S) its strip from the
    ///     period servicing fee. The strip is identified by
    ///     <c>DealStructure.PayFrom == ExcessServicing</c> (assigned by
    ///     <see cref="GraamFlows.Api.Transformers.UnifiedWaterfallBuilder"/> for a
    ///     non-residual Reference/IOS IO tranche). The servicing fee was already
    ///     deducted from collateral interest before the waterfall, so paying the
    ///     strip from it neither reduces nor draws on the interest available to the
    ///     offered classes — it is a redirect of the fee, mirroring the reference
    ///     deal models' <c>serv_fee_rate</c> strip. The amount is the tranche's own
    ///     coupon on its notional when one is supplied, otherwise the full servicing
    ///     fee, capped at the fee actually collected.
    /// </summary>
    private void PayExcessServicingStep(DynamicGroup dynGroup, IRateProvider rateProvider,
        PeriodCashflows periodCf, IEnumerable<DynamicTranche> allTranches)
    {
        var serviceFee = periodCf.ServiceFee;
        if (serviceFee <= 0)
            return;

        var allTranchesList = allTranches?.ToList();
        var stripTranches = dynGroup.DynamicClasses
            .SelectMany(dc => dc.DynamicTranches)
            .Where(dt => dt.DealStructure != null &&
                         dt.DealStructure.PayFromEnum == PayFromEnum.ExcessServicing);

        foreach (var dynTran in stripTranches)
        {
            var cf = dynTran.GetCashflow(periodCf.CashflowDate);
            var stripDue = dynTran.Interest(cf, rateProvider, allTranchesList);
            if (stripDue <= 0)
                stripDue = serviceFee;
            var toPay = Math.Min(stripDue, serviceFee);
            if (toPay > 0)
                dynTran.PayInterest(cf, rateProvider, null, allTranchesList, toPay);
        }
    }

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

        // Calculate target OC using the configured formula
        var targetOc = ocConfig.CalculateTargetOc(poolBalance);

        // Check if we have a pre-calculated OC release amount (from principal allocation)
        var ocReleaseAmount = dynGroup.GetVariable("oc_release_amount");

        if (ocReleaseAmount > 0 && dynGroup.ReleasePayable != null)
        {
            // Pay the OC release to CERTIFICATE
            dynGroup.ReleasePayable.PaySp(null, periodCf.CashflowDate, ocReleaseAmount, () => { });
        }
        else if (currentOc < targetOc && availableInterest > 0)
        {
            // OC below target - turbo pay notes to build OC
            var shortfall = targetOc - currentOc;
            var turboAmount = Math.Min(availableInterest, shortfall);

            if (turboAmount > 0)
            {
                // Pay down notes (reduces note balance, increases OC)
                // Track actual amount absorbed — if notes are fully paid off,
                // PaySp won't absorb anything and funds should remain available.
                var turboPayable = dynGroup.TurboPayable ?? dynGroup.ScheduledPayable;
                var noteBalBefore = dynGroup.Balance();
                turboPayable?.PaySp(null, periodCf.CashflowDate, turboAmount, () => { });
                var actualTurboPaid = noteBalBefore - dynGroup.Balance();
                availableInterest -= actualTurboPaid;
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
    /// Records as Interest (not Principal) on certificate cashflows to avoid
    /// conflicting with UpdateCertificateBalance's balance-derived principal tracking.
    /// </summary>
    private void PayExcessReleaseStep(DynamicGroup dynGroup, PeriodCashflows periodCf,
        double availableInterest)
    {
        if (availableInterest <= 0)
            return;

        // Release the excess to the EXCESS_RELEASE structure's classes IN ORDER.
        // e.g. SEQ(XS, R) sends the excess to the XS excess-spread strip first,
        // then R — earlier this ignored the structure and dumped it onto every
        // Certificate class, so an XS strip listed first got nothing while the
        // residual Certificate scooped it all (#1714). The first recipient (the
        // excess-spread / residual sweeper) takes the remainder as interest.
        //
        // Descend the structure recursively (ordered) so a nested SEQ/PRORATA
        // release — SEQ(SEQ(XS), R) — still yields XS as the first recipient
        // rather than silently dropping it (a flat OfType<DynamicClass>() misses
        // the inner SequentialStructure and reopens #1714).
        var recipients = ReleaseRecipientsInOrder(dynGroup.ReleasePayable).ToList();
        if (!recipients.Any())
            // No release structure — fall back to the OC certificate(s).
            recipients = dynGroup.DynamicClasses
                .Where(dc => dc.Tranche.TrancheTypeEnum == TrancheTypeEnum.Certificate)
                .ToList();

        var remaining = availableInterest;
        foreach (var cls in recipients)
        {
            if (remaining <= 0.01)
                break;

            // WHERE the released interest must land depends on how the recipient is serialized:
            //   - A Certificate / OC class is output from its CLASS cashflow — ConvertToResponse
            //     SKIPS Certificate per-tranche cashflows (they track balance in ClassCashflows),
            //     and UpdateCertificateBalance writes that same class cashflow. Crediting the
            //     per-tranche cashflow here dropped the excess from the output entirely: the
            //     certificate showed $0 interest and ~all net interest went unaccounted
            //     (graam-flows#32, harmony PAID 2026-2). Credit the class cashflow so the release
            //     lands where the output reads it (UpdateCertificateBalance only touches
            //     balance/principal, never Interest, so the two don't conflict).
            if (cls.Tranche.TrancheTypeEnum == TrancheTypeEnum.Certificate)
            {
                cls.GetCashflow(periodCf.CashflowDate).Interest += remaining;
                remaining = 0;
                continue;
            }

            //   - Any other recipient (an XS / residual-interest strip) is output from its
            //     per-TRANCHE cashflows. Split the release across the recipient class's tranches —
            //     balance-weighted, even split when balances are 0 — so a multi-tranche (combined /
            //     exchangeable) class receives the amount ONCE, not once per tranche (#1714).
            var tranches = cls.DynamicTranches;
            if (tranches == null || tranches.Count == 0)
                continue;
            var totalBal = tranches.Sum(t => t.GetCashflow(periodCf.CashflowDate).BeginBalance);
            foreach (var dynTran in tranches)
            {
                var cf = dynTran.GetCashflow(periodCf.CashflowDate);
                var share = totalBal > 0.01
                    ? remaining * (cf.BeginBalance / totalBal)
                    : remaining / tranches.Count;
                cf.Interest += share;
            }
            remaining = 0; // first (residual) recipient sweeps the excess
        }
    }

    /// <summary>
    ///     Leaf recipient classes of an EXCESS_RELEASE structure, in order,
    ///     descending nested SEQ / PRORATA payables. A DynamicClass is a leaf;
    ///     any other payable is a container whose ordered children are walked.
    /// </summary>
    private static IEnumerable<DynamicClass> ReleaseRecipientsInOrder(IPayable payable)
    {
        if (payable == null)
            yield break;
        if (payable is DynamicClass dc)
        {
            yield return dc;
            yield break;
        }
        var children = payable.GetChildren();
        if (children == null)
            yield break;
        foreach (var child in children)
        foreach (var leaf in ReleaseRecipientsInOrder(child))
            yield return leaf;
    }

    /// <summary>
    ///     Cap Carryover step for Private RMBS with WAC-capped coupons.
    ///     When a tranche coupon is limited by the Net WAC Rate (e.g., MIN(fixed, eff_wac)),
    ///     the shortfall accumulates as AccumInterestShortfall on each tranche cashflow.
    ///     This step uses available excess cashflow to pay back those accumulated shortfalls
    ///     sequentially per the Cap Carryover payable structure.
    /// </summary>
    private double PayCapCarryoverStep(DynamicGroup dynGroup, PeriodCashflows periodCf,
        double availableInterest)
    {
        if (dynGroup.CapCarryoverPayable == null || availableInterest <= 0)
            return availableInterest;

        // Walk the payable structure and pay back accumulated interest shortfalls
        var totalPaid = dynGroup.CapCarryoverPayable.PayInterestShortfall(
            periodCf.CashflowDate, availableInterest);

        return availableInterest - totalPaid;
    }

    /// <summary>
    ///     Pay supplemental subordinate reduction amount.
    ///     If the aggregate balance of offered tranches exceeds the cap percentage of pool balance,
    ///     the excess is paid down as principal via the supplemental payable structure.
    /// </summary>
    /// <summary>
    /// Supplemental Reduction: replaces CSCAP by computing credit support and redirecting
    /// excess principal from seniors to subordinates when support exceeds the cap.
    /// Uses the same math as EnhancementCapStructure.CalcExcessEnhancement.
    ///
    /// Senior tranches = tranches exclusive to the primary waterfall (AH, A1/A1H, B-classes).
    /// Sub tranches = tranches in the cap overflow (M1/M1H, M2A/M2AH, M2B/M2BH).
    /// The cap variable (SupplSubReduAmt, typically 5.5%) is the maximum credit support level.
    /// </summary>
    private double PaySupplementalReductionStep(DynamicGroup dynGroup, PeriodCashflows periodCf,
        double availableSchedPrin)
    {
        if (dynGroup.SupplementalPayable == null ||
            dynGroup.SupplementalCapVariable == null ||
            dynGroup.SupplementalOfferedTranches == null ||
            dynGroup.SupplementalSeniorTranches == null)
            return availableSchedPrin;

        if (availableSchedPrin < 0.01)
            return availableSchedPrin;

        var cap = dynGroup.GetVariable(dynGroup.SupplementalCapVariable, periodCf.CashflowDate);

        // Sum balances for senior-only and sub tranches
        var senBal = 0.0;
        foreach (var name in dynGroup.SupplementalSeniorTranches)
        {
            var dc = dynGroup.ClassByName(name);
            if (dc != null) senBal += dc.Balance;
        }

        var subBal = 0.0;
        foreach (var name in dynGroup.SupplementalOfferedTranches)
        {
            var dc = dynGroup.ClassByName(name);
            if (dc != null) subBal += dc.Balance;
        }

        // Credit support if all principal goes to seniors:
        // cs = 1 - (senBal - prin) / (senBal - prin + subBal)
        var adjSenBal = senBal - availableSchedPrin;
        var total = adjSenBal + subBal;
        if (total <= 0) return availableSchedPrin;

        var expectedSupport = 1.0 - adjSenBal / total;
        if (double.IsNaN(expectedSupport) || double.IsInfinity(expectedSupport))
            return availableSchedPrin;

        double subPrin = 0;
        if (expectedSupport > cap)
        {
            var excess = expectedSupport - cap;
            var excessAmt = excess * total;
            excessAmt = Math.Min(excessAmt, availableSchedPrin);
            subPrin = excessAmt;
        }

        // Balance overflow: if remaining senior principal exceeds senior balance
        var senPrin = availableSchedPrin - subPrin;
        if (senPrin > senBal)
        {
            var overflow = senPrin - senBal;
            senPrin = senBal;
            subPrin += overflow;
        }
        if (subPrin > subBal)
        {
            var overflow = subPrin - subBal;
            subPrin = subBal;
            senPrin += overflow;
        }

        if (subPrin < 0.01)
            return availableSchedPrin;

        // Distribute subordinate portion through the supplemental payable
        dynGroup.SupplementalPayable.PaySp(null, periodCf.CashflowDate, subPrin, () => { });
        return availableSchedPrin - subPrin;
    }

    /// <summary>
    /// Test triggers and record results without executing termination or pay rules.
    /// Used by ComposableStructure to defer termination until after the waterfall steps.
    /// </summary>
    /// <summary>
    /// Check if a payable tree contains an EnhancementCapStructure (CSCAP) node.
    /// </summary>
    private static bool ContainsEnhancementCap(IPayable? payable)
    {
        if (payable == null) return false;
        if (payable is PayableStructures.EnhancementCapStructure) return true;

        var children = payable.GetChildren();
        if (children == null) return false;

        var queue = new Queue<IPayable>(children);
        while (queue.Count > 0)
        {
            var child = queue.Dequeue();
            if (child is PayableStructures.EnhancementCapStructure) return true;
            var sub = child.GetChildren();
            if (sub != null)
                foreach (var s in sub)
                    queue.Enqueue(s);
        }
        return false;
    }

    private List<TriggerValue> TestAndRecordTriggers(DynamicGroup dynGroup, IList<ITrigger> triggers,
        PeriodCashflows adjPeriodCf)
    {
        var triggerValues = TestTriggers(triggers, dynGroup, adjPeriodCf.CashflowDate, adjPeriodCf);
        foreach (var triggerResult in triggerValues)
            dynGroup.AddTriggerResult(adjPeriodCf.CashflowDate, triggerResult.TriggerName,
                triggerResult.NumericValue, triggerResult.RequiredValue, triggerResult.TriggerResult);
        return triggerValues;
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
