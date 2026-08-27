using GraamFlows.Objects.DataObjects;

namespace GraamFlows.AssetCashflowEngine;

/// <summary>
///     Period-indexed output arrays for aggregated cashflows.
///     All assets contribute to these arrays during processing.
/// </summary>
public class CashflowResultArrays
{
    public CashflowResultArrays(int maxPeriods)
    {
        MaxPeriods = maxPeriods;

        BeginBalance = new double[maxPeriods];
        Balance = new double[maxPeriods];
        ScheduledPrincipal = new double[maxPeriods];
        UnscheduledPrincipal = new double[maxPeriods];
        Interest = new double[maxPeriods];
        NetInterest = new double[maxPeriods];
        ServiceFee = new double[maxPeriods];
        DefaultedPrincipal = new double[maxPeriods];
        RecoveryPrincipal = new double[maxPeriods];
        DelinqBalance = new double[maxPeriods];
        UnAdvancedPrincipal = new double[maxPeriods];
        UnAdvancedInterest = new double[maxPeriods];
        AdvancedPrincipal = new double[maxPeriods];
        AdvancedInterest = new double[maxPeriods];
        ForbearanceRecovery = new double[maxPeriods];
        ForbearanceLiquidated = new double[maxPeriods];
        AccumForbearance = new double[maxPeriods];
        LiquidationPipelineBalance = new double[maxPeriods];
        WAM = new double[maxPeriods];
        WALA = new double[maxPeriods];
    }

    public int MaxPeriods { get; }
    public int NumberOfPeriods { get; set; }

    // Period-indexed result arrays
    public double[] BeginBalance { get; }
    public double[] Balance { get; }
    public double[] ScheduledPrincipal { get; }
    public double[] UnscheduledPrincipal { get; }
    public double[] Interest { get; }
    public double[] NetInterest { get; }
    public double[] ServiceFee { get; }
    public double[] DefaultedPrincipal { get; }
    public double[] RecoveryPrincipal { get; }
    public double[] DelinqBalance { get; }
    public double[] UnAdvancedPrincipal { get; }
    public double[] UnAdvancedInterest { get; }
    public double[] AdvancedPrincipal { get; }
    public double[] AdvancedInterest { get; }
    public double[] ForbearanceRecovery { get; }
    public double[] ForbearanceLiquidated { get; }
    public double[] AccumForbearance { get; }

    /// <summary>
    ///     Defaulted principal that has been recognised but not yet liquidated —
    ///     the balance sitting in the liquidation pipeline at the end of the period
    ///     (graam-harmony #4481 §2). A default at t liquidates at t + RecoveryLag,
    ///     so this holds the defaults from periods (t - lag, t].
    ///
    ///     Reported SEPARATELY and deliberately NOT summed into <see cref="Balance"/>.
    ///     Three consumers read Balance as performing collateral — reinvestment gap
    ///     sizing (CfCore), `oc_amt` (RulesHost) and the collateral-value call factor —
    ///     and CLO indentures are explicit that a defaulted obligation is excluded from
    ///     the OC numerator at par and re-admitted only at a rating-agency recovery
    ///     value. Folding pipeline par into Balance would under-buy reinvestment and
    ///     overstate OC. A consumer that wants the RMBS "reported balance" adds the
    ///     two together itself.
    /// </summary>
    public double[] LiquidationPipelineBalance { get; }

    public double[] WAM { get; }
    public double[] WALA { get; }

    /// <summary>
    ///     Convert the result arrays to a list of PeriodCashflows for downstream consumers.
    /// </summary>
    public List<PeriodCashflows> ToPeriodCashflows(DateTime firstProjectionDate, string groupNum)
    {
        var result = new List<PeriodCashflows>(NumberOfPeriods);

        for (var period = 0; period < NumberOfPeriods; period++)
        {
            var cf = new PeriodCashflows
            {
                CashflowDate = firstProjectionDate.AddMonths(period),
                GroupNum = groupNum,
                BeginBalance = BeginBalance[period],
                Balance = Balance[period],
                ScheduledPrincipal = ScheduledPrincipal[period],
                UnscheduledPrincipal = UnscheduledPrincipal[period],
                Interest = Interest[period],
                NetInterest = NetInterest[period],
                ServiceFee = ServiceFee[period],
                DefaultedPrincipal = DefaultedPrincipal[period],
                RecoveryPrincipal = RecoveryPrincipal[period],
                DelinqBalance = DelinqBalance[period],
                UnAdvancedPrincipal = UnAdvancedPrincipal[period],
                UnAdvancedInterest = UnAdvancedInterest[period],
                AdvancedPrincipal = AdvancedPrincipal[period],
                AdvancedInterest = AdvancedInterest[period],
                ForbearanceRecovery = ForbearanceRecovery[period],
                ForbearanceLiquidated = ForbearanceLiquidated[period],
                AccumForbearance = AccumForbearance[period],
                LiquidationPipelineBalance = LiquidationPipelineBalance[period],
                WAM = WAM[period],
                WALA = WALA[period]
            };

            // Compute WAC from interest/balance
            if (cf.BeginBalance > 0)
            {
                cf.WAC = cf.Interest * 1200 / cf.BeginBalance;
                cf.NetWac = cf.NetInterest * 1200 / cf.BeginBalance;
            }

            result.Add(cf);
        }

        return result;
    }

    /// <summary>
    ///     Determine the actual number of periods with data.
    /// </summary>
    public void ComputeNumberOfPeriods()
    {
        NumberOfPeriods = 0;
        for (var i = 0; i < MaxPeriods; i++)
        {
            // Scan for the LAST period carrying data rather than stopping at the
            // first empty one, and include RecoveryPrincipal — so a lagged
            // recovery (graam-harmony #3449) that lands after the pool has
            // amortized to zero (a gap of empty periods before it) is still
            // emitted instead of truncated.
            if (Balance[i] != 0 || ScheduledPrincipal[i] != 0 || Interest[i] != 0
                || DefaultedPrincipal[i] != 0 || RecoveryPrincipal[i] != 0)
                NumberOfPeriods = i + 1;
        }
    }
}