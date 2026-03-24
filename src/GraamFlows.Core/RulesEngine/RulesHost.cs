using System;
using System.Collections.Generic;
using System.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Triggers;
using GraamFlows.Util;
using GraamFlows.Util.Functions;
using GraamFlows.Waterfall;
using GraamFlows.Waterfall.MarketTranche;
using GraamFlows.Waterfall.Structures;
using GraamFlows.Waterfall.Structures.PayableStructures;
using SequentialStructure = GraamFlows.Waterfall.Structures.PayableStructures.SequentialStructure;

namespace GraamFlows.RulesEngine
{
    public class RulesHost
    {
        private class RulesHostSpecifier
        {
            public string Id { get; }

            public RulesHostSpecifier()
            {
                Id = Guid.NewGuid().ToString();
            }
        }


        public List<TriggerValue> TriggerResults { get; set; }
        public DynamicGroup DynamicGroup { get; set; }
        public IEnumerable<DynamicClass> PayRuleDynamicClass { get; set; }
        public PeriodCashflows PeriodCashflows { get; set; }
        private RulesResults _rulesResults;
        private IRateProvider _rateProvider { get; set; }
        private DateTime _cfDate { get; set; }
        private IEnumerable<DynamicTranche> _allTranches { get; set; }
        private DynamicTranche _dynamicTranche { get; set; }

        public double usp = 0;
        public double sp = 0;
        public double rp = 0;
        public double dp = 0;
        public double ib = 0;
        public double ccl = 0;
        public double net_wac = 0;
        public double eff_wac = 0;
        public double wac = 0;
        public double begin_balance = 0;
        public double forbearance_amt = 0;
        public double bal_at_issuance = 0;
        public double net_loss;
        public double cum_default_amt;
        public double cum_default_pct;
        public double serv_fee_rate;
        public int curr_month;
        public int curr_year;
        public double oc_amt;
        public double oc_pct;
        public double collat_wac;
        public double collat_net_wac;
        public double libor_1m => _rateProvider.GetRate(MarketDataInstEnum.Libor1M, _cfDate);
        public double libor_3m => _rateProvider.GetRate(MarketDataInstEnum.Libor3M, _cfDate);
        public double libor_6m => _rateProvider.GetRate(MarketDataInstEnum.Libor6M, _cfDate);
        public double libor_12m => _rateProvider.GetRate(MarketDataInstEnum.Libor12M, _cfDate);
        public double swap_2y => _rateProvider.GetRate(MarketDataInstEnum.Swap2Y, _cfDate);
        public double swap_3y => _rateProvider.GetRate(MarketDataInstEnum.Swap3Y, _cfDate);
        public double swap_4y => _rateProvider.GetRate(MarketDataInstEnum.Swap4Y, _cfDate);
        public double swap_5y => _rateProvider.GetRate(MarketDataInstEnum.Swap5Y, _cfDate);
        public double swap_6y => _rateProvider.GetRate(MarketDataInstEnum.Swap6Y, _cfDate);
        public double swap_7y => _rateProvider.GetRate(MarketDataInstEnum.Swap7Y, _cfDate);
        public double swap_8y => _rateProvider.GetRate(MarketDataInstEnum.Swap8Y, _cfDate);
        public double swap_9y => _rateProvider.GetRate(MarketDataInstEnum.Swap9Y, _cfDate);
        public double swap_10y => _rateProvider.GetRate(MarketDataInstEnum.Swap10Y, _cfDate);
        public double swap_12y => _rateProvider.GetRate(MarketDataInstEnum.Swap12Y, _cfDate);
        public double swap_15y => _rateProvider.GetRate(MarketDataInstEnum.Swap15Y, _cfDate);
        public double swap_20y => _rateProvider.GetRate(MarketDataInstEnum.Swap20Y, _cfDate);
        public double swap_25y => _rateProvider.GetRate(MarketDataInstEnum.Swap25Y, _cfDate);
        public double swap_30y => _rateProvider.GetRate(MarketDataInstEnum.Swap30Y, _cfDate);

