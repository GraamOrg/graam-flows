using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.RulesEngine;
using GraamFlows.Util;
using GraamFlows.Util.Calender;
using GraamFlows.Util.Calender.DayCounters;

namespace GraamFlows.Waterfall.MarketTranche;

public class DynamicTranche : DynamicClass
{
    /// <summary>
    ///     Set by RulesHost
    /// </summary>
    public bool IsInterest = false;

    public DynamicTranche(IFormulaExecutor formulaExecutor, DynamicGroup dynamicGroup, ITranche tranche,
        DateTime settleDate) : base(dynamicGroup, tranche)
    {
        FormulaExecutor = formulaExecutor;
        Calendar = Tranche.GetCalendar();
        DayCounter = Tranche.GetDayCounter();
        SettleDate = settleDate;
    }

    public Calendar Calendar { get; }
    public DayCounter DayCounter { get; }
    public DateTime SettleDate { get; }
    public DynamicClass ClassReference { get; set; }
    public IFormulaExecutor FormulaExecutor { get; }

    public virtual void PayInterest(TrancheCashflow trancheCashflow, IRateProvider rateProvider,
        IAssumptionMill assumps, IEnumerable<DynamicTranche> allTranches)
    {
        // get days from last accrual period
        var frac = YearFraction(trancheCashflow.CashflowDate);
        var balance = TrancheBalance(trancheCashflow);
        trancheCashflow.ResetSlope = ResetSlope();
        trancheCashflow.AccrualDays = AccuralDays(trancheCashflow.CashflowDate);
        var coupon = Coupon(rateProvider, trancheCashflow.CashflowDate, allTranches);
        if (IsInterest)
        {
            var interest = coupon;
            var effCoupon = interest / (balance * .01 * frac);
            if (double.IsNaN(effCoupon) || double.IsInfinity(effCoupon))
                effCoupon = 0;
            trancheCashflow.Interest = interest;
            trancheCashflow.Coupon = effCoupon;
            trancheCashflow.EffectiveCoupon = effCoupon;
            return;
        }

        trancheCashflow.Coupon = coupon;
        trancheCashflow.EffectiveCoupon = coupon;
        trancheCashflow.Interest = balance * coupon * .01 * frac;
        trancheCashflow.Interest += trancheCashflow.InterestShortfallPayback;

        if (Tranche.CouponTypeEnum == CouponType.Floating)
        {
            trancheCashflow.IndexValue =
                rateProvider.GetRate(FloaterIndex(), trancheCashflow.CashflowDate) * ResetSlope();
            trancheCashflow.FloaterIndex = FloaterIndex().ToString();
            trancheCashflow.FloaterMargin = FloaterSpread();
        }
    }

    public virtual void PayInterest(TrancheCashflow trancheCashflow, IRateProvider rateProvider,
        IAssumptionMill assumps, IEnumerable<DynamicTranche> allTranches, double interest)
    {
        // get days from last accrual period
        var frac = YearFraction(trancheCashflow.CashflowDate);
        var balance = TrancheBalance(trancheCashflow);
        trancheCashflow.ResetSlope = ResetSlope();
        trancheCashflow.AccrualDays = AccuralDays(trancheCashflow.CashflowDate);
        var coupon = Coupon(rateProvider, trancheCashflow.CashflowDate, allTranches);
        trancheCashflow.Coupon = coupon;
        var effCoupon = interest / (balance * .01 * frac);
        trancheCashflow.EffectiveCoupon = effCoupon;
        trancheCashflow.Interest = interest;
        trancheCashflow.Interest += trancheCashflow.InterestShortfallPayback;

        if (Tranche.CouponTypeEnum == CouponType.ResidualInterest)
        {
            trancheCashflow.Coupon = effCoupon;
            trancheCashflow.EffectiveCoupon = effCoupon;
        }
        else
        {
            var expectedInterest = balance * coupon * .01 * frac;
            trancheCashflow.InterestShortfall = Math.Max(0, expectedInterest - interest);
            trancheCashflow.AccumInterestShortfall += trancheCashflow.InterestShortfall;
        }

        if (Tranche.CouponTypeEnum == CouponType.Floating)
        {
            trancheCashflow.IndexValue = rateProvider.GetRate(FloaterIndex(), trancheCashflow.CashflowDate);
            trancheCashflow.FloaterIndex = FloaterIndex().ToString();
            trancheCashflow.FloaterMargin = FloaterSpread();
        }
    }

