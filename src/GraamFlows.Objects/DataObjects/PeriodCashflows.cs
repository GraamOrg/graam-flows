namespace GraamFlows.Objects.DataObjects;

public class PeriodCashflows
{
    public PeriodCashflows()
    {
    }

    private PeriodCashflows(DateTime cashflowDate, double scheduledPrincipal, double balance,
        double unscheduledPrincipal, double interest, double netInterest, double serviceFee,
        double wac, double netWac, double wam, double wala, double effectiveWac, double beginBalance,
        double defaultedPrincipal, double recoveryPrincipal, double vpr, double cdr, double sev, double dq,
        string groupNum,
        double cumDefaultedPrincipal, double cumDefaultedPrincipalPct, double cumCollateralLoss,
        double cumCollateralLossPct, double collateralLoss,
        double delinqBalance, double unAdvancedPrincipal, double unAdvancedInterest, double advancedPrincipal,
        double advancedInterest, double expenses,
        double accumForbearance, double forbearanceRecovery, double forbearanceLiquidated,
        double forbearanceUnscheduled)
    {
        CashflowDate = cashflowDate;
        ScheduledPrincipal = scheduledPrincipal;
        Balance = balance;
        UnscheduledPrincipal = unscheduledPrincipal;
        Interest = interest;
        NetInterest = netInterest;
        ServiceFee = serviceFee;
        WAC = wac;
        NetWac = netWac;
        WAM = wam;
        WALA = wala;
        EffectiveWac = effectiveWac;
        BeginBalance = beginBalance;
        DefaultedPrincipal = defaultedPrincipal;
        RecoveryPrincipal = recoveryPrincipal;
        VPR = vpr;
        CDR = cdr;
        SEV = sev;
        DQ = dq;
        GroupNum = groupNum;
        CumDefaultedPrincipal = cumDefaultedPrincipal;
        CumDefaultedPrincipalPct = cumDefaultedPrincipalPct;
        CumCollateralLoss = cumCollateralLoss;
        CumCollateralLossPct = cumCollateralLossPct;
        CollateralLoss = collateralLoss;
        DelinqBalance = delinqBalance;
        UnAdvancedPrincipal = unAdvancedPrincipal;
        UnAdvancedInterest = unAdvancedInterest;
        AdvancedPrincipal = advancedPrincipal;
        AdvancedInterest = advancedInterest;
        Expenses = expenses;
        AccumForbearance = accumForbearance;
        ForbearanceRecovery = forbearanceRecovery;
        ForbearanceLiquidated = forbearanceLiquidated;
        ForbearanceUnscheduled = forbearanceUnscheduled;
    }

    public DateTime CashflowDate { get; set; }
    public double ScheduledPrincipal { get; set; }
    public double Balance { get; set; }
    public double UnscheduledPrincipal { get; set; }
    public double Interest { get; set; }
    public double NetInterest { get; set; }
    public double ServiceFee { get; set; }
    public double WAC { get; set; }
    public double NetWac { get; set; }
    public double WAM { get; set; }
    public double WALA { get; set; }
    public double EffectiveWac { get; set; }
    public double BeginBalance { get; set; }
    public double DefaultedPrincipal { get; set; }
    public double RecoveryPrincipal { get; set; }
    public double DelinqBalance { get; set; }
    public double UnAdvancedPrincipal { get; set; }
    public double UnAdvancedInterest { get; set; }
    public double AdvancedPrincipal { get; set; }
    public double AdvancedInterest { get; set; }
    public double VPR { get; set; }
    public double CDR { get; set; }
    public double SEV { get; set; }
    public double DQ { get; set; }
    public string GroupNum { get; set; }
    public double CumDefaultedPrincipal { get; set; }
    public double CumDefaultedPrincipalPct { get; set; }
    public double CumCollateralLoss { get; set; }
    public double CumCollateralLossPct { get; set; }
    public double CollateralLoss { get; set; }
    public double Expenses { get; set; }
    public double AccumForbearance { get; set; }
    public double ForbearanceRecovery { get; set; }
    public double ForbearanceLiquidated { get; set; }
    public double ForbearanceUnscheduled { get; set; }

    public double TotalCashflow()
    {
        return ScheduledPrincipal + UnscheduledPrincipal + Interest + RecoveryPrincipal + ForbearanceRecovery +
               ForbearanceUnscheduled;
    }

    public void DebitPrin(double prin)
    {
        if (Math.Abs(prin) < .001)
            return;

        var totalPrin = ScheduledPrincipal + UnscheduledPrincipal;
        if (Math.Abs(totalPrin) < .001)
            return;

        UnscheduledPrincipal -= UnscheduledPrincipal / totalPrin * prin;
        ScheduledPrincipal -= ScheduledPrincipal / totalPrin * prin;
    }

    public PeriodCashflows Clone()
    {
        return new PeriodCashflows(CashflowDate, ScheduledPrincipal, Balance, UnscheduledPrincipal, Interest,
            NetInterest, ServiceFee, WAC, NetWac, WAM, WALA, EffectiveWac, BeginBalance,
            DefaultedPrincipal, RecoveryPrincipal, VPR, CDR, SEV, DQ, GroupNum, CumDefaultedPrincipal,
            CumDefaultedPrincipalPct, CumCollateralLoss, CumCollateralLossPct, CollateralLoss,
            DelinqBalance, UnAdvancedPrincipal, UnAdvancedInterest, AdvancedPrincipal, AdvancedInterest, Expenses,
            AccumForbearance, ForbearanceRecovery, ForbearanceLiquidated, ForbearanceUnscheduled);
    }

    public void Add(PeriodCashflows periodCf)
    {
        ScheduledPrincipal += periodCf.ScheduledPrincipal;
        UnscheduledPrincipal += periodCf.UnscheduledPrincipal;
        Interest += periodCf.Interest;
        NetInterest += periodCf.NetInterest;
        RecoveryPrincipal += periodCf.RecoveryPrincipal;
        ForbearanceRecovery += periodCf.ForbearanceRecovery;
        ForbearanceUnscheduled += periodCf.ForbearanceUnscheduled;
        DefaultedPrincipal += periodCf.DefaultedPrincipal;
        ServiceFee += periodCf.ServiceFee;
        CollateralLoss += periodCf.CollateralLoss;
        Expenses += periodCf.Expenses;
    }
}