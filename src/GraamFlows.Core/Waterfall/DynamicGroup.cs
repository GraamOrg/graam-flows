using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.RulesEngine;
using GraamFlows.Util;
using GraamFlows.Util.Functions;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall;

public class DynamicGroup : IDealVariableProvider, IPayablesHost
{
    private readonly Dictionary<string, IPayable> _accrualPayables = new();
    private readonly Dictionary<string, DynamicClass> _classByName = new();
    private readonly Dictionary<string, List<DynamicClass>> _classesByNameOrTag = new();
    private readonly Dictionary<string, List<DynamicClass>> _classesByTag = new();
    private readonly Dictionary<string, object> _ruleVars = new();
    private readonly Dictionary<ITranche, IList<DynamicClass>> _subordinateClassesCache = new();

    public DynamicGroup(IFormulaExecutor formulaExecutor, DateTime firstProjDate, IDeal deal, string groupNum, double collatBalance) :
        this(null, formulaExecutor, firstProjDate, deal, groupNum, collatBalance)
    {
    }

    public DynamicGroup(DynamicGroup? parentGroup, IFormulaExecutor formulaExecutor, DateTime firstProjDate, IDeal deal,
        string groupNum, double collatBalance)
    {
        ExchPayables = new Dictionary<string, IPayable>();
        FormulaExecutor = formulaExecutor;
        TriggerResults = new List<TriggerResult>();
        FailedStickyTriggers = new HashSet<string>();
        FirstProjectionDate = firstProjDate;
        GroupNum = groupNum;
        Deal = deal;
        EarliestTerminationDate = DateTime.MaxValue;
        Initialize(parentGroup, collatBalance);
    }

    public DateTime FirstProjectionDate { get; }
    public IDeal Deal { get; }
    public ICollection<DynamicClass> DynamicClasses => _classByName.Values;
    public string GroupNum { get; }
    public double BalanceAtIssuance { get; private set; }
    public double BeginningBalance { get; private set; }
    public IList<TriggerResult> TriggerResults { get; }
    public IFormulaExecutor FormulaExecutor { get; }
    public HashSet<string> FailedStickyTriggers { get; }
    public DynamicFundsAccount? FundsAccount { get; private set; }
    public double CollateralWac { get; set; }
    public double CollateralNetWac { get; set; }
    public double BeginCollatBalance { get; set; }
    public bool HasCrossedGroups { get; private set; }
    public double CollateralBondRatio { get; set; }
    public DateTime EarliestTerminationDate { get; set; }

    public DateTime FirstPayDate =>
        new(Deal.Tranches.First().FirstPayDate.Year, Deal.Tranches.First().FirstPayDate.Month, 1);

    /// <summary>
    ///     returns the classes which make up the group's balance
    /// </summary>
    public IList<DynamicClass>? DealClasses { get; private set; }


    /// <summary>
    ///     returns the classes which represent expenses
    /// </summary>
    public IList<DynamicClass> ExpenseClasses
    {
        get
        {
            var expenseClasses = DynamicClasses.Where(dynClass =>
                dynClass.DealStructure != null && dynClass.DealStructure.PayFromEnum == PayFromEnum.Expense).ToList();
            return expenseClasses;
        }
    }

    public IPayable? AccrualPayable { get; set; }
    public Dictionary<string, IPayable> ExchPayables { get; }

    public void SetVariable(string varName, object varValue)
    {
        _ruleVars[varName] = varValue;
    }

    public object GetVariableObj(string varName, DateTime? asOfDate = null)
    {
        // check rule vars first
        if (_ruleVars.TryGetValue(varName, out var ruleVar) && ruleVar != null) return ruleVar;

        // check deal vars
        var dealVar = Deal.DealVariables.FirstOrDefault(v =>
            v.VariableName.Equals(varName, StringComparison.InvariantCultureIgnoreCase));
        if (dealVar != null) return dealVar.VariableValue;