        public void Reset(RulesResults rulesResults, List<TriggerValue> triggerResults, DynamicGroup dynGroup, PeriodCashflows periodCf, IEnumerable<DynamicClass> payRuleClass)
        {
            TriggerResults = triggerResults;
            DynamicGroup = dynGroup;
            PayRuleDynamicClass = payRuleClass;
            PeriodCashflows = periodCf;
            _rulesResults = rulesResults;

            // set vars
            sp = periodCf.ScheduledPrincipal;
            usp = periodCf.UnscheduledPrincipal;
            rp = periodCf.RecoveryPrincipal;
            dp = periodCf.DefaultedPrincipal;
            ib = dynGroup.BalanceAtIssuance;
            ccl = periodCf.CumCollateralLoss;
            net_wac = periodCf.NetWac;
            wac = periodCf.WAC;
            begin_balance = periodCf.BeginBalance;
            forbearance_amt = periodCf.AccumForbearance;
            eff_wac = periodCf.EffectiveWac;
            bal_at_issuance = dynGroup.Deal.BalanceAtIssuance;
            net_loss = periodCf.CollateralLoss;
            cum_default_amt = periodCf.CumDefaultedPrincipal;
            cum_default_pct = periodCf.CumDefaultedPrincipalPct;
            serv_fee_rate = 1200 * periodCf.ServiceFee / periodCf.BeginBalance;
            curr_month = periodCf.CashflowDate.Month;
            curr_year = periodCf.CashflowDate.Year;
            oc_amt = periodCf.Balance - dynGroup.Balance(); // assets minus liabilities
            oc_pct = dynGroup.Balance() / periodCf.Balance;
                
            /*// backwards compatibility
            if (dynGroup.GetType().GetMethods().Contains("CollateralWac"))
            {
                collat_wac = dynGroup.CollateralWac;
                collat_net_wac = dynGroup.CollateralNetWac;

                if (payRuleClass != null)
                {
                    foreach (var dynClass in payRuleClass)
                    {
                        dynClass.ClearShortfallInterestSupport();
                    }
                }
            }*/
        }

        public void ResetTrancheFormulas(DynamicTranche tranche, IRateProvider rateProvider, DateTime cfDate, IEnumerable<DynamicTranche> allTranches)
        {
            _dynamicTranche = tranche;
            _rateProvider = rateProvider;
            _cfDate = cfDate;
            _allTranches = allTranches;
            curr_month = _cfDate.Month;
            curr_year = _cfDate.Year;
            collat_wac = tranche.DynamicGroup.CollateralWac;
            collat_net_wac = tranche.DynamicGroup.CollateralNetWac;
            _dynamicTranche.IsInterest = false;
        }

        private class ClassStructureVars
        {
            public DynamicClass DynamicClass { get; }
            public DynamicGroup DynamicGroup { get; }

            public ClassStructureVars(DynamicClass dynClass, DynamicGroup dynGroup)
            {
                DynamicClass = dynClass;
                DynamicGroup = dynGroup;
            }

            public double creditSupport => DynamicClass.CreditSupport();
            public double cs => creditSupport;
        }

        private bool PASSED(string triggerName)
        {
            foreach (var trigger in triggerName.Split(','))
            {
                var triggerResult = GetTriggerValue(trigger.Trim());
                if (triggerResult.TriggerResult == false)
                    return false;
            }

            return true;
        }

        private bool FAILED(string triggerName)
        {
            foreach (var trigger in triggerName.Split(','))
            {
                var triggerResult = GetTriggerValue(trigger.Trim());
                if (triggerResult.TriggerResult == false)
                    return true;
            }

            return false;
        }

        private bool FAILED_ALL(string triggerName)
        {
            foreach (var trigger in triggerName.Split(','))
            {
                var triggerResult = GetTriggerValue(trigger.Trim());
                if (triggerResult.TriggerResult)
                    return false;
            }

            return true;
        }

        public void SET_VAR(string varName, object valueValue)
        {
            DynamicGroup.SetVariable(varName, valueValue);
        }

        private double VALUE(string triggerName)
        {
            var triggerResult = GetTriggerValue(triggerName);
            return triggerResult.NumericValue;
        }

        private void PAY(double amount)
        {
            _rulesResults.Pay(amount);
        }

        private double VAR(string varName)
        {
            return DynamicGroup.GetVariable(varName, PeriodCashflows.CashflowDate);

            //TODO: Check triggers
        }

