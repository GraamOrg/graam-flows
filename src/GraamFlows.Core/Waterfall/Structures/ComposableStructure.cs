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
        // Sort explicitly. `AlignStubPeriodsToPaySchedule` sorts as a side effect of
        // re-dating, but it is a no-op under Fold/Drop and for non-monthly deals — and the
        // waterfall IS order-sensitive (a date-descending tape gives a different answer),
        // so relying on that side effect made correctness depend on the policy.
        var periodCashflows = AlignStubPeriodsToPaySchedule(
            deal,
            cashflows.PeriodCashflows.OrderBy(pc => pc.CashflowDate).ToList());
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

                // Collateral periods dated before the first distribution. A deal's cut-off
                // normally precedes its closing, so there is usually more than one; whether
                // the waterfall may spend them is a modelling choice, not a fact.
                //   Align — re-date onto the pay schedule, and FOLD whatever the re-dating
                //           could not reach. It is monthly-only, and it re-dates onto the
                //           first pay date rather than a stated CollateralAccrualStartDate,
                //           so periods survive it on any quarterly or semi-annual deal and
                //           on any deal that states a later boundary. Gating the fold on
                //           `== Fold` discarded exactly those — Align became Drop for every
                //           non-monthly deal, taking 4,474,457.84 of a 100M quarterly pool
                //           and 8,748,707.96 of a semi-annual one, written down nowhere.
                //   Fold  — accumulate into the first distribution (historical behaviour)
                //   Drop  — exclude them; the first distribution spends one period
                // The boundary is the deal's stated CollateralAccrualStartDate when given,
                // else the derived first-of-month of the first tranche's FirstPayDate.
                if (periodCf.CashflowDate < dynGroup.CollateralAccrualStart)
                {
                    if (deal.FirstPeriodCollateralPolicyEnum != FirstPeriodCollateralPolicyEnum.Drop)
                    {
                        if (!cashflowsBeforeFirstPay.ContainsKey(periodCf.GroupNum))
                            cashflowsBeforeFirstPay[periodCf.GroupNum] = new List<PeriodCashflows>();
                        cashflowsBeforeFirstPay[periodCf.GroupNum].Add(periodCf);
                    }

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

        // The fold accumulator drains onto the first period at/after the boundary FOR THE
        // SAME GROUP. If a group never reaches one, whatever was accumulated is neither
        // paid, nor folded, nor written down — it is simply dropped on the floor when this
        // dictionary goes out of scope, with no error and no trace. Reachable two ways, and
        // both were silent: a CollateralAccrualStartDate past the end of the tape (100% of
        // the pool, zero tranche rows returned, no exception), and a collateral group whose
        // periods all precede the boundary.
        //
        // Failing loud rather than guessing where the money should go: a boundary the
        // collateral never reaches is a mis-stated deal, and the engine cannot know whether
        // the caller meant a later boundary or a longer tape. Returning an empty cashflow
        // set for a funded pool is the one answer that must not ship.
        if (cashflowsBeforeFirstPay.Any(kv => kv.Value.Count > 0))
        {
            var stranded = cashflowsBeforeFirstPay
                .Where(kv => kv.Value.Count > 0)
                .Select(kv =>
                    $"group {kv.Key}: {kv.Value.Count} period(s), " +
                    $"{kv.Value.Sum(p => p.ScheduledPrincipal + p.UnscheduledPrincipal):N2} principal");
            throw new InvalidOperationException(
                "Collateral was accumulated ahead of the first distribution and never "
                + "reached one — no period in the group falls on or after "
                + $"CollateralAccrualStart ({string.Join("; ", stranded)}). That principal "
                + "would be paid to nobody and written down nowhere. Check "
                + "CollateralAccrualStartDate against the collateral's last period, or the "
                + "group's pay schedule.");
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
        // Re-timing is itself a policy, and it PRE-EMPTS the fold: once every period has
        // been moved onto the pay schedule nothing is left before the boundary, so Fold and
        // Drop become indistinguishable. A caller asking for either is asking for the
        // collateral NOT to be re-dated.
        if (deal.FirstPeriodCollateralPolicyEnum != FirstPeriodCollateralPolicyEnum.Align)
            return periodCashflows;

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

        // The coverage cascade IS a per-level interleaving of interest and principal
        // (each failing level diverts interest to principal mid-interest-waterfall),
        // so combining it with the interleaved waterfall orders would run two
        // conflicting notions of "per level". Fail loudly instead of silently
        // producing one of them.
        if (deal.CoverageCascade is { Count: > 0 } && deal.WaterfallOrder != WfOrder.Standard)
            throw new DealModelingException(deal.DealName,
                "CoverageCascade requires the standard waterfall order; interleaved " +
                "(interestFirst/principalFirst) INTEREST/PRINCIPAL is not supported with per-level coverage tests.");

        // At most one ExcessInterest (XS / monthly-excess-cashflow) tranche per interest
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
            .Where(dt => dt.Tranche.CouponTypeEnum == CouponType.ExcessInterest)
            .ToList();
        if (residualTranches.Count > 1)
            throw new DealModelingException(deal.DealName,
                $"Interest group has {residualTranches.Count} ExcessInterest (excess-spread) tranches " +
                $"({string.Join(", ", residualTranches.Select(dt => dt.Tranche.TrancheName))}); at most one is " +
                "supported — the interest sweep pays all residual to the first, silently zeroing the rest.");

        // An Exchanged (combinable / MACR) class pays through the component notes
        // it combines, via a PayFrom=Exchange structure whose ExchangableTranche
        // names those components. A class typed Exchanged but missing that
        // reference — e.g. a plain debt note mis-typed as Exchanged by an
        // upstream extractor — would otherwise NRE deep in the subordination walk
        // (DynamicGroup.SubordinateClasses splits a null ExchangableTranche). Fail
        // loudly and actionably here instead (cf. #42).
        foreach (var dc in dynGroup.DynamicClasses)
        {
            var ds = dc.DealStructure;
            if (ds is not { PayFromEnum: PayFromEnum.Exchange })
                continue;

            if (string.IsNullOrWhiteSpace(ds.ExchangableTranche))
                throw new DealModelingException(deal.DealName,
                    $"Class '{dc.Tranche.TrancheName}' is typed Exchanged but names no component classes " +
                    "(exchangableTranche is empty). An Exchanged/combinable class must reference the classes " +
                    "it combines; a plain debt note should be typed Offered (or similar), not Exchanged.");

            foreach (var component in ds.ExchangableTranche.Split(
                         ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (dynGroup.ClassByName(component) == null)
                    throw new DealModelingException(deal.DealName,
                        $"Class '{dc.Tranche.TrancheName}' (Exchanged) references unknown component class " +
                        $"'{component}'.");
        }
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

        // Excess-spread first-loss applies automatically when an ExcessInterest (XS)
        // strip is present — UNLESS the deal already routes losses through an OC-target
        // or excess-turbo/release path. In those deals the excess spread is trapped to
        // build OC or turbo-pay the notes, and diverting the same excess spread to cover
        // writedowns here would absorb it twice. Defer to that path (skip auto-absorb).
        var excessSpreadFirstLoss = deal.OcTargetConfig == null &&
            !executionOrder.Any(s =>
            {
                var u = s.ToUpperInvariant();
                return u == "EXCESS_TURBO" || u == "EXCESS" || u == "EXCESS_RELEASE";
            });

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
                    // CLO per-level OC/IC coverage cascade replaces the flat interest
                    // step when configured: pay each level's interest, test coverage,
                    // and divert available interest to senior-first principal on a
                    // failing test — before any junior level's interest.
                    availableInterest = deal.CoverageCascade is { Count: > 0 }
                        ? PayCoverageCascadeInterestStep(deal, dynGroup, rateProvider, adjPeriodCf,
                            availableInterest, allTranches)
                        : PayInterestStep(dynGroup, rateProvider, adjPeriodCf, availableInterest,
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
                    PayWritedownStep(dynGroup, adjPeriodCf, cfAlloc.Writedown, excessSpreadFirstLoss);
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

        // REMIC Residual (Class R) catch-all — runs AFTER the certificate. The
        // Residual is non-economic: in a well-formed deal it stays ~0, and a
        // non-zero value is a deliberate red flag that cash went unclaimed.
        //  - Interest: the certificate path only ever handles principal, so any
        //    leftover interest (no XS sweep, no excess step consumed it) is
        //    genuinely unallocated and lands here — no double-count.
        //  - Principal: the certificate already absorbs the OC (Pool - Notes) via
        //    its balance identity, so route leftover principal here ONLY when there
        //    is no certificate to catch it (else it would be counted twice).
        var residualPrincipal =
            dynGroup.DynamicClasses.Any(dc => dc.Tranche.TrancheTypeEnum == TrancheTypeEnum.Certificate)
                ? 0.0
                : availableSchedPrin + availablePrepayPrin + availableRecovPrin;
        CreditResidual(dynGroup, adjPeriodCf, availableInterest, residualPrincipal);
    }

    /// <summary>
    /// Book any cash left unclaimed at the end of the period onto the non-economic
    /// REMIC Residual (Class R). No-op when the group has no Residual class or when
    /// nothing is left over. A genuine distribution error (cash stranded while a
    /// funded note is still owed) is already caught inside the principal cascade
    /// (SequentialStructure), so anything reaching here is a legitimate — and, for a
    /// well-formed deal, ~0 — residual amount.
    /// </summary>
    private void CreditResidual(DynamicGroup dynGroup, PeriodCashflows periodCf,
        double leftoverInterest, double leftoverPrincipal)
    {
        if (leftoverInterest <= 0.005 && leftoverPrincipal <= 0.005)
            return;

        var dynTran = dynGroup.DynamicClasses
            .FirstOrDefault(dc => dc.IsResidual)?.DynamicTranches.FirstOrDefault();
        if (dynTran == null)
            return;

        var cf = dynTran.GetCashflow(periodCf.CashflowDate);
        if (leftoverInterest > 0)
            cf.Interest += leftoverInterest;
        if (leftoverPrincipal > 0)
            cf.UnscheduledPrincipal += leftoverPrincipal;
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
        // `+ periodCf.UnAdvancedInterest` was here, and it was the exact algebraic
        // inverse of the delinquency docking the amortizer used to apply: interest
        // arrived short by `interest * dq * (1 - adv)` and this added it straight
        // back, reconstructing the contractual net WAC. With the docking gone
        // (graam-harmony #4481 §1.1) the add-back is uncompensated and inflates
        // eff_wac by `1 + dq * (1 - adv)` — +113bp at dq=20/adv=0, which un-caps
        // every `MIN(fixed, eff_wac)` tranche whose fixed rate sits in that band.
        // This is a CASH path, not disclosure: eff_wac is the net-WAC cap exposed
        // to the rules engine (RulesHost.cs:101).
        var wac = 1200 * (periodCf.Interest - periodCf.ServiceFee - expenses) /
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
    ///     Deal-variable name of the adjusted collateral principal amount — the CLO
    ///     OC numerator. Supplied by harmony as a scheduled/deal variable when the
    ///     deal carries CCC-haircut / defaulted-asset adjustments (the engine stays
    ///     ratings-agnostic); absent, the OC numerator falls back to the period-end
    ///     collateral balance.
    /// </summary>
    private const string AcpaVariableName = "ACPA";

    /// <summary>
    ///     CLO per-level OC/IC coverage cascade — replaces the flat INTEREST step
    ///     when <see cref="IDeal.CoverageCascade" /> is configured. Per level, in
    ///     senior→junior order (mirrors the validated reference model,
    ///     graam-harmony clo/reference_model.py::forward_sim):
    ///     1. pay the level's note interest (the classes new at this level — the
    ///        Tranches lists are cumulative);
    ///     2. test OC(L) = numerator / Σ BALANCE(level's Tranches) ×100 vs the
    ///        level's ocTriggerPct, and IC(L) = period collateral interest
    ///        collected / period interest due on the level's Tranches ×100 vs
    ///        icTriggerPct (IC ratios are computed up-front, before any note
    ///        interest or diversion moves, like the reference model's ic_now);
    ///     3. on a failing test, divert available interest — up to the cure
    ///        amount max(0, Σ BALANCE − numerator/(ocTriggerPct/100)) for an OC
    ///        failure, or the remaining-interest sweep for an IC-only failure —
    ///        to SEQUENTIAL senior-first principal paydown of the note stack,
    ///        before any junior level's interest.
    ///     Remaining interest then continues down the normal interest structure
    ///     (junior/sub interest, residual sweep) in structure order.
    ///     Per-level results are recorded as trigger results ("OC_{level}" /
    ///     "IC_{level}") and the period's total diversion in the
    ///     "coverage_diverted" variable. Collateral interest treatment only (no
    ///     reserve draw / no Guaranteed top-up — CLO notes are unguaranteed).
    /// </summary>
    private double PayCoverageCascadeInterestStep(IDeal deal, DynamicGroup dynGroup,
        IRateProvider rateProvider, PeriodCashflows periodCf, double availableInterest,
        List<DynamicTranche> allTranches)
    {
        var cascade = deal.CoverageCascade!;
        var cfDate = periodCf.CashflowDate;

        // Resolve each level's classes once, failing loudly on an unknown name.
        var levelClasses = new List<List<DynamicClass>>(cascade.Count);
        foreach (var level in cascade)
        {
            var classes = new List<DynamicClass>();
            foreach (var name in level.Tranches)
            {
                var cls = dynGroup.ClassByName(name);
                if (cls == null)
                    throw new DealModelingException(deal.DealName,
                        $"CoverageCascade level '{level.Level}' references unknown class '{name}'.");
                if (!classes.Contains(cls))
                    classes.Add(cls);
            }

            levelClasses.Add(classes);
        }

        // OC numerator: the ACPA scheduled/deal variable when the deal carries one
        // (per-date), else the Collateral Principal Amount — the period-end
        // collateral balance PLUS this period's principal collections (scheduled,
        // unscheduled, recoveries). Indentures include principal proceeds held in
        // the collection account, and the cash is real: it redeems notes later
        // this same payment date. Without it the test understates coverage by one
        // period's collections all deal long and reads 0% on the final payment
        // date (the whole pool is principal cash by then), spuriously diverting
        // the junior classes' last coupon to equity's benefit (#67).
        var numerator = DealCarriesVariable(deal, AcpaVariableName)
            ? dynGroup.GetVariable(AcpaVariableName, cfDate)
            : periodCf.Balance + periodCf.ScheduledPrincipal
              + periodCf.UnscheduledPrincipal + periodCf.RecoveryPrincipal;

        // IC ratios up-front: numerator is the period collateral interest collected
        // (net of fees/expenses paid senior to the notes — the funds entering this
        // step), denominator the level set's interest due this period.
        var interestCollected = availableInterest;
        var icRatioPct = new double?[cascade.Count];
        for (var i = 0; i < cascade.Count; i++)
        {
            if (!cascade[i].IcTriggerPct.HasValue)
                continue;
            var due = levelClasses[i].Sum(c => c.InterestDue(cfDate, rateProvider, allTranches));
            icRatioPct[i] = due > 0.005 ? interestCollected / due * 100.0 : double.PositiveInfinity;
        }

        // Cure diversions pay the full note stack sequentially, senior-first — the
        // most junior level's (cumulative) class list is that stack in order.
        var noteStack = new SequentialStructure(levelClasses[^1].Cast<IPayable>().ToList());

        var paidClasses = new HashSet<DynamicClass>();
        var totalDiverted = 0.0;

        for (var i = 0; i < cascade.Count; i++)
        {
            var level = cascade[i];

            // 1) This level's interest: the classes not already paid at a senior
            // level. Pass all remaining funds — a coupon class takes only its due
            // (DynamicClass.PayInterest), and with no funds it books the unpaid
            // coupon as a shortfall (#58 parity with SequentialStructure).
            foreach (var cls in levelClasses[i])
            {
                if (!paidClasses.Add(cls) || cls.IsLockedOut(cfDate))
                    continue;
                var funds = availableInterest < 0.01 ? 0 : availableInterest;
                availableInterest -= cls.PayInterest(null, cfDate, funds, rateProvider, allTranches);
            }

            // 2) Test coverage at this level (current balances — senior levels'
            // diversions this period have already paid the stack down).
            var denom = levelClasses[i].Sum(c => c.Balance);
            var ocRatioPct = denom > 0.005 ? numerator / denom * 100.0 : double.PositiveInfinity;
            var ocFail = level.OcTriggerPct.HasValue && ocRatioPct < level.OcTriggerPct.Value;
            var icFail = level.IcTriggerPct.HasValue && icRatioPct[i] < level.IcTriggerPct.Value;

            if (level.OcTriggerPct.HasValue)
                dynGroup.AddTriggerResult(cfDate, $"OC_{level.Level}",
                    ocRatioPct, level.OcTriggerPct.Value, !ocFail);
            if (level.IcTriggerPct.HasValue)
                dynGroup.AddTriggerResult(cfDate, $"IC_{level.Level}",
                    icRatioPct[i]!.Value, level.IcTriggerPct.Value, !icFail);

            if (!ocFail && !icFail)
                continue;

            // 3) Cure: an OC failure needs exactly the paydown that restores
            // OC(L) to its trigger; an IC-only failure sweeps the remaining
            // interest (reference-model parity — IC cures by deleveraging, so all
            // available interest turns to principal until the test passes).
            var need = ocFail
                ? Math.Max(0.0, denom - numerator / (level.OcTriggerPct!.Value / 100.0))
                : availableInterest;
            var cure = Math.Min(Math.Max(availableInterest, 0.0), need);
            if (cure <= 0.005)
                continue;

            var noteBalBefore = dynGroup.Balance();
            noteStack.PaySp(null, cfDate, cure, () => { });
            var diverted = noteBalBefore - dynGroup.Balance();
            availableInterest -= diverted;
            totalDiverted += diverted;
        }

        dynGroup.SetVariable("coverage_diverted", totalDiverted);

        // 4) Remaining interest continues down the normal interest structure — the
        // classes below the cascade (junior/sub interest, residual sweep), in
        // structure order, skipping the cascade classes already paid above.
        if (dynGroup.InterestPayable != null)
            availableInterest = PayInterestSkippingPaid(dynGroup.InterestPayable, paidClasses,
                cfDate, availableInterest, rateProvider, allTranches);

        return Math.Max(availableInterest, 0.0);
    }

    /// <summary>
    ///     Pays interest through <paramref name="payable" /> while skipping classes
    ///     the coverage cascade already paid. A container none of whose leaf
    ///     classes was cascade-paid pays natively — preserving its own semantics
    ///     (e.g. PRORATA scaling on a shortfall); a container holding cascade
    ///     classes is descended in order so only the unpaid remainder pays.
    /// </summary>
    private static double PayInterestSkippingPaid(IPayable payable, HashSet<DynamicClass> paidClasses,
        DateTime cfDate, double availableInterest, IRateProvider rateProvider,
        List<DynamicTranche> allTranches)
    {
        if (payable is DynamicClass cls)
        {
            if (paidClasses.Contains(cls) || cls.IsLockedOut(cfDate))
                return availableInterest;
            var funds = availableInterest < 0.01 ? 0 : availableInterest;
            return availableInterest - cls.PayInterest(null, cfDate, funds, rateProvider, allTranches);
        }

        if (!payable.Leafs().OfType<DynamicClass>().Any(paidClasses.Contains))
        {
            var funds = availableInterest < 0.01 ? 0 : availableInterest;
            return availableInterest - payable.PayInterest(null, cfDate, funds, rateProvider, allTranches);
        }

        foreach (var child in payable.GetChildren() ?? new List<IPayable>())
            availableInterest = PayInterestSkippingPaid(child, paidClasses, cfDate,
                availableInterest, rateProvider, allTranches);
        return availableInterest;
    }

    /// <summary>
    ///     True when the deal defines <paramref name="varName" /> as a deal
    ///     variable or scheduled variable — mirroring the lookup order (and
    ///     casing rules) of <see cref="DynamicGroup.GetVariableObj" />, which
    ///     otherwise silently returns 0 for a missing variable.
    /// </summary>
    private static bool DealCarriesVariable(IDeal deal, string varName)
    {
        return deal.DealVariables.Any(v =>
                   v.VariableName.Equals(varName, StringComparison.InvariantCultureIgnoreCase)) ||
               deal.ScheduledVariables.Any(sv =>
                   sv.ScheduleVariableName.Equals(varName, StringComparison.InvariantCulture));
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
        if (index >= intChildren.Count)
            return availableInterest;

        var child = intChildren[index];

        // Out of funds at this seniority level. Walk the child anyway so its classes
        // book their unpaid coupon as a shortfall (#58) — but take no reserve draw,
        // which leaves the cash outcome exactly as it was when the level was skipped.
        if (availableInterest < 0.01)
        {
            child.PayInterest(null, periodCf.CashflowDate, 0, rateProvider, allTranches);
            return availableInterest;
        }

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
    private void PayWritedownStep(DynamicGroup dynGroup, PeriodCashflows periodCf, double writedownAmt,
        bool excessSpreadFirstLoss)
    {
        if (writedownAmt <= 0 || dynGroup.WritedownPayable == null)
            return;

        // Excess-spread first-loss: an ExcessInterest (XS / monthly-excess-cashflow) strip
        // absorbs the period loss out of the excess spread it swept this period, BEFORE any
        // funded bond is written down. XS has no principal (its notional balance is reset to
        // the pool each period), so it can only absorb via excess spread; only the shortfall
        // (loss beyond this period's excess spread) cascades to the funded bonds below.
        // Driven by the ExcessInterest TYPE — no writedown-structure placement required — and
        // gated by excessSpreadFirstLoss so OC/turbo deals defer to that path.
        if (excessSpreadFirstLoss)
        {
            var lossBeforeAbsorb = writedownAmt;
            writedownAmt = AbsorbLossFromExcessSpread(dynGroup, periodCf, writedownAmt);

            // Absorbing a loss out of excess spread is not a write-off of the loss — it is the
            // excess-spread strip funding the recovery on the bonds' behalf ("XS increases
            // recoveries"). The absorbed cash must therefore pay down bond principal, so the
            // liability side declines with the collateral. Without this, the collateral shrinks
            // by the full defaulted principal while the notes only shrink by (recovery + the
            // shortfall write-down), leaving the notes permanently over the pool and unable to
            // pay off or write down at maturity. Route it through the recovery waterfall so it
            // amortizes the notes sequentially, building credit support beneath the juniors.
            var absorbed = lossBeforeAbsorb - writedownAmt;
            if (absorbed > 0.005)
            {
                var principalPayable = dynGroup.RecoveryPayable ?? dynGroup.PrepayPayable;
                principalPayable?.PayRp(null, periodCf.CashflowDate, absorbed, () => { });
            }

            if (writedownAmt <= 0.005)
                return;
        }

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
    /// Absorb the period loss out of the excess spread swept by any ExcessInterest
    /// (XS) class in the group — the excess-spread first-loss layer, identified by
    /// TYPE (not by writedown-structure placement). Reduces the XS class's recorded
    /// interest for the period by the loss it absorbs (its cash shrinks with losses)
    /// and returns the shortfall — the loss beyond this period's excess spread —
    /// which the caller then cascades to the funded bonds. When the group has no
    /// ExcessInterest strip the loss passes through unchanged.
    /// </summary>
    private double AbsorbLossFromExcessSpread(DynamicGroup dynGroup, PeriodCashflows periodCf, double loss)
    {
        foreach (var leaf in dynGroup.DynamicClasses.Where(dc => dc.IsExcessInterest))
        {
            if (loss <= 0.005)
                break;

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
        // An EXCESS step's structure lands in ExcessPayable (SET_EXCESS_STRUCT),
        // an EXCESS_RELEASE step's in ReleasePayable (SET_RELEASE_STRUCT) — but both
        // step types execute HERE. Reading only ReleasePayable meant a deal whose
        // waterfall declared EXCESS(SINGLE(Sub)) with no Certificate class silently
        // DESTROYED the residual interest every period: recipients resolved empty
        // and availableInterest was zeroed by the caller (graam-flows#68, found on
        // the CLO-native path where the equity tranche is a plain note class).
        var recipients = ReleaseRecipientsInOrder(dynGroup.ReleasePayable ?? dynGroup.ExcessPayable).ToList();
        if (!recipients.Any())
            // No release/excess structure — fall back to the OC certificate(s).
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