        // check scheduled variables
        if (asOfDate != null)
        {
            var schedVars = Deal.ScheduledVariables.Where(schedVar =>
                schedVar.ScheduleVariableName.Equals(varName, StringComparison.InvariantCulture)).ToList();
            if (schedVars.Any())
            {
                var schedVarFunc = ScheduledVariableFunction.FromScheduleVariables(schedVars.ToArray());
                return schedVarFunc.ValueAt(asOfDate.Value);
            }
        }

        // check field values
        var dealFv = Deal.DealFieldFieldValueByName(GroupNum, varName);
        if (dealFv != null)
            return dealFv.ValueNum;

        return 0; // dont fail if variable not found
    }

    public double GetVariable(string varName, DateTime? asOfDate = null)
    {
        if (double.TryParse(GetVariableObj(varName, asOfDate).ToString(), out var dblVal))
            return dblVal;
        throw new Exception($"Variable {varName} is not numeric!");
    }

    public IPayable? ScheduledPayable { get; set; }
    public IPayable? PrepayPayable { get; set; }
    public IPayable? RecoveryPayable { get; set; }
    public IPayable? ReservePayable { get; set; }

    // Unified waterfall payables
    public IPayable? InterestPayable { get; set; }
    public IPayable? WritedownPayable { get; set; }
    public IPayable? ExcessPayable { get; set; }

    // OC turbo payables
    public IPayable? TurboPayable { get; set; }
    public IPayable? ReleasePayable { get; set; }

    // Cap Carryover payable (Private RMBS - WAC-capped interest shortfall payback)
    public IPayable? CapCarryoverPayable { get; set; }

    // Supplemental reduction payables
    public IPayable? SupplementalPayable { get; set; }
    public string? SupplementalCapVariable { get; set; }
    public List<string>? SupplementalOfferedTranches { get; set; }
    public List<string>? SupplementalSeniorTranches { get; set; }

    private void Initialize(DynamicGroup? parentGroup, double collatBalance = 0)
    {
        foreach (var dealStructure in Deal.DealStructures.Where(ds => ds.GroupNum == GroupNum || ds.GroupNum == "0"))
        {
            if (dealStructure.GroupNum == "0")
                HasCrossedGroups = true;

            var classTranche = Deal.Tranches.Single(t => t.TrancheName == dealStructure.ClassGroupName);
            var dynamicTranches = Deal.Tranches.Where(tran => tran.ClassReference == dealStructure.ClassGroupName)
                .Select(tran =>
                    MarketTrancheFactory.GetDynamicMarketTranche(FormulaExecutor, this, tran, FirstProjectionDate));

            var parentClass = parentGroup?.ClassByName(classTranche.TrancheName);
            if (parentClass != null)
            {
                _classByName.Add(classTranche.TrancheName, parentClass);
                continue;
            }

            if (classTranche.TrancheTypeEnum == TrancheTypeEnum.CapFundsReserve)
                _classByName.Add(classTranche.TrancheName,
                    new DynamicFundsAccount(this, classTranche, dynamicTranches.ToList()));
            else
                _classByName.Add(classTranche.TrancheName,
                    new DynamicClass(this, classTranche, dynamicTranches.ToList()));
            EarliestTerminationDate = EarliestTerminationDate < classTranche.LegalMaturityDate
                ? classTranche.LegalMaturityDate
                : EarliestTerminationDate;
        }

        AddPseudoClasses();

        var issueBal = Deal.DealFieldFieldValueByName(GroupNum, "issue_bal");
        if (issueBal != null && issueBal.ValueNum > 0)
            BalanceAtIssuance = issueBal.ValueNum;
        else if (Deal.BalanceAtIssuance > 0)
            BalanceAtIssuance = Deal.BalanceAtIssuance;
        else
            BalanceAtIssuance = Deal.Assets.Where(asset => asset.GroupNum == GroupNum)
                .Sum(asset => asset.BalanceAtIssuance);

        var collatBalField = Deal.DealFieldFieldValueByName(GroupNum, "collat_balance");
        if (collatBalField != null)
            BeginningBalance = collatBalField.ValueNum;
        else if (collatBalance > 0)
            BeginningBalance = collatBalance;  // Use constructor parameter as fallback
        else
            BeginningBalance = Deal.Assets.Where(asset => asset.GroupNum == GroupNum)
                .Sum(asset => asset.CurrentBalance);
        
        // hash class tags
        _classesByTag.Clear();
        var classTags = Deal.DealStructures.Where(dc => dc.ClassTags != null).SelectMany(dc => dc.ClassTags.Split(','))
            .Distinct();
        foreach (var classTag in classTags)
            _classesByTag.Add(classTag,
                DynamicClasses.Where(dc => dc.DealStructure != null && dc.DealStructure.ContainsClassTag(classTag))
                    .ToList());

        // save deal classes
        var classes = DynamicClasses.Where(dc =>
            !dc.Tranche.IsPseudo &&
            !dc.IsExchangable() &&
            dc.Tranche.TrancheTypeEnum != TrancheTypeEnum.Exchanged &&
            dc.Tranche.TrancheTypeEnum != TrancheTypeEnum.CapFundsReserve &&
            dc.Tranche.TrancheTypeEnum != TrancheTypeEnum.ResidualInterest &&
            dc.Tranche.TrancheTypeEnum != TrancheTypeEnum.Certificate &&
            dc.DealStructure.PayFromEnum != PayFromEnum.ExcessServicing &&
            dc.DealStructure.PayFromEnum != PayFromEnum.Expense &&
            dc.DealStructure.PayFromEnum != PayFromEnum.Residual &&
            dc.DealStructure.PayFromEnum != PayFromEnum.Notional &&
            dc.Tranche.CashflowTypeEnum != CashflowType.Expense &&
            dc.Tranche.CashflowTypeEnum != CashflowType.InterestOnly).ToList();
        DealClasses = classes;

        FundsAccount = (DynamicFundsAccount)_classByName.Values.SingleOrDefault(dc =>
            dc.Tranche.TrancheTypeEnum == TrancheTypeEnum.CapFundsReserve ||
            dc.Tranche.TrancheTypeEnum == TrancheTypeEnum.OfferedCapFundsReserve);
    }