    public virtual void PaybackInterestShortfall(TrancheCashflow trancheCashflow, double interestShortfallPayback)
    {
        var balance = TrancheBalance(trancheCashflow);
        var frac = YearFraction(trancheCashflow.CashflowDate);
        trancheCashflow.Interest += interestShortfallPayback;
        trancheCashflow.InterestShortfallPayback += interestShortfallPayback;
        trancheCashflow.AccumInterestShortfall -= interestShortfallPayback;
        var effCoupon = trancheCashflow.Interest / (balance * .01 * frac);
        trancheCashflow.EffectiveCoupon = effCoupon;
    }

    public virtual double Interest(TrancheCashflow trancheCashflow, IRateProvider rateProvider,
        IEnumerable<DynamicTranche> allTranches)
    {
        var coupon = Coupon(rateProvider, trancheCashflow.CashflowDate, allTranches);
        var balance = TrancheBalance(trancheCashflow);
        var frac = YearFraction(trancheCashflow.CashflowDate);
        var interest = balance * coupon * .01 * frac;
        return interest;
    }

    public double Coupon(IRateProvider rateProvider, DateTime cfDate, IEnumerable<DynamicTranche> allTranches)
    {
        double coupon;
        if (Tranche.CouponTypeEnum == CouponType.Fixed)
        {
            coupon = Tranche.FixedCoupon;
        }
        else if (Tranche.CouponTypeEnum == CouponType.Floating)
        {
            var floaterIndex = FloaterIndex();
            var index = rateProvider.GetRate(floaterIndex, cfDate);
            var margin = FloaterSpread();
            coupon = margin + index * ResetSlope();

            if (Tranche.Cap > 0)
                coupon = Math.Min(Tranche.Cap, coupon);

            coupon = Math.Max(Tranche.Floor, coupon);
        }
        else if (Tranche.CouponTypeEnum == CouponType.None)
        {
            coupon = 0;
        }
        else if (Tranche.CouponTypeEnum == CouponType.TrancheWac)
        {
            var allTransList = allTranches.ToList();
            var wacTranches = ParseCouponFormula(Tranche.CouponFormula, allTransList)
                .Where(wc => wc.Cashflows.ContainsKey(cfDate)).ToList();
            var product = wacTranches.Sum(wacTran =>
                wacTran.Cashflows[cfDate].BeginBalance * wacTran.Cashflows[cfDate].Coupon);
            var totalBal = wacTranches.Sum(wacTran => wacTran.Cashflows[cfDate].BeginBalance);
            var wac = product / totalBal;
            if (double.IsNaN(wac))
                wac = 0;
            coupon = wac;
        }
        else if (Tranche.CouponTypeEnum == CouponType.Formula)
        {
            FormulaExecutor.ResetTrancheFormulas(this, rateProvider, cfDate, allTranches);
            var functionName = RulesBuilder.GetTrancheCpnFormulaName(Tranche);
            coupon = FormulaExecutor.EvaluateDouble(functionName);
        }
        else if (Tranche.CouponTypeEnum == CouponType.ResidualInterest)
        {
            coupon = 0;
        }
        else
        {
            throw new DealModelingException(Tranche.DealName,
                $"Coupon type {Tranche.CouponTypeEnum} for {Tranche.TrancheName} is unknown!");
        }

        return coupon;
    }

