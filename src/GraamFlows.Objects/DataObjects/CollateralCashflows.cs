namespace GraamFlows.Objects.DataObjects;

public class CollateralCashflows
{
    private readonly Dictionary<CfPeriodKey, PeriodCashflows> _aggregatedPeriodCashflows;
    private IList<PeriodCashflows> _periodCashflows;

    public CollateralCashflows(bool saveAssetCf)
    {
        AssetCashflows = new Dictionary<IAsset, AssetCashflows>();
        _aggregatedPeriodCashflows = new Dictionary<CfPeriodKey, PeriodCashflows>();
        SaveAssetCashflows = saveAssetCf;
    }

    public CollateralCashflows(IList<PeriodCashflows> cashflows)
    {
        _periodCashflows = cashflows;
    }

    public IList<PeriodCashflows> PeriodCashflows
    {
        get
        {
            if (_periodCashflows == null)
                _periodCashflows = GeneratePeriodCashflows();
            return _periodCashflows;
        }
    }

    public Dictionary<IAsset, AssetCashflows> AssetCashflows { get; }
    public bool SaveAssetCashflows { get; }

    public void AddAssetCashflow(AssetCashflows cashflows)
    {
        _periodCashflows = null;
        lock (this)
        {
            foreach (var cf in cashflows.Cashflows)
            {
                var key = new CfPeriodKey(cf.CashflowDate, cashflows.Asset.GroupNum);
                if (!_aggregatedPeriodCashflows.TryGetValue(key, out var periodCf))
                {
                    periodCf = new PeriodCashflows();
                    _aggregatedPeriodCashflows.Add(key, periodCf);
                }

                periodCf.WAM = (cf.Maturity * cf.BeginBalance + periodCf.WAM * periodCf.BeginBalance) /
                               (periodCf.BeginBalance + cf.BeginBalance);
                periodCf.WALA = (cf.Age * cf.BeginBalance + periodCf.WALA * periodCf.BeginBalance) /
                                (periodCf.BeginBalance + cf.BeginBalance);

                periodCf.CashflowDate = cf.CashflowDate;
                periodCf.GroupNum = cashflows.Asset.GroupNum;
                periodCf.ScheduledPrincipal += cf.ScheduledPrincipal;
                periodCf.BeginBalance += cf.BeginBalance;
                periodCf.Balance += cf.Balance;
                periodCf.UnscheduledPrincipal += cf.UnscheduledPrincipal;
                periodCf.Interest += cf.Interest;
                periodCf.NetInterest += cf.NetInterest;
                periodCf.ServiceFee += cf.ServiceFee;
                periodCf.DelinqBalance += cf.DelinqBalance;
                periodCf.UnAdvancedPrincipal += cf.UnAdvancedPrincipal;
                periodCf.UnAdvancedInterest += cf.UnAdvancedInterest;
                periodCf.AdvancedPrincipal += cf.AdvancedPrincipal;
                periodCf.AdvancedInterest += cf.AdvancedInterest;
                periodCf.DefaultedPrincipal += cf.DefaultedPrincipal;
                periodCf.RecoveryPrincipal += cf.RecoveryPrincipal;
                periodCf.AccumForbearance += cf.AccumForbearance;
                periodCf.ForbearanceRecovery += cf.ForbearanceRecovery;
                periodCf.ForbearanceLiquidated += cf.ForbearanceLiquidated;

                // wavgs
                periodCf.WAC = periodCf.Interest * 1200 / periodCf.BeginBalance;
                periodCf.NetWac = periodCf.NetInterest * 1200 / periodCf.BeginBalance;
            }

            if (SaveAssetCashflows)
                AssetCashflows.Add(cashflows.Asset, cashflows);
        }
    }