    private void AddPseudoClasses()
    {
        if (Deal.DealStructurePseudo == null)
            return;

        foreach (var pseudoStructure in Deal.DealStructurePseudo)
        {
            var dynClasses = pseudoStructure.ExchangableClassList.Split(',').Select(ClassByName).Where(dc => dc != null)
                .ToList();
            if (!dynClasses.Any())
                continue;
            var origBal = dynClasses.Sum(d => d.Tranche.OriginalBalance);
            var curBal = dynClasses.Sum(d => d.Balance);
            var factor = curBal / origBal;
            var pseudoTran = new Tranche(true)
            {
                Deal = Deal,
                DealName = Deal.DealName,
                TrancheName = pseudoStructure.ClassGroupName,
                OriginalBalance = origBal,
                Factor = factor,
                ClassReference = pseudoStructure.ClassGroupName
            };
            var tranches = Deal.Tranches.Where(tran => tran.ClassReference == pseudoStructure.ClassGroupName)
                .Select(tran =>
                    MarketTrancheFactory.GetDynamicMarketTranche(FormulaExecutor, this, tran, FirstProjectionDate));
            _classByName.Add(pseudoTran.TrancheName,
                new DynamicPseudoClass(this, pseudoTran, tranches.ToList(), dynClasses));
        }
    }

