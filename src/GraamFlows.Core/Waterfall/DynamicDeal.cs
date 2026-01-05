using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Waterfall;

public class DynamicDeal
{
    private readonly Dictionary<string, DynamicGroup> _dynGroups;

    public DynamicDeal(IDeal deal)
    {
        Deal = deal;
        _dynGroups = new Dictionary<string, DynamicGroup>();
    }

    public DynamicDeal(DynamicGroup dynGroup)
    {
        Deal = dynGroup.Deal;
        _dynGroups = new Dictionary<string, DynamicGroup>();
        AddGroup(dynGroup);
    }

    public IDeal Deal { get; }
    public ICollection<DynamicGroup> DynamicGroups => _dynGroups.Values;

    public void AddGroup(DynamicGroup dynGroup)
    {
        _dynGroups.Add(dynGroup.GroupNum, dynGroup);
    }

    public DynamicGroup? GetGroup(string groupNum)
    {
        _dynGroups.TryGetValue(groupNum, out var dynGroup);
        return dynGroup;
    }

    public IEnumerable<DynamicGroup> GroupsForPeriod(IEnumerable<PeriodCashflows> periodCashflows)
    {
        var groupSet = new HashSet<string>(periodCashflows.Select(p => p.GroupNum));

        foreach (var dynGroup in _dynGroups.Values)
            if (groupSet.Contains(dynGroup.GroupNum))
                yield return dynGroup;
    }
}