using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.RulesEngine;
using GraamFlows.Triggers;
using GraamFlows.Util;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures;

public abstract class BaseStructure : IWaterfall
{
    public abstract DealCashflows Waterfall(IDeal deal, IRateProvider rateProvider, DateTime firstProjectionDate,
        CollateralCashflows cashflows, IAssumptionMill assumps, ITrancheAllocator trancheAllocator);

    public abstract List<InputField> GetInputs(IDeal deal);

    internal void PaySequentialClass(DynamicGroup dynamicGroup, IEnumerable<DynamicClass> dynamicClasses,
        DateTime cashflowDate, double unschedPrin, double schedPrin)
    {
        var dynamicClassesList = dynamicClasses as IList<DynamicClass> ?? dynamicClasses.ToList();
        if (!dynamicClassesList.Any())
        {
            if (unschedPrin + schedPrin > 100)
                throw new ArgumentException(
                    $"Attempting to pay class with {schedPrin + unschedPrin} but there are no classes to pay");
            return;
        }

        var totBal = dynamicClassesList.Where(dc => dc.DealStructure.ExchangableTranche == null).Sum(dc => dc.Balance);
        var totOrigBal = dynamicClassesList.Where(dc => dc.DealStructure.ExchangableTranche == null)
            .Sum(dc => dc.Tranche.OriginalBalance);

        double waterfallFactor = 1;
        if (unschedPrin + schedPrin > totBal)
            waterfallFactor = totBal / (unschedPrin + schedPrin);

        foreach (var dynamicClass in dynamicClassesList.Where(dc => !dc.IsExchangable()))
        {
            var pmtFactor = 1.0;
            if (dynamicClass.DealStructure.ExchangableTranche == null)
                pmtFactor = dynamicClass.Tranche.OriginalBalance * dynamicClass.Tranche.Factor /
                            dynamicClassesList.Sum(c => c.Tranche.OriginalBalance * c.Tranche.Factor);
            var unschedPrinToPay = unschedPrin * pmtFactor * waterfallFactor;
            var schedPrinToPay = schedPrin * pmtFactor * waterfallFactor;
            dynamicClass.Pay(cashflowDate, unschedPrinToPay, schedPrinToPay);
            PayPseudoClass(dynamicClass, cashflowDate, unschedPrinToPay, schedPrinToPay);
            PayExchClass(dynamicGroup, dynamicClassesList, dynamicClass, cashflowDate, schedPrinToPay,
                unschedPrinToPay);
        }

        if (waterfallFactor < 1)
            PaySequentialClass(dynamicGroup, dynamicGroup.SeniorSequentialClass(), cashflowDate,
                unschedPrin * (1 - waterfallFactor), schedPrin * (1 - waterfallFactor));
    }

    protected void PayNotionalClasses(DateTime cashflowDate, IEnumerable<DynamicGroup> dynGroups,
        IEnumerable<PeriodCashflows> periodCfs)
    {
        foreach (var dynGroup in dynGroups)
        {
            var notionalClasses = dynGroup.DynamicClasses
                .Where(dc => dc.DealStructure?.PayFromEnum == PayFromEnum.Notional).ToList();
            foreach (var notionalClass in notionalClasses)
            {
                var notionals = new List<string>();
                if (notionalClass.DealStructure.ExchangableTranche != null)
                    notionals.AddRange(notionalClass.DealStructure.ExchangableTranche.Split(','));
                notionals = notionals.Distinct().ToList();

                var propClasses = notionals
                    .SelectMany(nc => dynGroups.Select(dg => dg.ClassByName(nc.Trim())).Where(dc => dc != null))
                    .ToList();
                var proportion = notionalClass.Tranche.OriginalBalance /
                                 propClasses.Sum(pc => pc.Tranche.OriginalBalance);

                if (double.IsNaN(proportion) || double.IsInfinity(proportion))
                    proportion = 0;

                var usp = propClasses.Sum(pc => GetExchangeShare(pc.DynamicGroup, notionalClass, pc,
                    pc.GetCashflow(cashflowDate).UnscheduledPrincipal));
                var sp = propClasses.Sum(pc => GetExchangeShare(pc.DynamicGroup, notionalClass, pc,
                    pc.GetCashflow(cashflowDate).ScheduledPrincipal));
                var wd = propClasses.Sum(pc =>
                    GetExchangeShare(pc.DynamicGroup, notionalClass, pc, pc.GetCashflow(cashflowDate).Writedown));

                notionalClass.Pay(cashflowDate, usp * proportion, sp * proportion);
                notionalClass.Writedown(cashflowDate, wd * proportion);

                // pay notionals from exchange shares
                var exchShares = notionalClass.DynamicGroup.Deal.ExchShares
                    .Where(e => e.ClassGroupName == notionalClass.Tranche.TrancheName).ToList();
                foreach (var exchShare in exchShares)
                    if (exchShare.TrancheName.StartsWith("GROUP"))
                    {
                        var origGroupBal = dynGroup.DealClasses.Sum(dc => dc.Tranche.OriginalBalance);
                        var prop = exchShare.Quantity / origGroupBal;
                        var periodCf = periodCfs.SingleOrDefault(pcf => pcf.GroupNum == dynGroup.GroupNum);
                        if (periodCf == null)
                            continue;

                        notionalClass.Pay(cashflowDate,
                            (periodCf.UnscheduledPrincipal + periodCf.RecoveryPrincipal) * prop,
                            periodCf.ScheduledPrincipal * prop);
                        notionalClass.Writedown(cashflowDate, periodCf.CollateralLoss * prop);
                    }
                    else
                    {
                        var exShareClasses = dynGroup.ClassesByNameOrTag(exchShare.TrancheName);
                        if (!exShareClasses.Any())
                            continue;

                        var prop = exchShare.Quantity / dynGroups
                            .SelectMany(dg => dg.ClassesByNameOrTag(exchShare.TrancheName))
                            .Sum(dg => dg.Tranche.OriginalBalance);
                        var exchCf = exShareClasses.Select(ex => ex.GetCashflow(cashflowDate)).ToList();
                        notionalClass.Pay(cashflowDate, exchCf.Sum(cf => cf.UnscheduledPrincipal) * prop,
                            exchCf.Sum(cf => cf.ScheduledPrincipal) * prop);
                        notionalClass.Writedown(cashflowDate, exchCf.Sum(cf => cf.Writedown) * prop);
                    }

                // pay notionals off of groups
                var groupNotionals = notionals.Where(n => n.ToUpper().StartsWith("GROUP"))
                    .Select(a => a.Replace("GROUP_", ""));
                foreach (var groupNotional in groupNotionals)
                {
                    var periodCf = periodCfs.SingleOrDefault(pcf => pcf.GroupNum == groupNotional);
                    if (periodCf == null)
                        continue;

                    proportion = notionalClass.BeginBalance(cashflowDate) / periodCf.BeginBalance;
                    if (double.IsNaN(proportion) || double.IsInfinity(proportion))
                        proportion = 0;

                    notionalClass.Pay(cashflowDate,
                        (periodCf.UnscheduledPrincipal + periodCf.RecoveryPrincipal) * proportion,
                        periodCf.ScheduledPrincipal * proportion);
                    notionalClass.Writedown(cashflowDate, periodCf.CollateralLoss * proportion);
                }
            }
        }
    }

