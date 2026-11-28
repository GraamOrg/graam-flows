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

    public SubgroupPrincipal Clone()
    {
        return new SubgroupPrincipal { Scheduled = Scheduled, Unscheduled = Unscheduled, Recovery = Recovery };
    }

    public void Add(SubgroupPrincipal other)
    {
        Scheduled += other.Scheduled;
        Unscheduled += other.Unscheduled;
        Recovery += other.Recovery;
    }

    /// <summary>Each subgroup's principal summed into <paramref name="into" /> (a new map when null).</summary>
    public static Dictionary<string, SubgroupPrincipal>? Merge(Dictionary<string, SubgroupPrincipal>? into,
        Dictionary<string, SubgroupPrincipal>? from)
    {
        if (from == null || from.Count == 0)
            return into;
        into ??= new Dictionary<string, SubgroupPrincipal>();
        foreach (var (name, principal) in from)
            if (into.TryGetValue(name, out var have))
                have.Add(principal);
            else
                into[name] = principal.Clone();
        return into;
    }

    public static Dictionary<string, SubgroupPrincipal>? CloneMap(Dictionary<string, SubgroupPrincipal>? map)
    {
        return map?.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
    }
}