    /// <summary>
    ///     Add a pre-aggregated period cashflow directly (used by array-based runner).
    /// </summary>
    public void AddPeriodCashflow(PeriodCashflows periodCf)
    {
        _periodCashflows = null;
        lock (this)
        {
            var key = new CfPeriodKey(periodCf.CashflowDate, periodCf.GroupNum);
            if (_aggregatedPeriodCashflows.TryGetValue(key, out var existingCf))
            {
                // Merge with existing
                existingCf.ScheduledPrincipal += periodCf.ScheduledPrincipal;
                existingCf.BeginBalance += periodCf.BeginBalance;
                existingCf.Balance += periodCf.Balance;
                existingCf.UnscheduledPrincipal += periodCf.UnscheduledPrincipal;
                existingCf.Interest += periodCf.Interest;
                existingCf.NetInterest += periodCf.NetInterest;
                existingCf.ServiceFee += periodCf.ServiceFee;
                existingCf.DelinqBalance += periodCf.DelinqBalance;
                existingCf.UnAdvancedPrincipal += periodCf.UnAdvancedPrincipal;
                existingCf.UnAdvancedInterest += periodCf.UnAdvancedInterest;
                existingCf.AdvancedPrincipal += periodCf.AdvancedPrincipal;
                existingCf.AdvancedInterest += periodCf.AdvancedInterest;
                existingCf.DefaultedPrincipal += periodCf.DefaultedPrincipal;
                existingCf.RecoveryPrincipal += periodCf.RecoveryPrincipal;
                existingCf.AccumForbearance += periodCf.AccumForbearance;
                existingCf.ForbearanceRecovery += periodCf.ForbearanceRecovery;
                existingCf.ForbearanceLiquidated += periodCf.ForbearanceLiquidated;
                existingCf.WAM = periodCf.WAM;
                existingCf.WALA = periodCf.WALA;
                existingCf.WAC = existingCf.Interest * 1200 / existingCf.BeginBalance;
                existingCf.NetWac = existingCf.NetInterest * 1200 / existingCf.BeginBalance;
            }
            else
            {
                _aggregatedPeriodCashflows.Add(key, periodCf);
            }
        }
    }

    private IList<PeriodCashflows> GeneratePeriodCashflows()
    {
        lock (this)
        {
            if (!_aggregatedPeriodCashflows.Values.Any())
                return new List<PeriodCashflows>();

            ComputeAggregates(_aggregatedPeriodCashflows.Values);
            return _aggregatedPeriodCashflows.Values.ToList();
        }
    }

    public static void ComputeAggregates(ICollection<PeriodCashflows> periodCashflows)
    {
        foreach (var groupPeriodCf in periodCashflows.GroupBy(agg => agg.GroupNum))
        {
            var firstCashflow = groupPeriodCf.First();
            double cumDefault = 0, cumCollatLoss = 0;
            foreach (var periodCf in groupPeriodCf)
            {
                periodCf.VPR = 100 * (1 -
                                      Math.Pow(
                                          1 - periodCf.UnscheduledPrincipal /
                                          (periodCf.BeginBalance - periodCf.ScheduledPrincipal), 12));
                periodCf.CDR = 100 * (1 - Math.Pow(1 - periodCf.DefaultedPrincipal / periodCf.BeginBalance, 12));
                periodCf.SEV = 100 * (1 - periodCf.RecoveryPrincipal / periodCf.DefaultedPrincipal);
                periodCf.DQ = 100 * (periodCf.DelinqBalance / periodCf.Balance);

                if (double.IsNaN(periodCf.VPR))
                    periodCf.VPR = 0;

                if (double.IsNaN(periodCf.CDR))
                    periodCf.CDR = 0;

                if (double.IsNaN(periodCf.SEV))
                    periodCf.SEV = 0;

                if (double.IsNaN(periodCf.DQ))
                    periodCf.DQ = 0;

                // cum default
                cumDefault += periodCf.DefaultedPrincipal;
                periodCf.CumDefaultedPrincipal = cumDefault;
                periodCf.CumDefaultedPrincipalPct = cumDefault / firstCashflow.BeginBalance;

                // total collat loss
                periodCf.CollateralLoss = periodCf.DefaultedPrincipal - periodCf.RecoveryPrincipal;
                cumCollatLoss += periodCf.CollateralLoss;
                periodCf.CumCollateralLoss = cumCollatLoss;
                periodCf.CumCollateralLossPct = cumCollatLoss / firstCashflow.BeginBalance;
            }
        }
    }

    private struct CfPeriodKey
    {
        public CfPeriodKey(DateTime cfDate, string groupNum)
        {
            CashflowDate = cfDate;
            GroupNum = groupNum;
        }

        private bool Equals(CfPeriodKey other)
        {
            return CashflowDate.Equals(other.CashflowDate) && GroupNum == other.GroupNum;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            return obj is CfPeriodKey key && Equals(key);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var group = GroupNum;
                if (group == null)
                    group = "0";
                return (CashflowDate.GetHashCode() * 397) ^ group.GetHashCode();
            }
        }

        private DateTime CashflowDate { get; }
        private string GroupNum { get; }
    }
}