        private double VAR2(string varName)
        {
            double dblVal;

            // check deal vars first
            var dealVar = DynamicGroup.Deal.DealVariables.FirstOrDefault(v => v.VariableName.Equals(varName, StringComparison.InvariantCultureIgnoreCase));
            if (dealVar != null)
            {
                if (double.TryParse(dealVar.VariableValue2, out dblVal))
                    return dblVal;
                throw new Exception($"Variable {varName} is not numeric!");
            }

            // check scheduled variables
            var schedVars = DynamicGroup.Deal.ScheduledVariables.Where(schedVar => schedVar.ScheduleVariableName.Equals(varName, StringComparison.InvariantCulture));
            if (schedVars.Any())
            {
                var schedVarFunc = ScheduledVariableFunction.FromScheduleVariables(schedVars.ToArray());
                return schedVarFunc.ValueAt(PeriodCashflows.CashflowDate);
            }

            var dealFv = DynamicGroup.Deal.DealFieldFieldValueByName(DynamicGroup.GroupNum, varName);
            if (dealFv != null)
                return dealFv.ValueNum;

            throw new Exception($"Variable {varName} does not exist as a Deal Variable or Deal Field Value");
        }

        private ClassStructureVars CLASS(string className)
        {
            return new ClassStructureVars(GetDynamicClass(className), DynamicGroup);
        }

        private TriggerValue GetTriggerValue(string triggerName)
        {
            var triggerResult = TriggerResults.FirstOrDefault(result => result.TriggerName.Equals(triggerName, StringComparison.InvariantCultureIgnoreCase));
            if (triggerResult == null)
                throw new DealModelingException(DynamicGroup.Deal.DealName, $"Rule requires trigger {triggerName} but does not exist!");
            return triggerResult;
        }

        private DynamicClass GetDynamicClass(string className)
        {
            var dynamicClass = DynamicGroup.ClassByName(className);
            if (dynamicClass == null)
                throw new DealModelingException(DynamicGroup.Deal.DealName, $"Rule requires class group {className} but does not exist!");
            return dynamicClass;
        }

        private double MIN(params double[] list)
        {
            return list.Min();
        }

        private double MAX(params double[] list)
        {
            return list.Max();
        }

        private double BALANCE(string classOrTag)
        {
            return DynamicGroup.BalanceByClassOrTag(classOrTag);
        }

        private double BEGIN_BALANCE(string classOrTag)
        {
            return DynamicGroup.BeginBalanceByClassOrTag(classOrTag, PeriodCashflows.CashflowDate);
        }

        /// <summary>
        /// Prevents a class or tag from recieving any principal
        /// </summary>
        /// <param name="classOrTag"></param>
        private void LOCKOUT(string classOrTag)
        {
            DynamicGroup.Lockout(PeriodCashflows.CashflowDate, classOrTag);
        }

        private void LOCKOUT()
        {
            foreach (var dc in PayRuleDynamicClass)
            {
                dc.Lockout(PeriodCashflows.CashflowDate);
            }
        }

        private void UNLOCK()
        {
            foreach (var payRuleDynClass in PayRuleDynamicClass)
            {
                payRuleDynClass.Unlock(PeriodCashflows.CashflowDate);
            }
        }

        private void SET_ACCRETION(string accretionClass)
        {
            foreach (var payRuleDynClass in PayRuleDynamicClass)
                payRuleDynClass.SetAccretion(accretionClass);
        }

        private void SET_PAYMENT_PHASE()
        {
            foreach (var payRuleDynClass in PayRuleDynamicClass)
                payRuleDynClass.SetPaymentPhase(true);
        }

        private void SET_ACCRUAL_PHASE()
        {
            foreach (var payRuleDynClass in PayRuleDynamicClass)
                payRuleDynClass.SetPaymentPhase(false);
        }

        // returns the percentage of the 
        private double BEGIN_CLASS_PCT(string classOrTag)
        {
            double balance = DynamicGroup.BeginBalanceByClassOrTag(classOrTag, PeriodCashflows.CashflowDate);
            double dealBalance = PeriodCashflows.BeginBalance;
            return balance / dealBalance;
        }

        private double CLASS_PCT(string classOrTag)
        {
            double balance = DynamicGroup.BalanceByClassOrTag(classOrTag);
            double dealBalance = PeriodCashflows.Balance;
            return balance / dealBalance;
        }


