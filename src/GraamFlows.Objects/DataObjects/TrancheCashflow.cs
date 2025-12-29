namespace GraamFlows.Objects.DataObjects;

public class TrancheCashflow
{
    public TrancheCashflow()
    {
    }

    public TrancheCashflow(DateTime cashflowDate, ITranche tranche)
    {
        CashflowDate = cashflowDate;
        Tranche = tranche;
        TrancheName = tranche.TrancheName;
        IsLockedOut = false;
    }

    public TrancheCashflow(DateTime cashflowDate, string trancheName)
    {
        CashflowDate = cashflowDate;
        TrancheName = trancheName;
        IsLockedOut = false;
    }

    public TrancheCashflow(string trancheName, ITranche tranche, DateTime cashflowDate, double unscheduledPrincipal,
        double scheduledPrincipal, double balance,
        double beginBalance, double interest, double expense, double expenseShortfall, double factor,
        double creditSupport, double beginCreditSupport, double detachPoint, double beginDetachPoint,
        double thickness, double beginThickness, double writedown, double cumWritedown, double coupon, double effCpn,
        double resetSlope, string floaterIndex, double floaterMargin, double indexValue, int accrualDays,
        bool isLockedOut, double interestShortfall, double accumInterestShortfall, double interestShortfallPayback)
    {
        TrancheName = trancheName;
        Tranche = tranche;
        CashflowDate = cashflowDate;
        UnscheduledPrincipal = unscheduledPrincipal;
        ScheduledPrincipal = scheduledPrincipal;
        Balance = balance;
        BeginBalance = beginBalance;
        Interest = interest;
        Expense = expense;
        ExpenseShortfall = expenseShortfall;
        Factor = factor;
        CreditSupport = creditSupport;
        BeginCreditSupport = beginCreditSupport;
        DetachmentPoint = detachPoint;
        BeginDetachmentPoint = beginDetachPoint;
        Thickness = thickness;
        BeginThickness = beginThickness;
        Writedown = writedown;
        CumWritedown = cumWritedown;
        Coupon = coupon;
        EffectiveCoupon = effCpn;
        ResetSlope = resetSlope;
        FloaterIndex = floaterIndex;
        FloaterMargin = floaterMargin;
        IndexValue = indexValue;
        AccrualDays = accrualDays;
        IsLockedOut = isLockedOut;
        InterestShortfall = interestShortfall;
        AccumInterestShortfall = accumInterestShortfall;
        InterestShortfallPayback = interestShortfallPayback;
    }

    public string TrancheName { get; }
    public ITranche Tranche { get; }
    public DateTime CashflowDate { get; set; }
    public double UnscheduledPrincipal { get; set; }
    public double ScheduledPrincipal { get; set; }
    public double Balance { get; set; }
    public double BeginBalance { get; set; }
    public double Interest { get; set; }
    public double Expense { get; set; }
    public double ExpenseShortfall { get; set; }
    public double Factor { get; set; }
    public double CreditSupport { get; set; }
    public double DetachmentPoint { get; set; }
    public double BeginDetachmentPoint { get; set; }
    public double BeginCreditSupport { get; set; }
    public double Thickness { get; set; }
    public double BeginThickness { get; set; }
    public double Writedown { get; set; }
    public double CumWritedown { get; set; }
    public double Coupon { get; set; }
    public double EffectiveCoupon { get; set; }
    public double ResetSlope { get; set; }
    public string FloaterIndex { get; set; }
    public double FloaterMargin { get; set; }
    public double IndexValue { get; set; }
    public int AccrualDays { get; set; }
    public bool IsLockedOut { get; set; }
    public double InterestShortfall { get; set; }
    public double AccumInterestShortfall { get; set; }
    public double InterestShortfallPayback { get; set; }

    /// <summary>
    ///     Excess interest redirected to this tranche (e.g., OC tranche balance writeup for Auto ABS)
    /// </summary>
    public double ExcessInterest { get; set; }

    public TrancheCashflow Copy()
    {
        var tcf = new TrancheCashflow(TrancheName, Tranche, CashflowDate, UnscheduledPrincipal, ScheduledPrincipal,
            Balance, BeginBalance, Interest, Expense, ExpenseShortfall, Factor,
            CreditSupport, BeginCreditSupport, DetachmentPoint, BeginDetachmentPoint, Thickness, BeginThickness,
            Writedown, CumWritedown, Coupon, EffectiveCoupon, ResetSlope, FloaterIndex, FloaterMargin, IndexValue,
            AccrualDays, IsLockedOut, InterestShortfall, AccumInterestShortfall, InterestShortfallPayback);
        return tcf;
    }

    public double TotalCashflow()
    {
        return UnscheduledPrincipal + ScheduledPrincipal + Interest + Expense;
    }

    public double TotalPrincipal()
    {
        return UnscheduledPrincipal + ScheduledPrincipal;
    }

    public override string ToString()
    {
        return $"{CashflowDate:d} Prin:{ScheduledPrincipal + UnscheduledPrincipal:#,###} Bal:{Balance:#,###}";
    }
}