namespace GraamFlows.Objects.DataObjects;

public class DealCashflows
{
    public DealCashflows()
    {
        ClassCashflows = new Dictionary<ITranche, TrancheCashflows>();
        TrancheCashflows = new Dictionary<ITranche, TrancheCashflows>();
        EarliestTerminationDates = new Dictionary<string, DateTime>();
        ContributedGroups = new Dictionary<string, HashSet<string>>();
    }

    public Dictionary<IAsset, AssetCashflows> AssetCashflows { get; set; }
    public IList<PeriodCashflows> CollateralCashflows { get; set; }
    public Dictionary<ITranche, TrancheCashflows> ClassCashflows { get; }
    public Dictionary<ITranche, TrancheCashflows> TrancheCashflows { get; }
    public IList<TriggerResult> TriggerResults { get; set; }
    public Dictionary<string, DateTime> EarliestTerminationDates { get; }
    public Dictionary<string, HashSet<string>> ContributedGroups { get; }
}