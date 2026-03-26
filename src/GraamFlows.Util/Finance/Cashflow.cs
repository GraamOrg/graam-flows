using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Util.Calender.DayCounters;
using GraamFlows.Util.MathUtil;
using GraamFlows.Util.Solvers1D;
using GraamFlows.Util.TermStructure;

namespace GraamFlows.Util.Finance;

public class Cashflow
{
    private readonly IMarketRates _marketRates;

    public Cashflow(ICashflowStream cashflowStream, IMarketRates marketRates)
    {
        CashFlowStream = cashflowStream;
        _marketRates = marketRates;
    }

    private ICashflowStream CashFlowStream { get; }

    public double PriceFromSpread(CurveType curveType, double spread = 0)
    {
        var curve = GetCurve(curveType);
        return PriceFromSpread(curve, spread);
    }

    public double PriceFromSpread(string curveName, double spread = 0)
    {
        var curve = GetCurve(curveName);
        return PriceFromSpread(curve, spread);
    }

    public double PriceFromSpread(ITermStructure curve, double spread = 0)
    {
        var s = spread * .0001;
        return NPV(CashFlowStream, cf => curve.GetRate(CashFlowStream.DayCounter, cf.CashflowDate),
            CashFlowStream.SettleDate, s);
    }

    public double SpreadFromPrice(CurveType curveType, double price)
    {
        var curve = GetCurve(curveType);
        return SpreadFromPriceInternal(curve, price);
    }

    public double SpreadFromPrice(string curveType, double price)
    {
        var curve = GetCurve(curveType);
        return SpreadFromPriceInternal(curve, price);
    }

    public double ModifiedDuration(CurveType curveType, double initPrice, double initSpread, double shockSize)
    {
        var curve = GetCurve(curveType);
        return ModifiedDuration(curve, initPrice, initSpread, shockSize);
    }

    public double ModifiedDuration(string curveType, double initPrice, double initSpread, double shockSize)
    {
        var curve = GetCurve(curveType);
        return ModifiedDuration(curve, initPrice, initSpread, shockSize);
    }

    public double ModifiedDuration(ITermStructure curve, double initPrice, double initSpread, double shockSize)
    {
        if (initSpread <= -10000)
            return 0;
        var pxu = PriceFromSpread(curve, initSpread + shockSize / 2);
        var pxd = PriceFromSpread(curve, initSpread - shockSize / 2);
        var dur = (pxd - pxu) / initPrice / (shockSize * .0001);
        return dur;
    }

    private double SpreadFromPriceInternal(ITermStructure termStructure, double price)
    {
        // Check if there are any cashflows after settle date
        var futureCashflows = CashFlowStream.Cashflows
            .Where(cf => cf.CashflowDate >= CashFlowStream.SettleDate && cf.Cashflow > 0)
            .ToList();

        if (!futureCashflows.Any())
        {
            var lastCfDate = CashFlowStream.Cashflows.Any()
                ? CashFlowStream.Cashflows.Max(cf => cf.CashflowDate).ToString("yyyy-MM-dd")
                : "none";
            throw new InvalidOperationException(
                $"No cashflows after settle date {CashFlowStream.SettleDate:yyyy-MM-dd}. " +
                $"Last cashflow date: {lastCfDate}. " +
                $"Adjust settle date to be on or before the first cashflow date.");
        }

        try
        {
            var solver = new Brent();
            solver.SetMaxEvaluations(1000);
            var objFunction = new SimpleRealFunction1D(n =>
            {
                return NPV(CashFlowStream, cf => termStructure.GetRate(CashFlowStream.DayCounter, cf.CashflowDate),
                    CashFlowStream.SettleDate, n) - price;
            });
            var spread = solver.Solve(objFunction, .0000001, .10, -1, 10);
            return spread * 10000;
        }
        catch (Exception)
        {
            return -10000;
        }
    }

    private ITermStructure GetCurve(CurveType curveType)
    {
        if (curveType == CurveType.DiscountMargin)
        {
            var rates = CashFlowStream.Cashflows.Select(cf => new InterestRate(
                CashFlowStream.DayCounter.YearFraction(CashFlowStream.SettleDate, cf.CashflowDate), cf.IndexValue * .01,
                CashFlowStream.DayCounter, CompoundingTypeEnum.Simple /*Simple compounding for DM calc*/,
                CashFlowStream.Frequency));
            var ts = new TermStructure.TermStructure(CashFlowStream.SettleDate, rates);
            return ts;
        }

        return _marketRates.GetTermStructure(curveType);
    }