    public virtual IList<DynamicTranche> ParseCouponFormula(string couponFormula,
        IEnumerable<DynamicTranche> allTranches)
    {
        var tranches = couponFormula.Split(',').Select(t => t.Trim()).Distinct().ToList();
        var results = allTranches.Where(tran => tranches.Contains(tran.Tranche.TrancheName)).ToList();
        if (tranches.Count != results.Count)
            throw new DealModelingException(Tranche.DealName,
                $"Tranche {Tranche.TrancheName} has a WAC formula {couponFormula} but one of the tranches do not exist!");
        return results;
    }

    public override DateTime AdjustedCashflowDate(DateTime cashflowDate)
    {
        return AdjustCashflowDate(cashflowDate, Calendar, Tranche.BusinessDayConvention, DayCounter, Tranche.PayDay,
            Tranche.FirstPayDate);
    }

    public static DateTime AdjustCashflowDate(DateTime cashflowDate, Calendar calendar, string businessDayConv,
        DayCounter dayCounter, int payDay, DateTime firstPayDate)
    {
        DateTime adjCashflowDate;
        var c = CalendarFactory.GetBusinessDayConvention(businessDayConv);

        // interest always accrues the same for 30/360
        if (dayCounter is Thirty360Us || dayCounter is Thirty360E)
            adjCashflowDate = new DateTime(cashflowDate.Year, cashflowDate.Month, payDay);
        else
            adjCashflowDate = calendar.Adjust(cashflowDate, payDay, c);

        if (adjCashflowDate < firstPayDate)
            adjCashflowDate = firstPayDate;
        return adjCashflowDate;
    }

    public virtual int AccuralDays(DateTime cashflowDate)
    {
        var prevDate = PreviousPayDate(cashflowDate);
        var accDays = DayCounter.DayCount(prevDate, cashflowDate);
        return accDays;
    }

    public virtual DateTime PreviousPayDate(DateTime cashflowDate)
    {
        var busDayConv = CalendarFactory.GetBusinessDayConvention(Tranche.BusinessDayConvention);
        var datePrev = Calendar.AdvanceMonth(cashflowDate, Tranche.PayDay, -1, busDayConv);
        if (datePrev < Tranche.FirstSettleDate || Tranche.FirstPayDate > datePrev)
            datePrev = Tranche.FirstSettleDate;

        // For 30/360, normalize to PayDay for regular periods.
        // But for the first period (when datePrev == FirstSettleDate and settlement
        // day differs from PayDay), use the actual settlement date to get correct
        // short first-period accrual (e.g., Jan 22 to Feb 15 = 23 days, not 30).
        if (DayCounter is Thirty360Us || DayCounter is Thirty360E)
        {
            if (datePrev == Tranche.FirstSettleDate && datePrev.Day != Tranche.PayDay)
                return datePrev;
            return new DateTime(datePrev.Year, datePrev.Month, Tranche.PayDay);
        }

        return datePrev;
    }

    public virtual double YearFraction(DateTime cashflowDate)
    {
        var datePrev = PreviousPayDate(cashflowDate);
        var frac = DayCounter.YearFraction(datePrev, cashflowDate);
        return frac;
    }

    public virtual double TrancheBalance(TrancheCashflow cashflow)
    {
        return cashflow.BeginBalance;
    }

    public virtual double ResetSlope()
    {
        var resetSlope = Tranche.ResetSlope;
        if (double.IsNaN(resetSlope) || Math.Abs(resetSlope) < double.Epsilon)
            resetSlope = 1;
        return resetSlope;
    }

    public virtual double FloaterSpread()
    {
        return Tranche.FloaterSpread * .01;
    }

    public virtual MarketDataInstEnum FloaterIndex()
    {
        return Tranche.FloaterIndexEnum;
    }

    public override double CreditSupport(DateTime cashflowDate)
    {
        if (cashflowDate == DateTime.MinValue)
            return 0;
        return ClassReference.Cashflows.Single(cf => cf.Key == cashflowDate).Value.CreditSupport;
    }
}