    protected void PayExchangeables(DateTime cashflowDate, IEnumerable<DynamicGroup> dynGroups,
        IEnumerable<PeriodCashflows> periodCfs, out IList<DynamicClass> payFromAllocator,
        IRateProvider rateProvider = null)
    {
        var payFromAllocatorSet = new HashSet<DynamicClass>();

        foreach (var dynGroup in dynGroups)
        {
            var periodCf = periodCfs.SingleOrDefault(p => p.GroupNum == dynGroup.GroupNum);
            if (periodCf == null)
                continue;

            if (periodCf.BeginBalance < .01)
                continue;

            // ONE ordered walk (#73). An exchange class is settled from the TRANCHES it names,
            // after every tranche it depends on already carries this period's cashflow.
            var exchClasses = dynGroup.DynamicClasses
                .Where(dc => dc.DealStructure?.PayFromEnum == PayFromEnum.Exchange)
                .ToList();

            foreach (var exchClass in OrderExchangeClassesByDependency(dynGroup, exchClasses))
            {
                var components = ExchangeComponentsOf(dynGroup, exchClass);
                if (components == null || components.Count == 0)
                {
                    // Nothing resolvable to settle from — hand it to the allocator rather than
                    // paying it a fraction of itself.
                    payFromAllocatorSet.Add(exchClass);
                    continue;
                }

                double usp = 0, sp = 0, wd = 0;
                foreach (var (tran, prop) in components)
                {
                    var ccf = tran.GetCashflow(cashflowDate);
                    if (ccf == null)
                        continue;
                    usp += ccf.UnscheduledPrincipal * prop;
                    sp += ccf.ScheduledPrincipal * prop;
                    wd += ccf.Writedown * prop;
                }

                if (sp + usp > exchClass.Balance)
                    exchClass.Pay(cashflowDate, exchClass.Balance, 0);
                else
                    exchClass.Pay(cashflowDate, usp, sp);

                exchClass.Writedown(cashflowDate, Math.Min(wd, exchClass.Balance));

                PayExchangeInterest(dynGroup, exchClass, components, cashflowDate, rateProvider);
            }

            // pay exchange off group
            var exchOffGroup = dynGroup.DynamicClasses.Where(dc => dc.DealStructure?.ExchangableTranche == null &&
                                                                   (dc.DealStructure?.PayFromEnum ==
                                                                    PayFromEnum.Group ||
                                                                    dc.DealStructure?.PayFromEnum ==
                                                                    PayFromEnum.ExcessServicing)).ToList();
            foreach (var groupExch in exchOffGroup)
                // classes off a group are typically IO's or excess servicing strips. We just need to factor down the class the same as the group. 
                if (groupExch.DealStructure.GroupNum != "0")
                {
                    var payDownFactor = periodCf.Balance / periodCf.BeginBalance;
                    var payDownAmt = groupExch.Balance - groupExch.Balance * payDownFactor;
                    groupExch.Pay(cashflowDate, 0, payDownAmt);
                }
                else
                {
                    groupExch.Pay(cashflowDate, 0, periodCf.BeginBalance - periodCf.Balance);
                }
        }

        payFromAllocator = payFromAllocatorSet.ToList();
    }