    public IList<DynamicClass> SubordinateClasses(ITranche tranche)
    {
        if (!_subordinateClassesCache.TryGetValue(tranche, out var subClassList))
        {
            var dealStructure = tranche.GetDealStructure();
            if (dealStructure == null)
            {
                _subordinateClassesCache[tranche] = new List<DynamicClass>();
                return _subordinateClassesCache[tranche];
            }

            if (dealStructure.PayFromEnum == PayFromEnum.Exchange)
            {
                var exClasses = dealStructure.ExchangableTranche.Split(",");
                var subClass = exClasses
                    .Select(@class => new { Class = @class, CreditSupp = ClassByName(@class)?.CreditSupport() })
                    .OrderBy(c => c.CreditSupp).FirstOrDefault();
                if (subClass != null && ClassByName(subClass.Class) != null)
                {
                    subClassList = SubordinateClasses(ClassByName(subClass.Class).Tranche);
                    _subordinateClassesCache[tranche] = subClassList;
                    return subClassList;
                }

                _subordinateClassesCache[tranche] = new List<DynamicClass>();
                return _subordinateClassesCache[tranche];
            }

            subClassList = DynamicClasses.Where(d => !d.Tranche.IsPseudo &&
                                                     d.DealStructure != null &&
                                                     d.DealStructure.SubordinationOrder >
                                                     dealStructure.SubordinationOrder &&
                                                     !d.IsExchangable() &&
                                                     (d.DealStructure.PayFromEnum == PayFromEnum.ProRata ||
                                                      d.DealStructure.PayFromEnum == PayFromEnum.Sequential ||
                                                      d.DealStructure.PayFromEnum == PayFromEnum.Rule)).ToList();
            _subordinateClassesCache[tranche] = subClassList;
            return _subordinateClassesCache[tranche];
        }

        return subClassList;
    }

    public IList<DynamicClass> SubordinateClasses(int subOrder)
    {
        return DynamicClasses.Where(d =>
            !d.Tranche.IsPseudo &&
            d.DealStructure != null &&
            !d.IsExchangable() &&
            d.DealStructure.SubordinationOrder > subOrder &&
            d.DealStructure.ExchangableTranche == null).ToList();
    }
    

    public IEnumerable<DynamicClass> SeniorSequentialClass()
    {
        var seniorClasses = DynamicClasses.Where(dc =>
                !dc.Tranche.IsPseudo &&
                dc.DealStructure != null &&
                dc.Balance > 0 &&
                dc.DealStructure.PayFromEnum == PayFromEnum.Sequential)
            .OrderBy(dc => dc.DealStructure.SubordinationOrder)
            .ToList();

        if (!seniorClasses.Any())
            return seniorClasses;
        var order = seniorClasses.First().DealStructure.SubordinationOrder;

        var parents = seniorClasses.Where(dc =>
            dc.Balance > 0 &&
            dc.DealStructure.SubordinationOrder == order &&
            !dc.IsExchangable());

        var exchables = DynamicClasses.Where(p =>
                !p.Tranche.IsPseudo &&
                p.DealStructure != null &&
                p.IsExchangable() &&
                p.Balance > 0 &&
                parents.Select(parent => parent.Tranche.TrancheName).Contains(p.DealStructure.ExchangableTranche))
            .OrderBy(b => b.DealStructure.SubordinationOrder).ToList();

        if (exchables.Any())
        {
            var exchOrder = exchables.First().DealStructure.SubordinationOrder;
            return parents.Concat(exchables.Where(p => p.DealStructure.SubordinationOrder == exchOrder));
        }

        return parents;
    }

    public IEnumerable<DynamicClass> AccrualStructures()
    {
        var accrualClasses = DynamicClasses.Where(dc =>
            !dc.Tranche.IsPseudo &&
            dc.DealStructure != null &&
            dc.Balance > 0 &&
            dc.DealStructure.PayFromEnum == PayFromEnum.Accrual &&
            !dc.IsInPaymentPhase &&
            dc.Tranche.TrancheTypeEnum != TrancheTypeEnum.Exchanged);

        return accrualClasses;
    }

    public IEnumerable<DynamicClass> SubordinateClass()
    {
        var juniorClasses = DynamicClasses.Where(dc =>
                !dc.Tranche.IsPseudo &&
                dc.DealStructure != null &&
                dc.Balance > 0 &&
                !dc.IsExchangable() &&
                (dc.Tranche.CashflowTypeEnum == CashflowType.PrincipalAndInterest ||
                 dc.Tranche.CashflowTypeEnum == CashflowType.PrincipalOnly) &&
                dc.DealStructure.PayFromEnum != PayFromEnum.ExcessServicing)
            .OrderByDescending(dc => dc.DealStructure.SubordinationOrder).ToList();

        if (!juniorClasses.Any())
            return juniorClasses;
        var order = juniorClasses.First().DealStructure.SubordinationOrder;
        var parents = juniorClasses.Where(dc =>
            dc.Balance > 0 && dc.DealStructure.SubordinationOrder == order && !dc.IsExchangable());

        var exchables = DynamicClasses.Where(p =>
                !p.Tranche.IsPseudo &&
                p.DealStructure != null &&
                p.IsExchangable() &&
                p.Balance > 0)
            .Where(exch => parents.Select(p => p.Tranche.TrancheName).Contains(exch.DealStructure.ExchangableTranche))
            .OrderByDescending(b => b.DealStructure.SubordinationOrder).ToList();
        if (exchables.Any())
        {
            var exchOrder = exchables.First().DealStructure.SubordinationOrder;
            return parents.Concat(exchables.Where(p => p.DealStructure.SubordinationOrder == exchOrder));
        }

        return parents;
    }

