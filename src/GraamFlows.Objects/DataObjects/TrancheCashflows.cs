using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Objects.DataObjects;

public class TrancheCashflows : ICashflowStream
{
    public TrancheCashflows(ITranche tranche, IDayCounter dayCounter, IAssumptionMill assumps,
        Dictionary<DateTime, TrancheCashflow> trancheCashflows, DateTime startAccPeriod)
    {
        Cashflows = trancheCashflows;
        Tranche = tranche;
        StartAccrualPeriod = startAccPeriod;

        // get balance at time of first entitled cashflow
        Balance = Tranche.OriginalBalance * Tranche.Factor;
        foreach (var cf in trancheCashflows)
        {
            if (cf.Value.CashflowDate >= assumps.SettleDate)
            {
                Balance = cf.Value.BeginBalance;
                break;
            }

            if (cf.Value.CashflowDate > StartAccrualPeriod)
                StartAccrualPeriod = cf.Value.CashflowDate;
        }

        Frequency = FrequencyTypeEnum.Semiannual;
        Compounding = assumps.CompoundingMethod;

        var settleDate = assumps.SettleDate;
        if (settleDate < tranche.FirstSettleDate)
            settleDate = tranche.FirstSettleDate;

        SettleDate = settleDate;
        DayCounter = dayCounter;
    }

    private ITranche Tranche { get; }

    public Dictionary<DateTime, TrancheCashflow> Cashflows { get; }

    public CompoundingTypeEnum Compounding { get; }
    public FrequencyTypeEnum Frequency { get; }
    public double Balance { get; }

    IList<ICashflow> ICashflowStream.Cashflows
    {
        get
        {
            return Cashflows.Select(cf => (ICashflow)new CashflowStreamItem(cf.Key, cf.Value.TotalCashflow(),
                cf.Value.IndexValue, cf.Value.TotalPrincipal(), cf.Value.Interest, cf.Value.Balance,
                cf.Value.BeginBalance)).OrderBy(cf => cf.CashflowDate).ToList();
        }
    }

    public DateTime SettleDate { get; }

    public DateTime StartAccrualPeriod { get; }

    public IDayCounter DayCounter { get; }

    public int PayDelay => Tranche.PayDelay;
    public bool IsIo => Tranche.CashflowTypeEnum == CashflowType.InterestOnly;

    public double Writedown()
    {
        if (!Cashflows.Any())
            return 0;

        var totalWritedown = Cashflows.Sum(t => t.Value.Writedown);
        return totalWritedown;
    }

    public double WritedownPct()
    {
        if (!Cashflows.Any())
            return 0;

        var totalWritedown = Writedown();
        return totalWritedown / Balance;
    }

    private class CashflowStreamItem : ICashflow
    {
        public CashflowStreamItem(DateTime cfdate, double cf, double indexValue, double principal, double interest,
            double balance, double prevBal)
        {
            CashflowDate = cfdate;
            Cashflow = cf;
            IndexValue = indexValue;
            Principal = principal;
            Interest = interest;
            Balance = balance;
            PrevBalance = prevBal;
        }

        public DateTime CashflowDate { get; }
        public double Cashflow { get; }
        public double IndexValue { get; }
        public double Interest { get; }
        public double Principal { get; }
        public double Balance { get; }
        public double PrevBalance { get; }
    }
}