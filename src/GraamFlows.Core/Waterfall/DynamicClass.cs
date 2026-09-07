using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Util;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall;

public class DynamicClass : IPayable
{
    private TrancheCashflow _transCf;

    public DynamicClass(DynamicGroup dynamicGroup, ITranche tranche, IList<DynamicTranche> dynamicTranches) : this(
        dynamicGroup, tranche)
    {
        DynamicTranches = dynamicTranches;
        foreach (var dynTran in dynamicTranches)
            dynTran.ClassReference = this;
    }

    public DynamicClass(DynamicGroup dynamicGroup, ITranche tranche)
    {
        Cashflows = new Dictionary<DateTime, TrancheCashflow>();
        ShortfallInterestSupport = new HashSet<string>();
        DynamicGroup = dynamicGroup;
        Tranche = tranche;
        DealStructure = Tranche.GetDealStructure();
        SetBalance(tranche.OriginalBalance * tranche.Factor);
    }

    public DynamicGroup DynamicGroup { get; }
    public ITranche Tranche { get; }
    public double Balance { get; private set; }

    public double CumWritedown { get; private set; }

    public IDealStructure DealStructure { get; }
    public Dictionary<DateTime, TrancheCashflow> Cashflows { get; }
    public IList<DynamicTranche> DynamicTranches { get; }
    internal DynamicClass DynAcrretionClass { get; private set; }
    internal DynamicClass DynAccrualClass { get; private set; }
    public bool IsInPaymentPhase { get; private set; }
    public HashSet<string> ShortfallInterestSupport { get; set; }