    public IList<DynamicClass> ClassesByTag(string tagName)
    {
        if (_classesByTag.TryGetValue(tagName, out var classes))
            return classes;
        return new List<DynamicClass>();
    }

    public virtual double CreditSupport(string tagName)
    {
        var classesByTag = ClassesByTag(tagName).ToList();
        if (!classesByTag.Any())
            throw new DealModelingException(Deal.DealName,
                $"Attempting to retrieve credit support for class tag {tagName} but it doesn't exist");
        return classesByTag.Select(ct => ct.CreditSupport()).Max();
    }

    public virtual double BalanceByClassOrTag(string tagName)
    {
        var classes = ClassesByNameOrTag(tagName);
        if (!classes.Any())
            throw new DealModelingException(Deal.DealName,
                $"Attempting to retrieve balance for class tag {tagName} but it doesn't exist");
        return classes.Sum(c => c.Balance);
    }

    public virtual double BeginBalanceByClassOrTag(string tagName, DateTime cfDate)
    {
        var dynClasses = ClassesByNameOrTag(tagName);
        return dynClasses.Sum(dc => dc.GetCashflow(cfDate).BeginBalance);
    }

    public IEnumerable<DynamicClass> NextExchangableClass(DynamicClass dynamicClass)
    {
        var exchClasses = DynamicClasses
            .Where(dc =>
                !dc.Tranche.IsPseudo &&
                dc.DealStructure != null &&
                dc.Balance > 0 &&
                dc.DealStructure.ExchangableTranche == dynamicClass.Tranche.TrancheName)
            .OrderBy(dc => dc.DealStructure.SubordinationOrder).ToList();
        if (exchClasses.Any())
        {
            var order = exchClasses.First().DealStructure.SubordinationOrder;
            return exchClasses.Where(dc => dc.DealStructure.SubordinationOrder == order);
        }

        return exchClasses;
    }

    public IEnumerable<DynamicClass> SubordinateExchangableClass(DynamicClass dynamicClass)
    {
        var exchClasses = DynamicClasses
            .Where(dc =>
                !dc.Tranche.IsPseudo &&
                dc.DealStructure != null &&
                dc.Balance > 0 &&
                dc.DealStructure.ExchangableTranche == dynamicClass.Tranche.TrancheName)
            .OrderByDescending(dc => dc.DealStructure.SubordinationOrder).ToList();
        if (exchClasses.Any())
        {
            var order = exchClasses.First().DealStructure.SubordinationOrder;
            return exchClasses.Where(dc => dc.DealStructure.SubordinationOrder == order);
        }

        return exchClasses;
    }

    public double Balance()
    {
        var balance = DealClasses.Sum(dc => dc.Balance);
        return balance;
    }
    
    /// <summary>
    /// WARNING This code needs review. Balance updates and cashflow release from certificates should happens as part of the deal model.
    /// Updates Certificate tranche balance to reflect current OC.
    /// OC = Pool Balance - Note Balance
    /// </summary>
    public void UpdateCertificateBalance(double poolBalance, DateTime cashflowDate)
    {
        var certificateClasses = DynamicClasses
            .Where(dc => dc.Tranche.TrancheTypeEnum == TrancheTypeEnum.Certificate)
            .ToList();

        if (!certificateClasses.Any())
            return;

        var noteBalance = Balance();
        var ocBalance = Math.Max(0, poolBalance - noteBalance);

        // Distribute OC balance to certificate tranches (typically just one)
        foreach (var certClass in certificateClasses)
        {
            // Record the cashflow to track balance over time
            var cf = certClass.GetCashflow(cashflowDate);
            cf.BeginBalance = certClass.Balance;

            // Record principal paydown when certificate balance decreases
            var balanceChange = certClass.Balance - ocBalance;
            if (balanceChange > 0)
                cf.ScheduledPrincipal += balanceChange;

            certClass.SetBalance(ocBalance);
            cf.Balance = ocBalance;
        }
    }