        private double SUBORDINATE_BALANCE(string classOrTag)
        {
            var classes = DynamicGroup.ClassesByNameOrTag(classOrTag);
            return classes.Max(dc => dc.SubordinateBalance());
        }

        private double CREDIT_SUPPORT(string classOrTag)
        {
            var classes = DynamicGroup.ClassesByNameOrTag(classOrTag);
            return classes.Max(dc => dc.CreditSupport());
        }

        private double BEGIN_CREDIT_SUPPORT(string classOrTag)
        {
            var classes = DynamicGroup.ClassesByNameOrTag(classOrTag);
            return classes.Max(dc => dc.BeginCreditSupport(PeriodCashflows.CashflowDate));
        }

        private double DETACH_POINT(string classOrTag)
        {
            var classes = DynamicGroup.ClassesByNameOrTag(classOrTag);
            return classes.Max(dc => dc.DetachmentPoint());
        }

        private double DETACH_POINT()
        {
            return PayRuleDynamicClass.Max(dc => dc.DetachmentPoint());
        }

        private double BEGIN_DETACH_POINT()
        {
            return PayRuleDynamicClass.Max(dc => dc.BeginDetachmentPoint(PeriodCashflows.CashflowDate));
        }

        private ProrataStructure PRORATA(params string[] classOrTag)
        {
            var payables = new List<IPayable>();

            foreach (var @class in classOrTag)
            {
                payables.AddRange(DynamicGroup.ClassesByNameOrTag(@class).Cast<IPayable>().ToList());
            }

            return new ProrataStructure(payables);
        }

        private ProrataStructure PRORATA(string classOrTag1, params IPayable[] payablesIn)
        {
            var payables = new List<IPayable>();
            payables.AddRange(DynamicGroup.ClassesByNameOrTag(classOrTag1).Cast<IPayable>().ToList());
            payables.AddRange(payablesIn);
            return new ProrataStructure(payables);
        }

        private ProrataStructure PRORATA(string classOrTag1, string classOrTag2, params IPayable[] payablesIn)
        {
            var payables = new List<IPayable>();
            payables.AddRange(DynamicGroup.ClassesByNameOrTag(classOrTag1).Cast<IPayable>().ToList());
            payables.AddRange(DynamicGroup.ClassesByNameOrTag(classOrTag2).Cast<IPayable>().ToList());
            payables.AddRange(payablesIn);
            return new ProrataStructure(payables);
        }

        private ProrataStructure PRORATA(string classOrTag1, string classOrTag2, string classOrTag3, params IPayable[] payablesIn)
        {
            var payables = new List<IPayable>();
            payables.AddRange(DynamicGroup.ClassesByNameOrTag(classOrTag1).Cast<IPayable>().ToList());
            payables.AddRange(DynamicGroup.ClassesByNameOrTag(classOrTag2).Cast<IPayable>().ToList());
            payables.AddRange(DynamicGroup.ClassesByNameOrTag(classOrTag3).Cast<IPayable>().ToList());
            payables.AddRange(payablesIn);
            return new ProrataStructure(payables);
        }

        private ProrataStructure PRORATA(params IPayable[] payables)
        {
            return new ProrataStructure(payables);
        }

        private ShiftingInterestStructure SHIFTI(string shiftiVar, IPayable seniors, IPayable subs)
        {
            return new ShiftingInterestStructure(DynamicGroup, shiftiVar, seniors, subs);
        }

        private ShiftingInterestStructure SHIFTI(double shiftAmt, IPayable seniors, IPayable subs)
        {
            return new ShiftingInterestStructure(shiftAmt, seniors, subs);
        }

        private EnhancementCapStructure CSCAP(string shiftiVar, IPayable seniors, IPayable subs)
        {
            return new EnhancementCapStructure(DynamicGroup, shiftiVar, seniors, subs);
        }

        private EnhancementCapStructure CSCAP(double shiftAmt, IPayable seniors, IPayable subs)
        {
            return new EnhancementCapStructure(shiftAmt, seniors, subs);
        }

        private SequentialStructure SEQ(params IPayable[] payables)
        {
            return new SequentialStructure(payables);
        }

        private PlannedAmortizationStructure PAC(string balSchedVar, IPayable senior, IPayable support)
        {
            return new PlannedAmortizationStructure(DynamicGroup, balSchedVar, senior, support);
        }

