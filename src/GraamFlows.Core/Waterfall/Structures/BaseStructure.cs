using GraamFlows.Objects.DataObjects;
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
        IEnumerable<PeriodCashflows> periodCfs, out IList<DynamicClass> payFromAllocator)
    {
        var nestedExchClasses = new HashSet<DynamicClass>();
        var payHash = new HashSet<DynamicClass>();
        var payFromAllocatorSet = new HashSet<DynamicClass>();

        foreach (var dynGroup in dynGroups)
        {
            var periodCf = periodCfs.SingleOrDefault(p => p.GroupNum == dynGroup.GroupNum);
            if (periodCf == null)
                continue;

            if (periodCf.BeginBalance < .01)
                continue;

            // pay exch
            var exchClasses = dynGroup.DynamicClasses.Where(dc =>
                    dc.DealStructure?.ExchangableTranche != null &&
                    dc.DealStructure.PayFromEnum == PayFromEnum.Exchange)
                .ToList();
            foreach (var exchClass in exchClasses)
            {
                var parentClass = dynGroups
                    .SelectMany(c => c.ClassesByNameOrTag(exchClass.DealStructure.ExchangableTranche)).ToList();
                var exClasses = exchClass.DealStructure.ExchangableTranche.Split(',').Distinct().ToList();
                var parClasses = parentClass.Select(c => c.Tranche.TrancheName).Distinct().ToList();

                if (parentClass.Any(pc => pc.DealStructure.PayFromEnum == PayFromEnum.Exchange))
                {
                    nestedExchClasses.Add(exchClass);
                    continue;
                }

                if (!exClasses.All(parClasses.Contains) || exClasses.Count != parClasses.Count)
                {
                    payFromAllocatorSet.Add(exchClass);
                    continue;
                }

                if (!payHash.Add(exchClass))
                    continue;

                var usp = parentClass.Sum(pc =>
                    GetExchangeShare(dynGroup, exchClass, pc, pc.GetCashflow(cashflowDate).UnscheduledPrincipal));
                var sp = parentClass.Sum(pc =>
                    GetExchangeShare(dynGroup, exchClass, pc, pc.GetCashflow(cashflowDate).ScheduledPrincipal));
                var wd = parentClass.Sum(pc =>
                    GetExchangeShare(dynGroup, exchClass, pc, pc.GetCashflow(cashflowDate).Writedown));

                if (sp + usp > exchClass.Balance)
                    exchClass.Pay(cashflowDate, exchClass.Balance, 0);
                else
                    exchClass.Pay(cashflowDate, usp, sp);

                if (wd > exchClass.Balance)
                    exchClass.Writedown(cashflowDate, exchClass.Balance);
                else
                    exchClass.Writedown(cashflowDate, wd);
            }

            foreach (var exchClass in nestedExchClasses)
            {
                var parentClass = dynGroup.ClassesByNameOrTag(exchClass.DealStructure.ExchangableTranche).ToList();
                var usp = parentClass.Sum(pc =>
                    GetExchangeShare(dynGroup, exchClass, pc, pc.GetCashflow(cashflowDate).UnscheduledPrincipal));
                var sp = parentClass.Sum(pc =>
                    GetExchangeShare(dynGroup, exchClass, pc, pc.GetCashflow(cashflowDate).ScheduledPrincipal));
                var wd = parentClass.Sum(pc =>
                    GetExchangeShare(dynGroup, exchClass, pc, pc.GetCashflow(cashflowDate).Writedown));
                if (sp + usp > exchClass.Balance)
                    exchClass.Pay(cashflowDate, exchClass.Balance, 0);
                else
                    exchClass.Pay(cashflowDate, usp, sp);

                if (wd > exchClass.Balance)
                    exchClass.Writedown(cashflowDate, exchClass.Balance);
                else
                    exchClass.Writedown(cashflowDate, wd);
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
        var wac = 1200 * (periodCf.Interest + periodCf.UnAdvancedInterest - periodCf.ServiceFee - expenses) /
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