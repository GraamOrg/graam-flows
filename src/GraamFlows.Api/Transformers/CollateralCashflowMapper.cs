using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Collateral period cashflows to their wire form. One mapping for every endpoint that returns
///     collateral, so a field added to one cannot silently go missing from another.
/// </summary>
public static class CollateralCashflowMapper
{
    /// <summary>Map in the order given, numbering periods from 1.</summary>
    public static List<PeriodCashflowDto> ToDtos(IEnumerable<PeriodCashflows> periodCashflows)
    {
        var dtos = new List<PeriodCashflowDto>();
        var period = 0;
        foreach (var cf in periodCashflows)
        {
            period++;
            dtos.Add(new PeriodCashflowDto
            {
                Period = period,
                CashflowDate = cf.CashflowDate,
                GroupNum = cf.GroupNum ?? "0",
                BeginBalance = cf.BeginBalance,
                Balance = cf.Balance,
                ScheduledPrincipal = cf.ScheduledPrincipal,
                UnscheduledPrincipal = cf.UnscheduledPrincipal,
                Interest = cf.Interest,
                NetInterest = cf.NetInterest,
                ServiceFee = cf.ServiceFee,
                DefaultedPrincipal = cf.DefaultedPrincipal,
                RecoveryPrincipal = cf.RecoveryPrincipal,
                CollateralLoss = cf.CollateralLoss,
                DelinqBalance = cf.DelinqBalance,
                ForbearanceRecovery = cf.ForbearanceRecovery,
                ForbearanceLiquidated = cf.ForbearanceLiquidated,
                ForbearanceUnscheduled = cf.ForbearanceUnscheduled,
                AccumForbearance = cf.AccumForbearance,
                Wac = cf.WAC,
                Wam = cf.WAM,
                Wala = cf.WALA,
                Vpr = cf.VPR,
                Cdr = cf.CDR,
                Sev = cf.SEV,
                Dq = cf.DQ,
                CumDefaultedPrincipal = cf.CumDefaultedPrincipal,
                CumDefaultedPrincipalPct = cf.CumDefaultedPrincipalPct,
                CumCollateralLoss = cf.CumCollateralLoss,
                CumCollateralLossPct = cf.CumCollateralLossPct,
                UnAdvancedPrincipal = cf.UnAdvancedPrincipal,
                UnAdvancedInterest = cf.UnAdvancedInterest,
                AdvancedPrincipal = cf.AdvancedPrincipal,
                AdvancedInterest = cf.AdvancedInterest,
                Expenses = cf.Expenses
            });
        }

        return dtos;
    }
}