    public void Advance(DateTime cashflowDate)
    {
        foreach (var dynClass in DynamicClasses)
        {
            if (dynClass.Balance > 0)
                dynClass.Pay(cashflowDate, 0, 0);
        }
    }

    public void Lockout(DateTime cashflowDate, string classOrTag)
    {
        var lockoutClasses = ClassesByNameOrTag(classOrTag);
        if (!lockoutClasses.Any())
            throw new DealModelingException(Deal.DealName,
                $"Attempting to lockout class {classOrTag} but it doesn't exist");
        foreach (var dynClass in lockoutClasses)
            dynClass.Lockout(cashflowDate);
    }

    public IList<DynamicClass> ClassesByNameOrTag(string classOrTag)
    {
        if (!_classesByNameOrTag.TryGetValue(classOrTag, out var classesByNameOrTag))
        {
            var splitClassOrTag = classOrTag.Split(',').Select(item => item.Trim()).ToList();
            var classesByTag = splitClassOrTag.SelectMany(ClassesByTag).ToList();
            var classesByName = splitClassOrTag.Select(ClassByName).Where(dc => dc != null);
            classesByTag.AddRange(classesByName);
            var classesByTran = splitClassOrTag.Select(DynamicClassByTrancheName).Where(dc => dc != null);
            classesByTag.AddRange(classesByTran);
            classesByNameOrTag = classesByTag.Distinct().ToList();
            _classesByNameOrTag.Add(classOrTag, classesByNameOrTag);
        }

        return classesByNameOrTag;
    }

    public DynamicClass DynamicClassByTrancheName(string trancheName)
    {
        var dynamicClass =
            DynamicClasses.SingleOrDefault(dc => dc.DynamicTranches.Any(dt => dt.Tranche.TrancheName == trancheName));
        return dynamicClass;
    }

    public DynamicClass ClassByName(string trancheName)
    {
        _classByName.TryGetValue(trancheName, out var dynClass);
        return dynClass;
    }

    public IEnumerable<DynamicPseudoClass> ApplicablePseudoClasses(DynamicClass dynamicClass)
    {
        return DynamicClasses.OfType<DynamicPseudoClass>().Where(dc => dc.IsApplicableClass(dynamicClass));
    }

    public double CollateralFactor()
    {
        return Balance() / BalanceAtIssuance;
    }

    public void AddTriggerResult(DateTime cashflowDate, string triggerName, double requiredValue, double actualValue,
        bool passed)
    {
        var triggerResult = new TriggerResult(cashflowDate, GroupNum, triggerName, requiredValue, actualValue, passed);
        TriggerResults.Add(triggerResult);
    }

    public void ResetLockedOutClasses(DateTime cashflowDate)
    {
        foreach (var dynClass in DynamicClasses) dynClass.Lock(cashflowDate, false);
    }

    public void StartTrans(DateTime cfDate)
    {
        foreach (var dc in DynamicClasses)
            dc.StartTrans(cfDate);
    }

    public void CommitTrans()
    {
        foreach (var dc in DynamicClasses)
            dc.CommitTrans();
    }

    public void Rollback()
    {
        foreach (var dc in DynamicClasses)
            dc.Rollback();
    }

    public void SetAccrualPayableForAccrual(string className, IPayable payable)
    {
        _accrualPayables[className] = payable;
    }

    public IPayable GetAccrualPayable(string className)
    {
        _accrualPayables.TryGetValue(className, out var payable);
        return payable;
    }

    public void SetExchPayableForRemic(string remic, IPayable payable)
    {
        ExchPayables[remic] = payable;
    }
}