    protected void PayExchangeableStructures(DateTime cashflowDate, IEnumerable<PeriodCashflows> periodCfs,
        IEnumerable<DynamicGroup> dynGroups, PayRuleExecutor payRuleExecutor, List<TriggerValue> triggerValues)
    {
        // pay exchange structs
        foreach (var dynGroup in dynGroups)
        {
            var exchPayables = dynGroup.ExchPayables.ToList();
            foreach (var exchStruct in exchPayables)
            {
                var dynRemic = dynGroups.Select(c => c.ClassByName(exchStruct.Key)).Where(rem => rem != null).Distinct()
                    .Single();
                var cfRemic = dynRemic.GetCashflow(cashflowDate);
                var periodCf = periodCfs.SingleOrDefault(p => p.GroupNum == dynGroup.GroupNum);

                if (cfRemic.ScheduledPrincipal > 0)
                    exchStruct.Value.PaySp(null, cashflowDate, cfRemic.ScheduledPrincipal,
                        () => ExecutePayRules(dynGroup.Deal, dynGroup, payRuleExecutor, triggerValues, periodCf));

                if (cfRemic.UnscheduledPrincipal > 0)
                    exchStruct.Value.PayUsp(null, cashflowDate, cfRemic.UnscheduledPrincipal,
                        () => ExecutePayRules(dynGroup.Deal, dynGroup, payRuleExecutor, triggerValues, periodCf));
            }
        }
    }