        private SinglePlannedAmortizationClass SPAC(string balSchedVar, IPayable spac)
        {
            return new SinglePlannedAmortizationClass(DynamicGroup, balSchedVar, spac);
        }

        private ProformaStructure PROFORMA(IPayable payable, double forma)
        {
            return new ProformaStructure(payable, forma);
        }

        private ProformaStructure PROFORMA(IPayable payable1, double forma1, IPayable payable2, double forma2)
        {
            return new ProformaStructure(payable1, forma1, payable2, forma2);
        }

        private ProformaStructure PROFORMA(IPayable payable1, double forma1, IPayable payable2, double forma2, IPayable payable3, double forma3)
        {
            return new ProformaStructure(payable1, forma1, payable2, forma2, payable3, forma3);
        }

        private FixedStructure FIXED(string @var, IPayable @fixed, IPayable support)
        {
            return new FixedStructure(DynamicGroup, @var, @fixed, support);
        }
        
        private ForcedPaydownStructure FORCE_PAYDOWN(IPayable forced, IPayable support)
        {
            return new ForcedPaydownStructure(forced, support);
        }

        private FixedStructure FIXED(double fixedAmt, IPayable @fixed, IPayable support)
        {
            return new FixedStructure(fixedAmt, @fixed, support);
        }

        private SequentialStructure SEQ(params string[] arrClasses)
        {
            // careful to preserve ordering
            var payables = new List<IPayable>();

            foreach (var classes in arrClasses)
            {
                var classesArray = classes.Split(',');
                foreach (var @class in classesArray)
                {
                    var dynSeq = DynamicGroup.ClassByName(@class);
                    if (dynSeq != null)
                        payables.Add(dynSeq);
                    else
                    {
                        var seqGroups = DynamicGroup.ClassesByNameOrTag(@class);
                        foreach (var seqGroup in seqGroups.OrderBy(g => g.DealStructure.SubordinationOrder))
                            payables.Add(seqGroup);
                    }
                }
            }

            return new SequentialStructure(payables);
        }

        private SequentialStructure SEQ(IPayable payable, string classes)
        {
            // careful to preserve ordering
            var payables = new List<IPayable>();
            payables.Add(payable);
            var classesArray = classes.Split(',');
            foreach (var @class in classesArray)
            {
                var dynSeq = DynamicGroup.ClassByName(@class);
                if (dynSeq != null)
                    payables.Add(dynSeq);
                else
                {
                    var seqGroups = DynamicGroup.ClassesByNameOrTag(@class);
                    foreach (var seqGroup in seqGroups.OrderBy(g => g.DealStructure.SubordinationOrder))
                        payables.Add(seqGroup);
                }
            }

            return new SequentialStructure(payables);
        }

        private SequentialStructure SEQ(IPayable payable1, IPayable payable2, string classes)
        {
            // careful to preserve ordering
            var payables = new List<IPayable>();
            payables.Add(payable1);
            payables.Add(payable2);
            var classesArray = classes.Split(',');
            foreach (var @class in classesArray)
            {
                var dynSeq = DynamicGroup.ClassByName(@class);
                if (dynSeq != null)
                    payables.Add(dynSeq);
                else
                {
                    var seqGroups = DynamicGroup.ClassesByNameOrTag(@class);
                    foreach (var seqGroup in seqGroups.OrderBy(g => g.DealStructure.SubordinationOrder))
                        payables.Add(seqGroup);
                }
            }

            return new SequentialStructure(payables);
        }

        private SequentialStructure SEQ(IPayable payable1, IPayable payable2, IPayable payable3, string classes)
        {
            // careful to preserve ordering
            var payables = new List<IPayable>();
            payables.Add(payable1);
            payables.Add(payable2);
            payables.Add(payable3);
            var classesArray = classes.Split(',');
            foreach (var @class in classesArray)
            {
                var dynSeq = DynamicGroup.ClassByName(@class);
                if (dynSeq != null)
                    payables.Add(dynSeq);
                else
                {
                    var seqGroups = DynamicGroup.ClassesByNameOrTag(@class);
                    foreach (var seqGroup in seqGroups.OrderBy(g => g.DealStructure.SubordinationOrder))
                        payables.Add(seqGroup);
                }
            }

            return new SequentialStructure(payables);
        }

