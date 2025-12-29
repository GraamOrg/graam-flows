namespace GraamFlows.Objects.DataObjects;

public interface IAssetCashflow
{
    int Period { get; set; }
    DateTime CashflowDate { get; set; }
    double Principal { get; }
    double ScheduledPrincipal { get; set; }
    double UnscheduledPrincipal { get; set; }
    double Interest { get; set; }
    double NetInterest { get; set; }
    double ServiceFee { get; set; }
    double Balance { get; set; }
    double BeginBalance { get; set; }
    double InterestRate { get; }
    double ScheduledPayment { get; }
    double DelinqBalance { get; set; }
    double UnAdvancedPrincipal { get; set; }
    double UnAdvancedInterest { get; set; }
    double AdvancedPrincipal { get; set; }
    double AdvancedInterest { get; set; }
    double DefaultedPrincipal { get; set; }
    double RecoveryPrincipal { get; set; }
    double AccumForbearance { get; set; }
    double ForbearanceRecovery { get; set; }
    double ForbearanceLiquidated { get; set; }
    double Age { get; set; }
    double Maturity { get; set; }
    void Assign(IAssetCashflow other);
    void Aggregate(IAssetCashflow other);
}