    public void PaySp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec)
    {
        Pay(cfDate, 0, prin);
    }

    public void PayUsp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec)
    {
        Pay(cfDate, prin, 0);
    }

    public void PayRp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayUsp(caller, cfDate, prin, payRuleExec);
    }

    public void PayWritedown(IPayable caller, DateTime cfDate, double amount, Action payRuleExec)
    {
        Writedown(cfDate, amount);
    }

    public double PayInterest(IPayable caller, DateTime cfDate, double availableFunds,
        IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches)
    {
        var interestPaid = 0.0;
        var allTranchesList = allTranches?.ToList();

        foreach (var dynTran in DynamicTranches)
        {
            var cf = dynTran.GetCashflow(cfDate);
            // ExcessInterest (XS / monthly excess cashflow) sweeps whatever
            // interest is left after the coupon-bearing classes are paid — Payscen
            // TrancheAllocator parity (CalculateTrancheInterest: `interest =
            // availableInterest`). Its coupon is 0, so balance × coupon would
            // otherwise pay it nothing.
            // A Modification Loss Amount allocated against this tranche's Interest Accrual
            // Amount reduces what it is DUE this period (agency CRT: amounts allocated in the
            // sixth, seventh, tenth or twelfth priority "will result in a corresponding
            // reduction of the Interest Payment Amount"). Netted here rather than paid and
            // clawed back, so the class simply asks for less.
            // `NetOfModification` is an identity when nothing was booked, so a deal with no
            // Modification Loss Priority keeps its exact previous arithmetic — including a
            // negative accrual, which a Formula coupon can produce and which an unconditional
            // Math.Max would have clamped.
            var interestDue = dynTran.Tranche.CouponTypeEnum == CouponType.ExcessInterest
                ? availableFunds - interestPaid
                : NetOfModification(dynTran.Interest(cf, rateProvider, allTranchesList), cf)
                  + cf.AccumInterestShortfall;
            var toPay = Math.Min(interestDue, availableFunds - interestPaid);

            if (toPay > 0)
            {
                dynTran.PayInterest(cf, rateProvider, null, allTranchesList, toPay);
                interestPaid += toPay;
            }
            else
            {
                // No funds reached this class. Its coupon still accrued, so book it
                // as a shortfall — AccrueUnpaidInterest pays nothing and consumes
                // nothing, it only records. Skipping the class outright left it
                // reporting a 0 coupon and a frozen shortfall while outstanding (#58).
                dynTran.AccrueUnpaidInterest(cf, rateProvider, allTranchesList);
            }
        }

        return interestPaid;
    }

    public double InterestDue(DateTime cfDate, IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches)
    {
        var allTranchesList = allTranches?.ToList();
        return DynamicTranches.Sum(dynTran =>
        {
            var cf = dynTran.GetCashflow(cfDate);
            // Netted exactly as PayInterest nets it — this is what sizes a class's ask inside a
            // SEQ or PRORATA interest cascade, so leaving it gross would hand the class funds
            // its own PayInterest then refuses, stranding them above the classes below it.
            return NetOfModification(dynTran.Interest(cf, rateProvider, allTranchesList), cf)
                   + cf.AccumInterestShortfall;
        });
    }

    /// <summary>
    ///     A period's accrual less any Modification Loss Amount booked against it, floored at
    ///     zero. An IDENTITY when nothing was booked — deliberately, so every deal without a
    ///     Modification Loss Priority keeps the arithmetic it had, negative accruals included.
    /// </summary>
    public static double NetOfModification(double accrual, TrancheCashflow cf)
    {
        return cf.ModificationLoss > 0 ? Math.Max(accrual - cf.ModificationLoss, 0) : accrual;
    }

    public double PayInterestShortfall(DateTime cfDate, double availableFunds)
    {
        var totalPaid = 0.0;
        var remaining = availableFunds;
        foreach (var dynTran in DynamicTranches)
        {
            if (remaining < 0.01) break;
            var cf = dynTran.GetCashflow(cfDate);
            if (cf.AccumInterestShortfall <= 0) continue;
            var toPay = Math.Min(remaining, cf.AccumInterestShortfall);
            dynTran.PaybackInterestShortfall(cf, toPay);
            totalPaid += toPay;
            remaining -= toPay;
        }
        return totalPaid;
    }

    public virtual double BeginBalance(DateTime cfDate)
    {
        var cashflow = GetCashflow(cfDate);
        return cashflow.BeginBalance;
    }

    public double CurrentBalance(DateTime cfDate)
    {
        return Balance;
    }

    /// <summary>
    /// True when every tranche in this class is ExcessInterest (the XS /
    /// monthly-excess-cashflow strip). Such a class has no principal — its
    /// "balance" is the pool notional it earns interest on (reset each period by
    /// the notional settle), so it can only absorb losses out of the excess
    /// spread it sweeps, never via a principal writedown.
    /// </summary>
    public bool IsExcessInterest =>
        DynamicTranches.Count > 0 &&
        DynamicTranches.All(dt => dt.Tranche.CouponTypeEnum == CouponType.ExcessInterest);

    /// <summary>
    /// True when every tranche in this class is the REMIC Residual (Class R) —
    /// the terminal catch-all. It has no principal to write down either.
    /// </summary>
    public bool IsResidual =>
        DynamicTranches.Count > 0 &&
        DynamicTranches.All(dt => dt.Tranche.CouponTypeEnum == CouponType.Residual);

    public double WritedownCapacity(DateTime cfDate)
    {
        // Neither the excess-spread strip nor the REMIC residual has principal to
        // write down, so a writedown allocation must cascade past them to the
        // funded bonds rather than be consumed against their notional balance.
        return IsExcessInterest || IsResidual ? 0.0 : CurrentBalance(cfDate);
    }

    /// <summary>
    ///     This period's Interest Accrual Amount for the class, GROSS of any Modification Loss
    ///     Amount already allocated against it — the cap an interest rung of a Modification Loss
    ///     Priority reads (Class Coupon x Class Notional Amount immediately prior to the Payment
    ///     Date x Day Count Fraction).
    ///
    ///     Gross rather than net because it is a CAPACITY, not an amount outstanding: a rung is
    ///     visited once per period, and netting what this same period's rung has already booked
    ///     would make the cap depend on the order the step happened to read it in.
    /// </summary>
    public double ModificationInterestAccrual(DateTime cfDate, IRateProvider rateProvider,
        IEnumerable<DynamicTranche> allTranches)
    {
        if (DynamicTranches == null || DynamicTranches.Count == 0)
            return 0;
        var allTranchesList = allTranches?.ToList();
        return DynamicTranches.Sum(dynTran =>
            dynTran.Interest(dynTran.GetCashflow(cfDate), rateProvider, allTranchesList));
    }

    /// <summary>
    ///     Book a Modification Loss Amount against this class's Interest Accrual Amount, split
    ///     across its tranches by balance the way a writedown is. Reduces the interest the class
    ///     is paid; leaves its Class Notional Amount, and therefore credit enhancement, untouched.
    /// </summary>
    public void BookModificationInterestLoss(DateTime cfDate, double amount,
        IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches)
    {
        if (amount <= 0 || DynamicTranches == null || DynamicTranches.Count == 0)
            return;

        // Split by INTEREST ACCRUAL AMOUNT, not by balance: "any Modification Loss Amount that is
        // allocable in the sixth or seventh priority ... will be allocated to reduce the Interest
        // Payment Amounts ... pro rata, based on their Interest Accrual Amounts". The two bases
        // coincide only when the tranches share a coupon, which is exactly what an exchangeable /
        // MACR combination does not do — and a class holding several tranches is usually holding
        // them BECAUSE their coupons differ.
        // Stamp the CLASS row too. The notional counterpart deliberately stamps both levels, and
        // the controller serializes both collections through the same DTO — so stamping only the
        // tranche rows gave a class-row reader the notional bite and never the interest one.
        GetCashflow(cfDate).ModificationLoss += amount;

        var allTranchesList = allTranches?.ToList();
        var accruals = DynamicTranches
            .ToDictionary(dt => dt, dt => Math.Max(dt.Interest(dt.GetCashflow(cfDate), rateProvider,
                allTranchesList), 0));
        var total = accruals.Values.Sum();

        foreach (var dynTran in DynamicTranches)
        {
            // Nothing accruing anywhere in the class: fall back to balance, then to an even
            // split, so the amount is booked somewhere rather than silently vanishing.
            var share = total > 0
                ? accruals[dynTran] / total
                : FallbackShare(dynTran);
            dynTran.GetCashflow(cfDate).ModificationLoss += amount * share;
        }
    }

    private double FallbackShare(DynamicTranche dynTran)
    {
        var balanceTotal = DynamicTranches.Sum(dt => dt.Balance);
        return balanceTotal > 0 ? dynTran.Balance / balanceTotal : 1.0 / DynamicTranches.Count;
    }

    /// <summary>
    ///     Increase the Class Notional Amount by <paramref name="amount" /> — the receiving half
    ///     of a modification notional bite, which the document makes a TRANSFER up the stack
    ///     rather than a destruction ("the Class Notional Amount for the Class A-H Reference
    ///     Tranche will be increased by the sum of amounts included in the first, third, fifth,
    ///     eighth, ninth, eleventh and thirteenth priorities above").
    ///
    ///     Deliberately NOT <see cref="Writeup" />, which reverses a previous writedown and
    ///     refuses to go past CumWritedown: this class has typically never been written down, so
    ///     Writeup would throw on the first bite and take the whole axis out. It is also not a
    ///     write-up in substance — CumWritedown must not go negative here, because a later
    ///     genuine Tranche Write-up would then have phantom room.
    /// </summary>
    public TrancheCashflow IncreaseNotional(DateTime cashflowDate, double amount)
    {
        if (double.IsNaN(amount) || double.IsInfinity(amount))
            throw new DealModelingException(Tranche.DealName,
                $"Invalid notional increase '{amount}' requested for class {Tranche.TrancheName} on {cashflowDate:MM/dd/yyyy}");

        var adjDate = AdjustedCashflowDate(cashflowDate);
        if (!Cashflows.TryGetValue(adjDate, out var cashflow))
        {
            cashflow = new TrancheCashflow(adjDate, Tranche);
            cashflow.BeginBalance = Balance;

            var prevCf = GetPrevCashflow(cashflowDate);
            if (prevCf != null)
                cashflow.AccumInterestShortfall = prevCf.AccumInterestShortfall;
        }

        var beginBal = Balance;
        SetBalance(Balance + amount);
        // Reported as a NEGATIVE modification writedown: the field is "what the modification did
        // to this class's notional", and on this class it added to it. Keeping one signed field
        // means the ladder's modification writedowns sum to zero across the deal, which is the
        // invariant a reader can check.
        cashflow.ModificationWritedown -= amount;
        cashflow.Factor = Factor();
        cashflow.Balance = Balance;
        cashflow.CumWritedown = CumWritedown;
        cashflow.CreditSupport = CreditSupport(cashflowDate);
        Cashflows[adjDate] = cashflow;

        AllocateNotionalIncreaseToTranches(cashflowDate, amount, beginBal);
        return cashflow;
    }

    private void AllocateNotionalIncreaseToTranches(DateTime cfDate, double amount, double beginBal)
    {
        if (DynamicTranches == null || !DynamicTranches.Any())
            return;

        foreach (var dynTran in DynamicTranches)
        {
            double allocPct;
            if (Math.Abs(beginBal) < 0.01)
            {
                if (Math.Abs(Tranche.OriginalBalance) < double.Epsilon)
                    continue;
                allocPct = dynTran.Tranche.OriginalBalance / Tranche.OriginalBalance;
            }
            else
            {
                allocPct = dynTran.Balance / beginBal;
            }

            dynTran.IncreaseNotional(cfDate, amount * allocPct);
        }
    }

    public bool IsLockedOut(DateTime cashflowDate)
    {
        var adjDate = AdjustedCashflowDate(cashflowDate);
        if (Cashflows.TryGetValue(adjDate, out var tcf))
            return tcf.IsLockedOut;
        return false;
    }

    public double LockedOutBalance(DateTime cfDate)
    {
        if (IsLockedOut(cfDate))
            return GetCashflow(cfDate).Balance;
        return 0;
    }

    public string Describe(int level)
    {
        var tabs = string.Concat(Enumerable.Repeat("\t", level));
        return $"{tabs}SINGLE('{Tranche.TrancheName}')";
    }

    public XElement DescribeXml()
    {
        var element = new XElement("Single");
        element.Add(new XAttribute("Class", Tranche.TrancheName));
        return element;
    }

    public HashSet<IPayable> Leafs()
    {
        var set = new HashSet<IPayable>();
        set.Add(this);
        return set;
    }

    public bool IsLeaf => true;

    public List<IPayable> GetChildren()
    {
        return null;
    }

    public virtual TrancheCashflow PayExpense(DateTime cashflowDate, double amount, double shortfall)
    {
        var adjDate = AdjustedCashflowDate(cashflowDate);

        if (!Cashflows.TryGetValue(adjDate, out var cashflow)) cashflow = new TrancheCashflow(adjDate, Tranche);

        cashflow.Expense += amount;
        cashflow.ExpenseShortfall += shortfall;
        Cashflows[adjDate] = cashflow;
        return cashflow;
    }

    public virtual TrancheCashflow GetCashflow(DateTime cashflowDate)
    {
        var adjDate = AdjustedCashflowDate(cashflowDate);
        if (Cashflows.TryGetValue(adjDate, out var tcf))
            return tcf;

        tcf = new TrancheCashflow(adjDate, Tranche);
        tcf.BeginBalance = Balance;
        tcf.Balance = Balance;

        var prevCf = GetPrevCashflow(cashflowDate);
        if (prevCf != null)
            tcf.AccumInterestShortfall = prevCf.AccumInterestShortfall;

        Cashflows[adjDate] = tcf;
        return tcf;
    }

    public virtual TrancheCashflow GetPrevCashflow(DateTime cashflowDate)
    {
        var prevDate = AdjustedCashflowDate(cashflowDate.AddMonths(-1));
        if (Cashflows.TryGetValue(prevDate, out var prevCf))
            return prevCf;
        return null;
    }

    public virtual void Pay(DateTime cashflowDate, double unshedPrin, double schedPrin)
    {
        if (double.IsNaN(unshedPrin) || double.IsNaN(schedPrin))
            throw new DealModelingException(Tranche.DealName,
                $"NaN principal detected for tranche {Tranche.TrancheName}: unshedPrin={unshedPrin}, schedPrin={schedPrin}");
        var adjDate = AdjustedCashflowDate(cashflowDate);

        var totalPrin = unshedPrin + schedPrin;
        double unschedFactor = 0;
        double schedFactor = 0;
        if (Math.Abs(totalPrin) > .01)
        {
            unschedFactor = unshedPrin / totalPrin;
            schedFactor = schedPrin / totalPrin;
        }

        // if (totalPrin > Balance + 1)
        // {
        //     var excess = totalPrin - Balance;
        //     var excessTol = Tranche.OriginalBalance * .00001;
        //     if (excess > excessTol) /*Data issue causing certain pari-passu tranches do not have identical factors causing lost cashflow. See checkin for more info 10/3/18 */
        //         throw new Exception(
        //             $"Trying to pay class {Tranche.TrancheName} with {totalPrin:#,###} but the balance is only {Balance:#,###}. This will cause lost cashflow of {totalPrin - Balance:#,###}. Proj date is {cashflowDate:yy-MM-dd}");
        //     unshedPrin -= excess * unschedFactor;
        //     schedPrin -= excess * schedFactor;
        //     totalPrin = unshedPrin + schedPrin;
        // }

        var beginBal = Balance;
        SetBalance(Balance - totalPrin);

        if (!Cashflows.TryGetValue(adjDate, out var cashflow))
        {
            cashflow = new TrancheCashflow(adjDate, Tranche);
            cashflow.BeginBalance = beginBal;

            var prevCf = GetPrevCashflow(cashflowDate);
            if (prevCf != null)
                cashflow.AccumInterestShortfall = prevCf.AccumInterestShortfall;
        }

        if (RecievesPrincipal())
        {
/*            if (cashflow.IsLockedOut && totalPrin > 0)
               throw new Exception($"Cannot pay {totalPrin} to {Tranche.TrancheName} because it is locked out");*/
            cashflow.UnscheduledPrincipal += totalPrin * unschedFactor;
            cashflow.ScheduledPrincipal += totalPrin * schedFactor;
        }

        cashflow.CreditSupport = CreditSupport(cashflowDate);
        cashflow.BeginCreditSupport = BeginCreditSupport(cashflowDate);
        cashflow.DetachmentPoint = DetachmentPoint(cashflowDate);
        cashflow.BeginDetachmentPoint = BeginDetachmentPoint(cashflowDate);
        cashflow.Thickness = Thickness();
        cashflow.BeginThickness = BeginThickness(cashflowDate);
        cashflow.Factor = Factor();
        cashflow.Balance = Balance;
        cashflow.CumWritedown = CumWritedown;
        Cashflows[adjDate] = cashflow;

        // Allocate principal to tranches proportionally
        AllocatePrincipalToTranches(cashflowDate, unshedPrin, schedPrin, beginBal);
    }

    /// <summary>
    /// Allocates principal payments to child tranches proportionally based on balance.
    /// This eliminates the need for TrancheAllocator.AllocatePrincipal for principal distribution.
    /// </summary>
    private void AllocatePrincipalToTranches(DateTime cfDate, double unshedPrin, double schedPrin, double beginBal)
    {
        if (DynamicTranches == null || !DynamicTranches.Any())
            return; // DynamicTranche has null DynamicTranches, so no recursion

        foreach (var dynTran in DynamicTranches)
        {
            double allocPct;
            if (Math.Abs(beginBal) < 0.01)
            {
                // Handle resurrection from zero balance - use original balance ratio
                if (Math.Abs(Tranche.OriginalBalance) < double.Epsilon)
                    continue;
                allocPct = dynTran.Tranche.OriginalBalance / Tranche.OriginalBalance;
            }
            else
            {
                allocPct = dynTran.Balance / beginBal;
            }

            dynTran.Pay(cfDate, unshedPrin * allocPct, schedPrin * allocPct);
        }
    }

    public TrancheCashflow Writeup(DateTime cashflowDate, double writeupAmt)
    {
        if (writeupAmt > CumWritedown + .01)
            Exceptions.PrincipalDistributionException(null, cashflowDate,
                $"Attempting to writeup class by {writeupAmt:#,###} but cumulative writedown is only {CumWritedown:#,###}");

        var adjDate = AdjustedCashflowDate(cashflowDate);

        if (!Cashflows.TryGetValue(adjDate, out var cashflow))
        {
            cashflow = new TrancheCashflow(adjDate, Tranche);
            cashflow.BeginBalance = Balance;

            var prevCf = GetPrevCashflow(cashflowDate);
            if (prevCf != null)
                cashflow.AccumInterestShortfall = prevCf.AccumInterestShortfall;
        }

        SetBalance(Balance + writeupAmt);
        if (RecievesPrincipal())
        {
            cashflow.Writedown -= writeupAmt;
            CumWritedown -= writeupAmt;
        }

        cashflow.CreditSupport = CreditSupport(cashflowDate);
        cashflow.Factor = Factor();
        cashflow.Balance = Balance;
        cashflow.CumWritedown = CumWritedown;
        Cashflows[adjDate] = cashflow;
        return cashflow;
    }

    public TrancheCashflow Writedown(DateTime cashflowDate, double writedownAmt)
    {
        // Reject non-finite input outright — silently clamping NaN/Infinity would
        // corrupt the balance and cascade into every downstream period.
        if (double.IsNaN(writedownAmt) || double.IsInfinity(writedownAmt))
            throw new DealModelingException(Tranche.DealName,
                $"Invalid writedown amount '{writedownAmt}' requested for class {Tranche.TrancheName} on {cashflowDate:MM/dd/yyyy}");

        // A class can only absorb writedowns up to its remaining balance. Any excess
        // (collateral losses exceeding the note, or losses at deal termination once
        // subordinates are already written to zero) is legitimately unabsorbed and is
        // capped here. This previously tripped a Debug.Assert that fail-fasted the
        // entire process on otherwise-valid deals when run as a Debug build.
        var totalWritedown = Math.Min(writedownAmt, Balance);

        var adjDate = AdjustedCashflowDate(cashflowDate);
        var beginBal = Balance;

        if (!Cashflows.TryGetValue(adjDate, out var cashflow))
        {
            cashflow = new TrancheCashflow(adjDate, Tranche);
            cashflow.BeginBalance = Balance;

            var prevCf = GetPrevCashflow(cashflowDate);
            if (prevCf != null)
                cashflow.AccumInterestShortfall = prevCf.AccumInterestShortfall;
        }

        SetBalance(Balance - totalWritedown);
        if (RecievesPrincipal())
        {
            cashflow.Writedown += totalWritedown;
            CumWritedown += totalWritedown;
        }

        cashflow.CreditSupport = CreditSupport(cashflowDate);
        cashflow.Factor = Factor();
        cashflow.Balance = Balance;
        cashflow.CumWritedown = CumWritedown;
        Cashflows[adjDate] = cashflow;

        // Allocate writedown to tranches proportionally
        AllocateWritedownToTranches(cashflowDate, totalWritedown, beginBal);

        return cashflow;
    }

    /// <summary>
    /// Allocates writedowns to child tranches proportionally based on balance.
    /// This eliminates the need for TrancheAllocator.AllocatePrincipal for writedown distribution.
    /// </summary>
    private void AllocateWritedownToTranches(DateTime cfDate, double writedownAmt, double beginBal)
    {
        if (DynamicTranches == null || !DynamicTranches.Any())
            return; // DynamicTranche has null DynamicTranches, so no recursion

        foreach (var dynTran in DynamicTranches)
        {
            double allocPct;
            if (Math.Abs(beginBal) < 0.01)
            {
                // Handle zero balance - use original balance ratio
                if (Math.Abs(Tranche.OriginalBalance) < double.Epsilon)
                    continue;
                allocPct = dynTran.Tranche.OriginalBalance / Tranche.OriginalBalance;
            }
            else
            {
                allocPct = dynTran.Balance / beginBal;
            }

            dynTran.Writedown(cfDate, writedownAmt * allocPct);
        }
    }

    public void Lockout(DateTime cashflowDate)
    {
        Lock(cashflowDate, true);
    }

    public void Unlock(DateTime cashflowDate)
    {
        Lock(cashflowDate, false);
    }

    public void SetAccretion(string accretionClass)
    {
        if (DealStructure.PayFromEnum != PayFromEnum.Accrual)
            throw new DealModelingException(Tranche.DealName,
                $"Unable to set accretion class to {accretionClass}. Accretion classes can only be set for accrual. Class {Tranche.TrancheName} is a {DealStructure.PayFromEnum}");

        DynAcrretionClass = DynamicGroup.ClassByName(accretionClass);

        if (DynAcrretionClass == null)
            throw new DealModelingException(Tranche.DealName,
                $"Unable to set accretion class to {accretionClass} because it does not exist!");

        DynAcrretionClass.DynAccrualClass = this;
    }

    /// <summary>
    ///     Specifies to set accrual class to payment phase. During payment phase the Z will start to recieve pricinpal and
    ///     will stop accruing
    /// </summary>
    public void SetPaymentPhase(bool paymentPhase)
    {
        IsInPaymentPhase = paymentPhase;
    }

    public void Lock(DateTime cashflowDate, bool lockedOut)
    {
        var adjDate = AdjustedCashflowDate(cashflowDate);
        if (!Cashflows.TryGetValue(adjDate, out var cashflow))
        {
            cashflow = new TrancheCashflow(adjDate, Tranche);
            cashflow.BeginBalance = Balance;
            cashflow.Balance = Balance;

            var prevCf = GetPrevCashflow(cashflowDate);
            if (prevCf != null)
                cashflow.AccumInterestShortfall = prevCf.AccumInterestShortfall;
        }

        cashflow.IsLockedOut = lockedOut;
        Cashflows[adjDate] = cashflow;
    }

    public void StartTrans(DateTime cfDate)
    {
        if (_transCf != null)
            return;

        var cf = GetCashflow(cfDate);
        if (Math.Abs(Balance - cf.Balance) >= .001)
            throw new DealModelingException(Tranche.DealName,
                $"Class {Tranche.TrancheName} live balance {Balance:#,##0.##} is out of sync with its cashflow balance {cf.Balance:#,##0.##} on {cfDate:MM/dd/yyyy}");
        _transCf = cf.Copy();
    }

    public void CommitTrans()
    {
        _transCf = null;
    }

    public void Rollback()
    {
        if (_transCf == null)
            throw new ArgumentException("No transaction to roll back");

        Cashflows[_transCf.CashflowDate] = _transCf;
        SetBalance(_transCf.Balance);
        _transCf = null;
    }

    public virtual double Factor()
    {
        return Balance / Tranche.OriginalBalance;
    }

    public virtual double CreditSupport(DateTime cashflowDate)
    {
        return CreditSupport();
    }

    public virtual double DetachmentPoint(DateTime cashflowDate)
    {
        return DetachmentPoint();
    }

    public virtual double CreditSupport()
    {
        var bal = DynamicGroup.Balance();
        return bal > 0 ? SubordinateBalance() / bal : 0;
    }

    public virtual double SubordinateBalance()
    {
        var subordinateBalance = DynamicGroup.SubordinateClasses(Tranche).Sum(dc => dc.Balance);
        var groupBal = DynamicGroup.Balance();
        if (groupBal > 0)
            return subordinateBalance;
        return 0;
    }

    public virtual double BeginCreditSupport(DateTime cfDate)
    {
        var subordinateBalance =
            DynamicGroup.SubordinateClasses(Tranche).Sum(dc => dc.GetCashflow(cfDate).BeginBalance);
        return subordinateBalance / DynamicGroup.DealClasses.Sum(dc => dc.GetCashflow(cfDate).BeginBalance);
    }

    public virtual double Thickness()
    {
        if (DealStructure == null)
            return 0;
        double balance;
        if (DealStructure.ExchangableTranche != null)
        {
            var exchClasses = DynamicGroup.ClassesByNameOrTag(DealStructure.ExchangableTranche)
                .Select(dc => dc.DealStructure.SubordinationOrder).Distinct().ToList();
            balance = DynamicGroup.DealClasses.Where(dc => exchClasses.Contains(dc.DealStructure.SubordinationOrder))
                .Sum(dc => dc.Balance);
        }
        else
        {
            balance = DynamicGroup.DealClasses
                .Where(dc => dc.DealStructure.SubordinationOrder == DealStructure.SubordinationOrder)
                .Sum(dc => dc.Balance);
        }

        var thickness = balance / DynamicGroup.Balance();

        return thickness;
    }

    public virtual double BeginThickness(DateTime cfDate)
    {
        if (DealStructure == null)
            return 0;
        double beginBalance;
        if (DealStructure.ExchangableTranche != null)
        {
            var exchClasses = DynamicGroup.ClassesByNameOrTag(DealStructure.ExchangableTranche)
                .Select(dc => dc.DealStructure.SubordinationOrder).Distinct().ToList();
            beginBalance = DynamicGroup.DealClasses
                .Where(dc => exchClasses.Contains(dc.DealStructure.SubordinationOrder))
                .Sum(dc => dc.GetCashflow(cfDate).BeginBalance);
        }
        else
        {
            beginBalance = DynamicGroup.DealClasses
                .Where(dc => dc.DealStructure.SubordinationOrder == DealStructure.SubordinationOrder)
                .Sum(dc => dc.GetCashflow(cfDate).BeginBalance);
        }

        var thickness = beginBalance / DynamicGroup.DealClasses.Sum(dc => dc.GetCashflow(cfDate).BeginBalance);

        return thickness;
    }


    public virtual double DetachmentPoint()
    {
        return CreditSupport() + Thickness();
    }

    public virtual double BeginDetachmentPoint(DateTime cfDate)
    {
        return BeginCreditSupport(cfDate) + BeginThickness(cfDate);
    }

    public override string ToString()
    {
        return $"{Tranche} - Balance:{Balance:#,###}";
    }

    public bool IsExchangable()
    {
        return Tranche.TrancheTypeEnum == TrancheTypeEnum.Exchanged || DealStructure.ExchangableTranche != null;
    }

    public bool IsProRata()
    {
        return DealStructure.PayFromEnum == PayFromEnum.ProRata;
    }

    internal void SetBalance(double balance)
    {
        Balance = balance;
        if (Balance < .01)
            Balance = 0;
    }

    public virtual DateTime AdjustedCashflowDate(DateTime cashflowDate)
    {
        return cashflowDate;
    }

    public virtual bool RecievesPrincipal()
    {
        return true;
    }

    public void AddShortfallInterestSupport(string className)
    {
        ShortfallInterestSupport.Add(className);
    }

    public void ClearShortfallInterestSupport()
    {
        ShortfallInterestSupport.Clear();
    }
}