namespace GraamFlows.Objects.DataObjects;

/// <summary>
///     One subgroup's principal for a period: the scheduled, unscheduled and recovery principal its
///     own collateral paid. A group whose supplement allocates each subgroup's Principal
///     Distribution Amount apart ("75% of the Subgroup 12A Principal Distribution Amount and 75% of
///     the Subgroup 12B Principal Distribution Amount to FL, …") is paid by
///     <c>SUBGROUP_SPLIT</c>, which reads these.
/// </summary>
public class SubgroupPrincipal
{
    public double Scheduled { get; set; }
    public double Unscheduled { get; set; }
    public double Recovery { get; set; }

    /// <summary>The subgroup's collateral balance at the start of the period.</summary>
    public double BeginBalance { get; set; }

    /// <summary>The subgroup's collateral loss in the period.</summary>
    public double Loss { get; set; }

    public SubgroupPrincipal Clone()
    {
        return new SubgroupPrincipal
        {
            Scheduled = Scheduled, Unscheduled = Unscheduled, Recovery = Recovery, BeginBalance = BeginBalance,
            Loss = Loss
        };
    }

    /// <summary>
    ///     Another row of the same subgroup and date (merged), or the next period of a fold: principal
    ///     and loss add; the begin balance is the earlier one's (a fold) or both rows' (a merge), as
    ///     <paramref name="sameDate" /> says.
    /// </summary>
    public void Add(SubgroupPrincipal other, bool sameDate = true)
    {
        Scheduled += other.Scheduled;
        Unscheduled += other.Unscheduled;
        Recovery += other.Recovery;
        Loss += other.Loss;
        if (sameDate)
            BeginBalance += other.BeginBalance;
    }

    /// <summary>Each subgroup's principal summed into <paramref name="into" /> (a new map when null).</summary>
    public static Dictionary<string, SubgroupPrincipal>? Merge(Dictionary<string, SubgroupPrincipal>? into,
        Dictionary<string, SubgroupPrincipal>? from, bool sameDate = true)
    {
        if (from == null || from.Count == 0)
            return into;
        into ??= new Dictionary<string, SubgroupPrincipal>();
        foreach (var (name, principal) in from)
            if (into.TryGetValue(name, out var have))
                have.Add(principal, sameDate);
            else
                into[name] = principal.Clone();
        return into;
    }

    public static Dictionary<string, SubgroupPrincipal>? CloneMap(Dictionary<string, SubgroupPrincipal>? map)
    {
        return map?.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
    }
}