    /// <summary>
    ///     The TRANCHES an exchange class is made of, each with its constant proportion of that
    ///     tranche's original face. Null when a declared component cannot be resolved.
    /// </summary>
    /// <remarks>
    ///     A component names a TRANCHE, not a class group (#73) — the document says so, and
    ///     resolving to a class made the exchange wrong in two directions: a class holding a
    ///     strip summed the funded coupon PLUS the strip carved out of it (class M-2A read
    ///     312,822.64 + 40,906.25, so Class M-2 was handed 707,457.78 where M2A + M2B is
    ///     625,645.28), and a component naming a strip resolved to the strip's CONTAINING class
    ///     and dragged in its principal.
    ///
    ///     A class holds more than one tranche only because a strip's NOTIONAL tracks its
    ///     parent — a different relationship, with no business in exchange resolution. A class
    ///     with several tranches is expressible as several components, so naming tranches is
    ///     strictly more expressive; and "M2B" names both a class and a tranche here, so a name
    ///     alone cannot disambiguate.
    ///
    ///     Proportions are constant fractions of the ORIGINAL face (the original NOTIONAL for an
    ///     interest-only tranche), which is the prospectus's own definition. A class declaring
    ///     components only through `ExchangableTranche` takes 100% of each, which is what the
    ///     absent-share default has always meant.
    /// </remarks>
    private static List<(DynamicTranche Tranche, double Proportion)> ExchangeComponentsOf(
        DynamicGroup dynGroup, DynamicClass exchClass)
    {
        var all = dynGroup.DynamicClasses.SelectMany(dc => dc.DynamicTranches).ToList();

        DynamicTranche ByName(string name) => all.FirstOrDefault(t =>
            string.Equals(t.Tranche.TrancheName, name, StringComparison.OrdinalIgnoreCase));

        var declared = dynGroup.Deal.ExchShares?
            .Where(es => string.Equals(es.ClassGroupName, exchClass.Tranche.TrancheName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        var outp = new List<(DynamicTranche, double)>();

        if (declared is { Count: > 0 })
        {
            foreach (var es in declared)
            {
                var tran = ByName(es.TrancheName);
                if (tran == null || Math.Abs(tran.Tranche.OriginalBalance) < 0.01)
                    return null;
                outp.Add((tran, es.Quantity / tran.Tranche.OriginalBalance));
            }

            return outp;
        }

        if (string.IsNullOrWhiteSpace(exchClass.DealStructure?.ExchangableTranche))
            return null;

        foreach (var name in exchClass.DealStructure.ExchangableTranche.Split(
                     ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var tran = ByName(name);
            if (tran == null)
                return null;
            outp.Add((tran, 1.0));
        }

        return outp;
    }

    /// <summary>
    ///     Exchange classes in settlement order: each comes after the classes its components
    ///     belong to.
    /// </summary>
    /// <remarks>
    ///     An exchange can be OF an exchange — Class M-2I's component is Class M-2, itself
    ///     received for M-2A + M-2B — so declaration order settles some classes from components
    ///     that have not been paid. This was a hand-rolled one-level deferral (a
    ///     `nestedExchClasses` set and a second loop), which handles one hop and no more. The
    ///     dependency is a DAG; sorting it is the general form and costs less code.
    ///
    ///     A cycle is a modelling error, not something to resolve: the classes in it are emitted
    ///     last, so they reach the caller's allocator rather than settling from a half-computed
    ///     component.
    /// </remarks>
    private static List<DynamicClass> OrderExchangeClassesByDependency(
        DynamicGroup dynGroup, List<DynamicClass> exchClasses)
    {
        var byTrancheName = new Dictionary<string, DynamicClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var dc in exchClasses)
        foreach (var t in dc.DynamicTranches)
            byTrancheName.TryAdd(t.Tranche.TrancheName, dc);

        var ordered = new List<DynamicClass>();
        var state = new Dictionary<DynamicClass, int>();

        void Visit(DynamicClass dc)
        {
            if (state.ContainsKey(dc))
                return;
            state[dc] = 1;

            var components = ExchangeComponentsOf(dynGroup, dc);
            if (components != null)
                foreach (var (tran, _) in components)
                    if (byTrancheName.TryGetValue(tran.Tranche.TrancheName, out var dep) && dep != dc)
                        Visit(dep);

            state[dc] = 2;
            ordered.Add(dc);
        }

        foreach (var dc in exchClasses)
            Visit(dc);

        foreach (var dc in exchClasses)
            if (!ordered.Contains(dc))
                ordered.Add(dc);

        return ordered;
    }

    private double GetExchangeShare(DynamicGroup dynGroup, DynamicClass exchClass, DynamicClass parentClass,
        double prin)
    {
        if (dynGroup.Deal.ExchShares == null)
            return prin;
        var exchShare = dynGroup.Deal.ExchShares.SingleOrDefault(es =>
            es.ClassGroupName == exchClass.Tranche.TrancheName && es.TrancheName == parentClass.Tranche.TrancheName);
        if (exchShare == null)
            return prin;

        var pctShare = exchShare.Quantity / parentClass.Tranche.OriginalBalance;
        var cashflow = prin * pctShare;
        return cashflow;
    }

    /// <summary>
    ///     An exchangeable (combined / recombinable / MACR) class receives the
    ///     proportional sum of its component tranches' interest — the same
    ///     exchange-share basis used for principal — because a holder of the
    ///     combined class holds slices of each underlying and collects each
    ///     underlying's coupon. Interest is credited to the exchange class's own
    ///     tranche cashflow(s) (balance-weighted across them, even split at zero
    ///     balance) so the output reads it directly.
    /// </summary>
    private void PayExchangeInterest(DynamicGroup dynGroup, DynamicClass exchClass,
        IReadOnlyList<(DynamicTranche Tranche, double Proportion)> components, DateTime cashflowDate,
        IRateProvider rateProvider = null)
    {
        // A class that states its OWN coupon accrues from it, and does not receive its
        // components' interest passed through (#4572).
        //
        // The pass-through below is right for a COMBINATION exchange — Class M-2 IS M-2A plus
        // M-2B, and its holder collects each underlying's coupon. It is wrong for a
        // coupon-STRIPPING exchange, where the received class carries a lower stated coupon and
        // the stripped margin goes to a separate interest-only class. On STACR 2025-DNA1,
        // M-2R/S/T/U state four different coupons (SOFR+0.60/0.75/0.90/1.05) and every one of
        // them was handed the identical pass-through amount — their coupons were never read, so
        // the cashflow reported `Coupon: 0`. Worse, the strip was ADDED where it should be
        // subtracted: M-2AR came out at M-2A + M-2AI when it is M-2A minus M-2AI.
        //
        // Accruing from the stated coupon also restores the invariant the document is built on:
        // received equals surrendered. M-2R + M-2I now sums to M-2A + M-2B, because the
        // published coupons are designed to. The pass-through summed to 1.696x that.

        // Interest is credited to the component's DynamicTranche cashflows, not the
        // DynamicClass wrapper (which aggregates principal but not interest), so read
        // it from the tranches before applying the exchange share.
        // What the COMPONENT TRANCHES were actually paid, at their stated proportions (#73).
        var interest = components.Sum(c =>
        {
            var ccf = c.Tranche.GetCashflow(cashflowDate);
            return ccf == null ? 0 : ccf.Interest * c.Proportion;
        });
        if (Math.Abs(interest) < 1e-9)
            return;

        // Split what the components RECEIVED by this class's stated coupon, when it states one.
        if (rateProvider != null &&
            SplitExchangeInterestByStatedCoupon(exchClass, components, cashflowDate, rateProvider,
                interest))
            return;

        var tranches = exchClass.DynamicTranches;
        if (tranches == null || tranches.Count == 0)
            return;

        var totalBal = tranches.Sum(t => t.GetCashflow(cashflowDate).BeginBalance);
        foreach (var dynTran in tranches)
        {
            var cf = dynTran.GetCashflow(cashflowDate);
            cf.Interest += totalBal > 0.01
                ? interest * (cf.BeginBalance / totalBal)
                : interest / tranches.Count;
        }
    }

    /// <summary>
    ///     Split the interest the components ACTUALLY received across the received class by its
    ///     own stated coupon. Returns false when it states none, leaving the caller's straight
    ///     pass-through in place (#4572).
    /// </summary>
    private static bool SplitExchangeInterestByStatedCoupon(DynamicClass exchClass,
        IReadOnlyList<(DynamicTranche Tranche, double Proportion)> components, DateTime cashflowDate,
        IRateProvider rateProvider, double receivedInterest)
    {
        var tranches = exchClass.DynamicTranches;
        if (tranches == null || tranches.Count == 0)
            return false;

        // ONLY a coupon the document STATES for this class — Fixed or Floating. A `Formula`
        // (`eff_wac`) or `TrancheWac` coupon is DERIVED from the deal rather than stated against
        // the class, so it carries no independent economics and the straight pass-through is
        // still right. `ExchangeClass_MirrorsSumOfComponents_PrincipalAndInterest` caught the
        // first version of this fix for exactly that.
        foreach (var dynTran in tranches)
        {
            var kind = dynTran.Tranche.CouponTypeEnum;
            if (kind != CouponType.Fixed && kind != CouponType.Floating)
                return false;
        }

        var coupons = new List<double>(tranches.Count);
        var ownWeight = 0.0;
        foreach (var dynTran in tranches)
        {
            var coupon = dynTran.Coupon(rateProvider, cashflowDate, tranches);
            if (double.IsNaN(coupon) || double.IsInfinity(coupon) || Math.Abs(coupon) < 1e-9)
                return false;
            coupons.Add(coupon);
            ownWeight += dynTran.TrancheBalance(dynTran.GetCashflow(cashflowDate)) * coupon;
        }

        // The components' own coupon-weighted balance. The ratio of the two is the share of the
        // period's interest this class's stated coupon entitles it to.
        // The COMPONENTS' coupon-weighted balance, on the same basis and at the same
        // proportions the received interest was summed at. Both sides must use one basis.
        var parentWeight = 0.0;
        foreach (var c in components)
        {
            var pcf = c.Tranche.GetCashflow(cashflowDate);
            if (pcf == null) continue;
            parentWeight += c.Tranche.TrancheBalance(pcf) *
                            c.Tranche.Coupon(rateProvider, cashflowDate, tranches) * c.Proportion;
        }

        if (Math.Abs(parentWeight) < 1e-9 || ownWeight <= 0)
            return false;

        // CONSERVATION IS INHERITED, NOT RE-DERIVED. `receivedInterest` is what the components
        // were actually paid, so a shortfall is already baked into it and this only decides how
        // it is SHARED — pro-rata by stated coupon. An earlier version accrued the class's own
        // coupon independently instead, which paid an exchangeable MORE than the deal collected
        // (1.85x on an uneven shortfall) because it replaced the pass-through's built-in
        // guarantee with nothing. The two shortfall tests exist to keep that from coming back.
        //
        // Note the year fraction cancels: both weights are balance x coupon over the SAME
        // period, so the accrual calendar never enters. That also removes the 30/34 stub
        // discrepancy an absolute accrual had to work around.
        var share = ownWeight / parentWeight;

        // Distribute WITHIN the class by the same coupon weight the share was computed from.
        // Balance-weighting would be inconsistent, and it matters: a class can hold tranches
        // with different coupons — harmony wires a MACR interest-only strip into its P&I
        // sibling's class (harmony#4586), so class M-2 holds M-2 at SOFR+1.35 and M-2I at
        // 0.75. Splitting that by balance hands each half the interest; splitting by coupon
        // weight gives the P&I class its coupon and the strip its own.
        for (var i = 0; i < tranches.Count; i++)
        {
            var dynTran = tranches[i];
            var cf = dynTran.GetCashflow(cashflowDate);
            var w = dynTran.TrancheBalance(cf) * coupons[i];
            cf.Interest += receivedInterest * share *
                           (ownWeight > 1e-9 ? w / ownWeight : 1.0 / tranches.Count);
            cf.Coupon = coupons[i];
            cf.EffectiveCoupon = coupons[i];
        }

        return true;
    }

    public void ExecutePayRules(IDeal deal, DynamicGroup dynGroup, IPayRuleExecutor payRuleExecutor,
        List<TriggerValue> triggerValues, PeriodCashflows adjPeriodCf)
    {
        dynGroup.ResetLockedOutClasses(adjPeriodCf.CashflowDate);
        foreach (var payRule in deal.PayRules.OrderBy(pr => pr.RuleExecutionOrder))
        {
            if (payRule.ClassGroupName.StartsWith("GROUP_"))
            {
                var ruleGroupNum = payRule.ClassGroupName.Replace("GROUP_", "");
                if (ruleGroupNum != "0" && ruleGroupNum != dynGroup.GroupNum)
                    continue;
            }

            // TODO: summing the balance for every rule is expensive. Need to figure out how to avoid it.
            if (dynGroup.Balance() <= 0)
                break;

            var ruleCfAlloc = payRuleExecutor.ExecutePayRule(payRule, triggerValues, dynGroup, adjPeriodCf);
            var totalCf = ruleCfAlloc.PrepayPrin + ruleCfAlloc.RecovPrin + ruleCfAlloc.SchedPrin;

            if (Math.Abs(totalCf) > .01) adjPeriodCf.DebitPrin(totalCf);
        }
    }

    internal void PayAccrualAndAccretionAccrualPhase(DateTime cfDate, DynamicGroup dynGroup,
        DynamicClass accretionClass, DynamicClass accrualClass, double accuralAmt)
    {
        var accAmt = accuralAmt;
        if (accretionClass != null)
        {
            accAmt = Math.Min(accuralAmt, accretionClass.Balance);
            accretionClass.Pay(cfDate, 0, accAmt);
        }

        accrualClass.Pay(cfDate, 0, -accAmt);

        if (accretionClass == null)
        {
            var accPayable = dynGroup.GetAccrualPayable(accrualClass.Tranche.TrancheName);
            if (accPayable != null)
                accPayable.PaySp(null, cfDate, accAmt, () => { });
            else if (dynGroup.AccrualPayable != null)
                dynGroup.AccrualPayable.PaySp(null, cfDate, accAmt, () => { });
            else if (dynGroup.ScheduledPayable != null)
                dynGroup.ScheduledPayable.PaySp(null, cfDate, accAmt, () => { });
            else
                throw new DealModelingException(dynGroup.Deal.DealName,
                    "Unable to distribute accrual to accretion classes. Accruals must be modeled with accretion classes.");
        }
    }

    private void PayExchClass(DynamicGroup dynamicGroup, IEnumerable<DynamicClass> dynamicClasses,
        DynamicClass parentClass, DateTime cashflowDate, double schedPrin, double unschedPrin)
    {
        var dynamicClassesList = dynamicClasses as IList<DynamicClass> ?? dynamicClasses.ToList();
        var exchToPay = dynamicClassesList.Where(dc =>
            dc.IsExchangable() && dc.DealStructure.ExchangableTranche == parentClass.Tranche.TrancheName);

        foreach (var exch in exchToPay)
        {
            var totalPrinToPay = unschedPrin + schedPrin;
            if (totalPrinToPay > exch.Balance)
            {
                var remainPrin = totalPrinToPay - exch.Balance;
                var exchSchedFactor = schedPrin / totalPrinToPay;
                var exchUnschedFactor = unschedPrin / totalPrinToPay;
                PayPseudoClass(exch, cashflowDate, exch.Balance * exchUnschedFactor, exch.Balance * exchSchedFactor);
                exch.Pay(cashflowDate, exch.Balance * exchUnschedFactor, exch.Balance * exchSchedFactor);
                var nextExchClass = dynamicGroup.NextExchangableClass(parentClass);
                PayExchClass(dynamicGroup, nextExchClass, parentClass, cashflowDate, remainPrin * exchSchedFactor,
                    remainPrin * exchUnschedFactor);
            }
            else
            {
                exch.Pay(cashflowDate, unschedPrin, schedPrin);
                PayPseudoClass(exch, cashflowDate, unschedPrin, schedPrin);
            }
        }
    }

    protected void WritedownClass(DynamicGroup dynamicGroup, IEnumerable<DynamicClass> dynamicClasses,
        DateTime cashflowDate, double writedownAmt)
    {
        var dynamicClassesList = dynamicClasses as IList<DynamicClass> ?? dynamicClasses.ToList();
        if (!dynamicClassesList.Any())
        {
            if (writedownAmt > 100)
                // Shouldn't end up in here but minor differences in collat balance vs tranche balance can cause this.
                if (dynamicGroup.BeginningBalance > 0 && writedownAmt / dynamicGroup.BeginningBalance > .00001)
                    throw new ArgumentException(
                        $"Attempting to write down class with {writedownAmt} but there are no classes to pay");
            return;
        }

        var totBal = dynamicClassesList.Where(dc => !dc.IsExchangable()).Sum(dc => dc.Balance);
        var totBeginBal = dynamicClassesList.Where(dc => !dc.IsExchangable()).Sum(dc => dc.BeginBalance(cashflowDate));

        double waterfallFactor = 1;
        if (writedownAmt > totBal)
            waterfallFactor = totBal / writedownAmt;

        foreach (var dynamicClass in dynamicClassesList.Where(dc => !dc.IsExchangable()))
        {
            var pmtFactor = 1.0;
            if (dynamicClass.DealStructure.ExchangableTranche == null)
                pmtFactor = dynamicClass.BeginBalance(cashflowDate) / totBeginBal;

            var adjWritedown = writedownAmt * pmtFactor * waterfallFactor;
            dynamicClass.Writedown(cashflowDate, adjWritedown);
            WritedownPseudoClass(dynamicClass, cashflowDate, adjWritedown);
            WritedownExchClass(dynamicGroup, dynamicClassesList, dynamicClass, cashflowDate, adjWritedown);
        }

        if (waterfallFactor < 1)
            WritedownClass(dynamicGroup, dynamicGroup.SubordinateClass(), cashflowDate,
                writedownAmt * (1 - waterfallFactor));
    }

    private void WritedownExchClass(DynamicGroup dynamicGroup, IEnumerable<DynamicClass> dynamicClasses,
        DynamicClass parentClass, DateTime cashflowDate, double writedownAmt)
    {
        var dynamicClassesList = dynamicClasses as IList<DynamicClass> ?? dynamicClasses.ToList();
        var exchToPay = dynamicClassesList.Where(dc => dc.IsExchangable() &&
                                                       dc.DealStructure.ExchangableTranche ==
                                                       parentClass.Tranche.TrancheName &&
                                                       dc.DealStructure.PayFromEnum != PayFromEnum.Exchange);

        foreach (var exch in exchToPay)
            if (writedownAmt > exch.Balance)
            {
                var remainWritedown = writedownAmt - exch.Balance;
                WritedownPseudoClass(exch, cashflowDate, exch.Balance);
                exch.Writedown(cashflowDate, exch.Balance);

                var nextExchClass = dynamicGroup.SubordinateExchangableClass(parentClass);
                WritedownExchClass(dynamicGroup, nextExchClass, parentClass, cashflowDate, remainWritedown);
            }
            else
            {
                exch.Writedown(cashflowDate, writedownAmt);
                WritedownPseudoClass(exch, cashflowDate, writedownAmt);
            }
    }

    protected void PayPseudoClass(DynamicClass dynClass, DateTime cashflowDate, double unsched, double sched)
    {
        var pseudoClasses = dynClass.DynamicGroup.ApplicablePseudoClasses(dynClass);
        foreach (var pseudoClass in pseudoClasses)
            pseudoClass.Pay(cashflowDate, unsched, sched);
    }

    protected void WritedownPseudoClass(DynamicClass dynClass, DateTime cashflowDate, double writedownAmt)
    {
        var pseudoClasses = dynClass.DynamicGroup.ApplicablePseudoClasses(dynClass);
        foreach (var pseudoClass in pseudoClasses)
            pseudoClass.Writedown(cashflowDate, writedownAmt);
    }

    public virtual double WritedownAmt(IDeal deal, DynamicGroup dynGroup, PeriodCashflows periodCf)
    {
        var writedownAmt = periodCf.DefaultedPrincipal - periodCf.RecoveryPrincipal;
        var forbWritedown = periodCf.ForbearanceLiquidated - periodCf.ForbearanceRecovery -
                            periodCf.ForbearanceUnscheduled;
        return writedownAmt + forbWritedown;
    }

    /// <summary>
    ///     The period's Modification Loss Amount — the net reduction in collateral interest
    ///     caused by modified Reference Obligations, taken straight off the row the caller posts.
    ///
    ///     Its own channel rather than a term folded into <see cref="WritedownAmt" />, because it
    ///     runs a DIFFERENT ladder: a Modification Loss Priority interleaves interest bites the
    ///     Tranche Write-down Priority has no notion of. The engine cannot derive this amount —
    ///     it is a function of each loan's Original and Current Accrual Rates, which live
    ///     upstream — so it arrives as an input, exactly as defaulted and recovered principal do.
    /// </summary>
    public virtual double ModificationLossAmt(IDeal deal, DynamicGroup dynGroup, PeriodCashflows periodCf)
    {
        return periodCf.ModificationLoss;
    }

    public virtual List<TriggerValue> TestTriggers(IList<ITrigger> triggers, DynamicGroup dynGroup,
        DateTime cashflowDate, PeriodCashflows periodCf)
    {
        if (triggers != null)
            return triggers.Select(trigger => trigger.TestTrigger(dynGroup, cashflowDate, periodCf))
                .Where(triggerValue => triggerValue != null).ToList();
        return new List<TriggerValue>();
    }

    public virtual List<TriggerValue> ExecuteTriggers(DynamicGroup dynGroup, IList<ITrigger> triggers,
        PeriodCashflows adjPeriodCf, IPayRuleExecutor payRuleExecutor)
    {
        var triggerValues = TestTriggers(triggers, dynGroup, adjPeriodCf.CashflowDate, adjPeriodCf);
        if (triggerValues != null && triggerValues.Any())
        {
            foreach (var triggerResult in triggerValues)
                dynGroup.AddTriggerResult(adjPeriodCf.CashflowDate, triggerResult.TriggerName,
                    triggerResult.NumericValue, triggerResult.RequiredValue, triggerResult.TriggerResult);

            foreach (var triggerValue in triggerValues.Where(trigger => trigger != null))
            {
                if (triggerValue.TriggerResultType == TriggerValueType.Executer)
                {
                    if (payRuleExecutor != null)
                        ExecutePayRules(dynGroup.Deal, dynGroup, payRuleExecutor, triggerValues, adjPeriodCf);
                    if (triggerValue.TriggerExecuter.TriggerExecType == TriggerExecutionType.Terminate)
                        ExecuteTermination(dynGroup, adjPeriodCf);

                    return triggerValues;
                }

                if (triggerValue.TriggerExecuter != null)
                    throw new Exception(
                        $"Trigger Executer {triggerValue.TriggerExecuter.TriggerExecType} is not known");
            }
        }

        return triggerValues;
    }

    /// <summary>
    /// Executes deal termination: writedown remaining losses, then pay off all tranche balances.
    /// </summary>
    protected void ExecuteTermination(DynamicGroup dynGroup, PeriodCashflows adjPeriodCf)
    {
        // At termination, writedown remaining losses then pay off all balances.
        // Unabsorbed writedowns are expected at termination (subordinates may already be zero).
        var writedown = WritedownAmt(dynGroup.Deal, dynGroup, adjPeriodCf);
        if (writedown > 0)
        {
            var subClasses = dynGroup.SubordinateClass().Where(dc => dc.Balance > 0).ToList();
            if (subClasses.Any())
            {
                var absorbable = subClasses.Sum(dc => dc.Balance);
                WritedownClass(dynGroup, subClasses, adjPeriodCf.CashflowDate, Math.Min(writedown, absorbable));
            }
        }

        foreach (var dynClass in dynGroup.DynamicClasses)
            if (dynClass.DealStructure == null ||
                dynClass.DealStructure.PayFromEnum != PayFromEnum.Exchange)
                dynClass.Pay(adjPeriodCf.CashflowDate, dynClass.Balance, 0);
    }

    protected virtual PeriodCashflows AdjustPeriodCashflows(DynamicGroup dynGroup, PeriodCashflows periodCf)
    {
        return periodCf.Clone();
    }

    protected virtual CashflowAllocs BeginPeriod(IDeal deal, DynamicGroup dynGroup, PeriodCashflows periodCf)
    {
        var writedownAmt = WritedownAmt(deal, dynGroup, periodCf);
        var cfAlloc = new CashflowAllocs(periodCf.ScheduledPrincipal + periodCf.ForbearanceRecovery,
            periodCf.UnscheduledPrincipal + periodCf.ForbearanceUnscheduled, periodCf.RecoveryPrincipal, writedownAmt, periodCf.NetInterest);
        return cfAlloc;
    }

    public void PayExpenses(IFormulaExecutor formulaExecutor, DynamicGroup dynGroup, IRateProvider rateProvider,
        DateTime cfDate, List<TriggerValue> triggerValues, PeriodCashflows periodCf)
    {
        var netInterest = periodCf.NetInterest;
        // pay expenses
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
                    ec.PayExpense(cfDate, netInterest, shortfall);
                    netInterest = 0;
                }
                else
                {
                    ec.PayExpense(cfDate, expense, 0);
                    netInterest -= expense;
                }

                return expense;
            });

