namespace GraamFlows.Objects.DataObjects;

public class Cashflow : IAssetCashflow
{
    private double _scheduledPayment = -1;

    public Cashflow()
    {
        ScheduledPayment = -1;
    }

    public Cashflow(DateTime date)
    {
        CashflowDate = date;
    }

    public int Period { get; set; }
    public DateTime CashflowDate { get; set; }
    public double ScheduledPrincipal { get; set; }
    public double UnscheduledPrincipal { get; set; }
    public double Interest { get; set; }
    public double NetInterest { get; set; }
    public double ServiceFee { get; set; }
    public double Balance { get; set; }
    public double BeginBalance { get; set; }

    public double InterestRate => Math.Round(Interest / BeginBalance * 1200, 3);

    public double ScheduledPayment
    {
        get
        {
            var result = _scheduledPayment;
            if (result < 0)
                result = ScheduledPrincipal + Interest;
            return result;
        }

        set => _scheduledPayment = value;
    }

    public double DelinqBalance { get; set; }
    public double UnAdvancedPrincipal { get; set; }
    public double UnAdvancedInterest { get; set; }
    public double AdvancedPrincipal { get; set; }
    public double AdvancedInterest { get; set; }
    public double DefaultedPrincipal { get; set; }
    public double RecoveryPrincipal { get; set; }
    public double AccumForbearance { get; set; }
    public double ForbearanceRecovery { get; set; }
    public double ForbearanceLiquidated { get; set; }
    public double Age { get; set; }
    public double Maturity { get; set; }
    public double Principal => ScheduledPrincipal + UnscheduledPrincipal + RecoveryPrincipal;

    public void Assign(IAssetCashflow other)
    {
        Period = other.Period;
        CashflowDate = other.CashflowDate;
        ScheduledPrincipal = other.ScheduledPrincipal;
        UnscheduledPrincipal = other.UnscheduledPrincipal;
        Interest = other.Interest;
        NetInterest = other.NetInterest;
        ServiceFee = other.ServiceFee;
        Balance = other.Balance;
        BeginBalance = other.BeginBalance;
        DelinqBalance = other.DelinqBalance;
        UnAdvancedPrincipal = other.UnAdvancedPrincipal;
        UnAdvancedInterest = other.UnAdvancedInterest;
        AdvancedPrincipal = other.AdvancedPrincipal;
        AdvancedInterest = other.AdvancedInterest;
        DefaultedPrincipal = other.DefaultedPrincipal;
        RecoveryPrincipal = other.RecoveryPrincipal;
        AccumForbearance = other.AccumForbearance;
        ForbearanceRecovery = other.ForbearanceRecovery;
        ForbearanceLiquidated = other.ForbearanceLiquidated;
        Age = other.Age;
        Maturity = other.Maturity;
    }

    public void Aggregate(IAssetCashflow other)
    {
        ScheduledPrincipal += other.ScheduledPrincipal;
        UnscheduledPrincipal += other.UnscheduledPrincipal;
        Interest += other.Interest;
        NetInterest += other.NetInterest;
        ServiceFee += other.ServiceFee;
        DelinqBalance += other.DelinqBalance;
        UnAdvancedPrincipal += other.UnAdvancedPrincipal;
        UnAdvancedInterest += other.UnAdvancedInterest;
        AdvancedPrincipal += other.AdvancedPrincipal;
        AdvancedInterest += other.AdvancedInterest;
        DefaultedPrincipal += other.DefaultedPrincipal;
        RecoveryPrincipal += other.RecoveryPrincipal;
        Balance += other.Balance;
        BeginBalance += other.BeginBalance;
        AccumForbearance += other.AccumForbearance;
        ForbearanceRecovery += other.ForbearanceRecovery;
        ForbearanceLiquidated += other.ForbearanceLiquidated;
    }

    public override string ToString()
    {
        return $"{CashflowDate.ToShortDateString()},P:{ScheduledPrincipal:#,###},I:{Interest:#,###},B:{Balance:$#,###}";
    }
}