    private ITermStructure GetCurve(string curve)
    {
        return _marketRates.TermStructure[curve];
    }

    public double YieldFromPrice(double price)
    {
        // Check if there are any cashflows after settle date
        var futureCashflows = CashFlowStream.Cashflows
            .Where(cf => cf.CashflowDate >= CashFlowStream.SettleDate && cf.Cashflow > 0)
            .ToList();

        if (!futureCashflows.Any())
        {
            var lastCfDate = CashFlowStream.Cashflows.Any()
                ? CashFlowStream.Cashflows.Max(cf => cf.CashflowDate).ToString("yyyy-MM-dd")
                : "none";
            throw new InvalidOperationException(
                $"No cashflows after settle date {CashFlowStream.SettleDate:yyyy-MM-dd}. " +
                $"Last cashflow date: {lastCfDate}. " +
                $"Adjust settle date to be on or before the first cashflow date.");
        }

        var solver = new Brent();
        solver.SetMaxEvaluations(1000);
        var objFunction = new SimpleRealFunction1D(n =>
        {
            var rate = new InterestRate(n, CashFlowStream.DayCounter, CashFlowStream.Compounding,
                CashFlowStream.Frequency);
            return NPV(CashFlowStream, cf => rate, CashFlowStream.SettleDate) - price;
        });
        try
        {
            var yield = solver.Solve(objFunction, .0000001, .10, -1, 10);
            return yield * 100;
        }
        catch
        {
            // cannot solve, probably because out of range.
            return -100;
        }
    }

    public double WeightedAverageLife()
    {
        var dayCount = new ActualActualISDA();
        var cashflows = CashFlowStream.Cashflows
            .Where(cf => cf.CashflowDate >= CashFlowStream.SettleDate && cf.Principal >= 0).Select(cf => new
            {
                Period = dayCount.YearFraction(CashFlowStream.SettleDate, cf.CashflowDate),
                Cashflow = CashFlowStream.IsIo ? cf.PrevBalance - cf.Balance : cf.Principal
            }).ToList();

        var totalCf = cashflows.Sum(cf => cf.Cashflow);
        if (totalCf < .01)
            return 0;

        var wal = cashflows.Sum(cf => cf.Cashflow * cf.Period) / totalCf;
        return wal;
    }

    public double BalanceWeightedAverageLife()
    {
        var dayCount = new Thirty360Us();
        var cashflows = CashFlowStream.Cashflows
            .Where(cf => cf.CashflowDate >= CashFlowStream.SettleDate && cf.Principal >= 0).Select(cf => new
            {
                Period = dayCount.YearFraction(CashFlowStream.SettleDate, cf.CashflowDate),
                Cashflow = cf.PrevBalance - cf.Balance
            }).ToList();

        var totalCf = cashflows.Sum(cf => cf.Cashflow);
        if (totalCf < .01)
            return 0;

        var wal = cashflows.Sum(cf => cf.Cashflow * cf.Period) / totalCf;
        return wal;
    }

    public double[] PrinWindowMos()
    {
        var prinWin = PrinWindowYear();
        prinWin[0] *= 12.0;
        prinWin[1] *= 12.0;
        return prinWin;
    }

    public double[] PrinWindowYear()
    {
        var result = new double[2];
        var firstPrinDate = FirstPrinDate();
        if (firstPrinDate == DateTime.MinValue)
            return result;

        var lastPrinDate = LastPrinDate();

        var daysFromFirstCf =
            Math.Max(0, CashFlowStream.DayCounter.YearFraction(CashFlowStream.SettleDate, firstPrinDate));
        var daysFromLastCf =
            Math.Max(0, CashFlowStream.DayCounter.YearFraction(CashFlowStream.SettleDate, lastPrinDate));
        result[0] = daysFromFirstCf;
        result[1] = daysFromLastCf;
        return result;
    }

    public DateTime[] PrinWindowDate()
    {
        var result = new DateTime[2];
        var firstPrinDate = FirstPrinDate();
        if (firstPrinDate == DateTime.MinValue)
            return result;

        var lastPrinDate = LastPrinDate();
        result[0] = firstPrinDate;
        result[1] = lastPrinDate;
        return result;
    }

