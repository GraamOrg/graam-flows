using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Objects.DataObjects;

public interface ICashflowStream
{
    CompoundingTypeEnum Compounding { get; }
    FrequencyTypeEnum Frequency { get; }
    double Balance { get; }
    IList<ICashflow> Cashflows { get; }
    DateTime SettleDate { get; }
    DateTime StartAccrualPeriod { get; }
    IDayCounter DayCounter { get; }
    int PayDelay { get; }
    bool IsIo { get; }
}

public interface ICashflow
{
    DateTime CashflowDate { get; }
    double Cashflow { get; }
    double IndexValue { get; }
    double Interest { get; }
    double Principal { get; }
    double Balance { get; }
    double PrevBalance { get; }
}

public class CashflowStreamImpl : ICashflowStream
{
    public CashflowStreamImpl()
    {
        Cashflows = new List<ICashflow>();
    }

    public CompoundingTypeEnum Compounding { get; set; }
    public FrequencyTypeEnum Frequency { get; set; }
    public double Balance { get; set; }
    public IList<ICashflow> Cashflows { get; set; }
    public DateTime SettleDate { get; set; }
    public DateTime StartAccrualPeriod { get; set; }
    public IDayCounter DayCounter { get; set; }
    public int PayDelay { get; set; }
    public bool IsIo { get; set; }
}

public class CashflowImpl : ICashflow
{
    public DateTime CashflowDate { get; set; }
    public double Cashflow { get; set; }
    public double IndexValue { get; set; }
    public double Interest { get; set; }
    public double Principal { get; set; }
    public double Balance { get; set; }
    public double PrevBalance { get; set; }
}