        private SequentialStructure SEQ(string classes, params IPayable[] payablesIn)
        {
            // careful to preserve ordering
            var payables = new List<IPayable>();
            var classesArray = classes.Split(',');
            foreach (var @class in classesArray)
            {
                var dynSeq = DynamicGroup.ClassByName(@class);
                if (dynSeq != null)
                    payables.Add(dynSeq);
                else
                {
                    var seqGroups = DynamicGroup.ClassesByNameOrTag(@class);
                    foreach (var seqGroup in seqGroups.OrderBy(g => g.DealStructure.SubordinationOrder))
                        payables.Add(seqGroup);
                }
            }

            foreach (var payable in payablesIn)
            {
                payables.Add(payable);
            }

            return new SequentialStructure(payables);
        }

        private IPayable SINGLE(string @class)
        {
            var dynClass = DynamicGroup.ClassByName(@class);
            return dynClass;
        }

        private void SET_SCHED_STRUCT(IPayable payable)
        {
            DynamicGroup.ScheduledPayable = payable;
        }

        private void SET_PREPAY_STRUCT(IPayable payable)
        {
            DynamicGroup.PrepayPayable = payable;
        }

        private void SET_RECOV_STRUCT(IPayable payable)
        {
            DynamicGroup.RecoveryPayable = payable;
        }

        private void SET_PRIN_STRUCT(IPayable payable)
        {
            SET_SCHED_STRUCT(payable);
            SET_PREPAY_STRUCT(payable);
            SET_RECOV_STRUCT(payable);
        }

        private void SET_ACC_STRUCT(IPayable payable)
        {
            DynamicGroup.AccrualPayable = payable;
        }

        private void SET_SPEC_ACC_STRUCT(string className, IPayable payable)
        {
            DynamicGroup.SetAccrualPayableForAccrual(className, payable);
        }

        private void SET_RESERVE_STRUCT(IPayable payable)
        {
            DynamicGroup.ReservePayable = payable;
        }

        private void SET_EXCH_STRUCT(string remic, IPayable payable)
        {
            DynamicGroup.SetExchPayableForRemic(remic, payable);
        }

        // Unified waterfall structure commands
        private void SET_INTEREST_STRUCT(IPayable payable)
        {
            DynamicGroup.InterestPayable = payable;
        }

        private void SET_WRITEDOWN_STRUCT(IPayable payable)
        {
            DynamicGroup.WritedownPayable = payable;
        }

        private void SET_EXCESS_STRUCT(IPayable payable)
        {
            DynamicGroup.ExcessPayable = payable;
        }

        private void SET_TURBO_STRUCT(IPayable payable)
        {
            DynamicGroup.TurboPayable = payable;
        }

        private void SET_RELEASE_STRUCT(IPayable payable)
        {
            DynamicGroup.ReleasePayable = payable;
        }

        private void SET_SUPPL_STRUCT(IPayable payable)
        {
            DynamicGroup.SupplementalPayable = payable;
        }

        private void SET_CAP_CARRYOVER_STRUCT(IPayable payable)
        {
            DynamicGroup.CapCarryoverPayable = payable;
        }

        private void SET_SUPPL_CONFIG(string capVariable, string subTranches, string seniorTranches)
        {
            DynamicGroup.SupplementalCapVariable = capVariable;
            DynamicGroup.SupplementalOfferedTranches = subTranches.Split(',').Select(t => t.Trim()).ToList();
            DynamicGroup.SupplementalSeniorTranches = seniorTranches.Split(',').Select(t => t.Trim()).ToList();
        }

        private double COUPON(params string[] trancheList)
        {
            double interest = 0;
            foreach (var tranDelim in trancheList)
            {
                foreach (var tranName in tranDelim.Split(','))
                {
                    var tran = _allTranches.SingleOrDefault(t => t.Tranche.TrancheName == tranName);
                    if (tran == null)
                        throw new DealModelingException(DynamicGroup.Deal.DealName, $"Cant find {tranName}");
                    var tranCf = tran.GetCashflow(_cfDate);
                    interest += tran.Interest(tranCf, _rateProvider, _allTranches);
                }
            }

            return COUPON(interest);
        }