        // compute wac
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
    }

    public void PayAccrualStructures(DynamicGroup dynGroup, IRateProvider rateProvider, PeriodCashflows adjPeriodCf,
        List<TriggerValue> triggerValues, IList<DynamicClass> accrualClasses)
    {
        var allTrans = dynGroup.DynamicClasses.SelectMany(dt => dt.DynamicTranches).ToList();
        foreach (var accrualClass in accrualClasses.Where(acc => acc.DealStructure.PayFromEnum == PayFromEnum.Accrual))
        {
            var accretionDirectedClass = accrualClass.DynAcrretionClass;
            var accrualTranche =
                accrualClass.DynamicTranches.Single(dc => dc.Tranche.TrancheName == accrualClass.Tranche.TrancheName);
            accrualTranche.FormulaExecutor.Reset(new RulesResults(), triggerValues, dynGroup, adjPeriodCf, null);
            var accrualAmt = accrualTranche.Interest(accrualTranche.GetCashflow(adjPeriodCf.CashflowDate), rateProvider,
                allTrans);
            PayAccrualAndAccretionAccrualPhase(adjPeriodCf.CashflowDate, dynGroup, accretionDirectedClass, accrualClass,
                accrualAmt);
        }
    }

    public void PayInterestShortfallSupport(DynamicDeal dynDeal, DateTime cfDate)
    {
        var crossedTrancheCheck = new HashSet<DynamicClass>();

        foreach (var dynGroup in dynDeal.DynamicGroups)
        {
            var supoortsShortfallClasses = dynGroup.DynamicClasses.Where(dc => dc.ShortfallInterestSupport.Any());
            foreach (var dynClassSupports in supoortsShortfallClasses)
            foreach (var supportsShortfallClass in dynClassSupports.ShortfallInterestSupport)
            {
                var dynClassSupported = dynGroup.ClassByName(supportsShortfallClass);
                if (!crossedTrancheCheck.Add(dynClassSupported))
                    continue;

                if (dynClassSupported.DynamicTranches.Sum(c => c.GetCashflow(cfDate).AccumInterestShortfall) > 0)
                {
                    var supportsCf = dynClassSupports.GetCashflow(cfDate);
                    var supportAvailable = supportsCf.TotalPrincipal();
                    if (supportAvailable > 0)
                    {
                        var paid = 0.0;
                        var totalShortfall =
                            dynClassSupported.DynamicTranches.Sum(t => t.GetCashflow(cfDate).AccumInterestShortfall);
                        foreach (var dynTran in dynClassSupported.DynamicTranches)
                        {
                            var supportedTranCf = dynTran.GetCashflow(cfDate);
                            var factor = supportedTranCf.AccumInterestShortfall / totalShortfall;
                            var paybackAmt = Math.Min(supportAvailable * factor,
                                supportedTranCf.AccumInterestShortfall);
                            dynTran.PaybackInterestShortfall(supportedTranCf, paybackAmt);
                            paid += paybackAmt;
                        }

                        var totalPrin = supportsCf.ScheduledPrincipal + supportsCf.UnscheduledPrincipal;
                        var usp = supportsCf.UnscheduledPrincipal / totalPrin;
                        var sp = supportsCf.ScheduledPrincipal / totalPrin;

                        dynClassSupports.Pay(cfDate, -paid * usp, -paid * sp);
                        dynClassSupports.Writedown(cfDate, paid);
                    }
                }
            }
        }
    }

    public void CheckReserveFunds(DynamicGroup dynGroup, PeriodCashflows periodCf, ITrancheAllocator tranAllocator,
        IRateProvider rateProvider, List<TriggerValue> triggerValues, IPayRuleExecutor payRuleExecutor)
    {
        var fundsAccount = dynGroup.FundsAccount;
        if (fundsAccount == null)
            return;
        fundsAccount.NewPeriod();

        var interestAlloc = tranAllocator.GetInterestCollateralTranches(Enumerable.Repeat(dynGroup, 1).ToList(),
            rateProvider, periodCf.CashflowDate, Enumerable.Repeat(periodCf, 1).ToList());
        var excessInterest = interestAlloc.SingleOrDefault(res =>
            res.DynamicTranche.Tranche.TrancheName == fundsAccount.Tranche.TrancheName);
        if (excessInterest == null)
            return;
        fundsAccount.Deposit(excessInterest.Interest);

        var writedowns = dynGroup.DealClasses.Where(dc => dc.CumWritedown > 0)
            .OrderBy(dc => dc.DealStructure.SubordinationOrder).ToList();
        if (!writedowns.Any())
            return;

        foreach (var wd in writedowns)
        {
            var amtWithdrawn = fundsAccount.Debit(wd.CumWritedown);
            wd.Writeup(periodCf.CashflowDate, amtWithdrawn);
            dynGroup.ReservePayable.PayUsp(null, periodCf.CashflowDate, amtWithdrawn,
                () => ExecutePayRules(dynGroup.Deal, dynGroup, payRuleExecutor, triggerValues, periodCf));
        }
    }
}