    public DateTime FirstPrinDate()
    {
        if (!CashFlowStream.Cashflows.Any())
            return DateTime.MinValue;

        if (!CashFlowStream.Cashflows.Any(cf => cf.Principal > 0))
            return DateTime.MinValue;

        var firstPrinDate = CashFlowStream.Cashflows.First(cf => cf.Principal > 0).CashflowDate;
        return firstPrinDate;
    }

    public DateTime LastPrinDate()
    {
        if (!CashFlowStream.Cashflows.Any())
            return DateTime.MinValue;

        if (!CashFlowStream.Cashflows.Any(cf => cf.Principal > 0))
            return DateTime.MinValue;

        var lastPrinDate = CashFlowStream.Cashflows.Last(cf => cf.Principal > 0).CashflowDate;
        return lastPrinDate;
    }

    public double PriceFromYield(double yield)
    {
        var rate = new InterestRate(yield * .01, CashFlowStream.DayCounter, CashFlowStream.Compounding,
            CashFlowStream.Frequency);
        var price = NPV(CashFlowStream, cf => rate, CashFlowStream.SettleDate);
        return price;
    }

    public double AccruedAmount()
    {
        var firstCashflow = CashFlowStream.Cashflows.FirstOrDefault(cf => cf.CashflowDate >= CashFlowStream.SettleDate);

        if (firstCashflow == null)
            return 0;

        if (CashFlowStream.StartAccrualPeriod == DateTime.MinValue)
            return 0;

        var delay = -CashFlowStream.PayDelay;
        var startAccPeriod = CashFlowStream.StartAccrualPeriod.AddDays(delay);
        var cfDate = firstCashflow.CashflowDate.AddDays(delay);

        if (startAccPeriod >= CashFlowStream.SettleDate)
            return 0;

        var accDays = CashFlowStream.DayCounter.DayCount(startAccPeriod, CashFlowStream.SettleDate);
        var accPeriod = CashFlowStream.DayCounter.DayCount(startAccPeriod, cfDate);
        var interest = firstCashflow.Interest;

        var accAmt = (double)accDays / accPeriod * interest;
        var accPx = 100 * accAmt / CashFlowStream.Balance;
        return accPx;
    }

    public static double NPV(ICashflowStream cashflowStream, Func<ICashflow, IInterestRate> intRateFunc,
        DateTime settlementDate, double s = 0)
    {
        if (!cashflowStream.Cashflows.Any())
            return 0.0;

        var npv = 0.0;
        var discount = 1.0;
        var lastDate = settlementDate;
        foreach (var cashflow in cashflowStream.Cashflows)
        {
            var cfDate = cashflow.CashflowDate;
            if (cfDate < settlementDate)
                continue;

            var ncf = cashflow.Cashflow / cashflowStream.Balance;
            if (cfDate != settlementDate) // dont discount if cashflow is happening on settlment date
            {
                var intRate = intRateFunc.Invoke(cashflow);
                var b = intRate.DiscountFactor(lastDate, cfDate, s);
                discount *= b;
            }

            lastDate = cfDate;
            npv += ncf * discount;
        }

        return npv * 100;
    }

    public static void PVCF(ICashflowStream cashflowStream, IDayCounter dayCounter,
        Func<ICashflow, IInterestRate> intRateFunc, DateTime settlementDate, out double[] pv, out double[] t)
    {
        if (!cashflowStream.Cashflows.Any())
        {
            pv = new double[0];
            t = new double[0];
            return;
        }

        var pvList = new List<double>();
        var tList = new List<double>();
        var discount = 1.0;
        var lastDate = settlementDate;

        foreach (var cashflow in cashflowStream.Cashflows)
        {
            var cfDate = cashflow.CashflowDate;
            if (cfDate < settlementDate)
            {
                pvList.Add(0);
                tList.Add(0);
                continue;
            }

            double term = 0;
            if (cfDate != settlementDate)
            {
                var intRate = intRateFunc.Invoke(cashflow);
                var b = intRate.DiscountFactor(lastDate, cfDate, 0);
                discount *= b;
                term = dayCounter.YearFraction(lastDate, cfDate);
            }

            pvList.Add(cashflow.Cashflow * discount);
            tList.Add(term);
            lastDate = cfDate;
        }

        pv = pvList.ToArray();
        t = tList.ToArray();
    }
}