        private double COUPON(double interest)
        {
            var tcf = _dynamicTranche.GetCashflow(_cfDate);
            var balance = _dynamicTranche.TrancheBalance(tcf);
            var frac = _dynamicTranche.YearFraction(_cfDate);
            var cpn = interest / (balance * .01 * frac * _dynamicTranche.ResetSlope());
            return cpn;
        }
      
        private double COUPON(string tranche, double weight)
        {
            double interest = 0;
            var tran = _allTranches.Single(t => t.Tranche.TrancheName == tranche);
            var tranCf = tran.GetCashflow(_cfDate);
            interest += tran.Interest(tranCf, _rateProvider, _allTranches) * weight;

            return COUPON(interest);
        }

        private double COUPON(string tranche1, double weight1, string tranche2, double weight2)
        {
            double interest = 0;

            // tranche 1
            var tran = _allTranches.Single(t => t.Tranche.TrancheName == tranche1);
            var tranCf = tran.GetCashflow(_cfDate);
            interest += tran.Interest(tranCf, _rateProvider, _allTranches) * weight1;

            // tranche 2
            tran = _allTranches.Single(t => t.Tranche.TrancheName == tranche2);
            tranCf = tran.GetCashflow(_cfDate);
            interest += tran.Interest(tranCf, _rateProvider, _allTranches) * weight2;

            return COUPON(interest);
        }

        private double COUPON(string tranche1, double weight1, string tranche2, double weight2, string tranche3, double weight3)
        {
            double interest = 0;

            // tranche 1
            var tran = _allTranches.Single(t => t.Tranche.TrancheName == tranche1);
            var tranCf = tran.GetCashflow(_cfDate);
            interest += tran.Interest(tranCf, _rateProvider, _allTranches) * weight1;

            // tranche 2
            tran = _allTranches.Single(t => t.Tranche.TrancheName == tranche2);
            tranCf = tran.GetCashflow(_cfDate);
            interest += tran.Interest(tranCf, _rateProvider, _allTranches) * weight2;

            // tranche 3
            tran = _allTranches.Single(t => t.Tranche.TrancheName == tranche3);
            tranCf = tran.GetCashflow(_cfDate);
            interest += tran.Interest(tranCf, _rateProvider, _allTranches) * weight3;

            return COUPON(interest);
        }

        private double INTEREST(string tranche, double weight)
        {
            double interest = 0;
            var tran = _allTranches.Single(t => t.Tranche.TrancheName == tranche);
            var tranCf = tran.GetCashflow(_cfDate);
            interest += tran.Interest(tranCf, _rateProvider, _allTranches) * weight;
            _dynamicTranche.IsInterest = true;
            return interest;
        }

        private double INTEREST(string tranche1, double weight1, string tranche2, double weight2)
        {
            double interest = 0;
            interest += INTEREST(tranche1, weight1);
            interest += INTEREST(tranche2, weight2);
            return interest;
        }

        private double INTEREST(string tranche1, double weight1, string tranche2, double weight2, string tranche3, double weight3)
        {
            double interest = 0;
            interest += INTEREST(tranche1, weight1);
            interest += INTEREST(tranche2, weight2);
            interest += INTEREST(tranche3, weight3);
            return interest;
        }

        private double IF(bool cond, double if_true, double if_false = 0)
        {
            if (cond)
                return if_true;
            return if_false;
        }
      
        private double WAC(params string[] trancheList)
        {
            double interest = 0;
            double balance = 0;
            foreach (var tranDelim in trancheList)
            {
                foreach (var tranName in tranDelim.Split(','))
                {
                    var tran = _allTranches.SingleOrDefault(t => t.Tranche.TrancheName == tranName);
                    if (tran == null)
                        throw new DealModelingException(DynamicGroup.Deal.DealName, $"Cant find {tranName}");
                    var tranCf = tran.GetCashflow(_cfDate);
                    interest += tran.Interest(tranCf, _rateProvider, _allTranches);
                    balance += tran.BeginBalance(_cfDate);
                }
            }

            return 1200.0 * interest / balance;
        }

        public void SUPPORT_INTEREST_SHORTFALL(params string[] trancheList)
        {
            foreach (var payRuleClass in PayRuleDynamicClass)
            {
                foreach (var dynClassItem in trancheList)
                {
                    foreach (var dynClass in dynClassItem.Split(','))
                    {
                        payRuleClass.AddShortfallInterestSupport(dynClass);
                    }
                }
            }
        }

//}}