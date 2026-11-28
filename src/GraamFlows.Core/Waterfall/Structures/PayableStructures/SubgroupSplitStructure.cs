using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

/// <summary>
///     Principal split by each subgroup's own principal: each leg is paid its stated share of each
///     subgroup's principal this period, summed over the subgroups ("75% of the Subgroup 12A
///     Principal Distribution Amount and 75% of the Subgroup 12B Principal Distribution Amount to
///     FL, until retired; 25% of the Subgroup 12A … and 25% of the Subgroup 12B …, in the following
///     order of priority: …"). A leg funded by several subgroups is paid what they give it
///     together, as the document pays it.
///     <para>
///         Each subgroup's principal is the collateral cashflow's (<see cref="SubgroupPrincipal" />,
///         stated by the caller by subgroup). Scheduled principal is split by the subgroups'
///         scheduled principal, unscheduled by their unscheduled, recoveries by their recoveries.
///         A write-down and interest are split by the subgroups' principal of all three.
///     </para>
///     <para>
///         The shares of each subgroup total 100. A leg that cannot take its share passes it to the
///         legs still holding a balance, as <see cref="SplitStructure" /> does. A subgroup with no
///         row in a period (its collateral paid off) paid no principal in it.
///     </para>
/// </summary>
public class SubgroupSplitStructure : SplitStructure
{
    private readonly DynamicGroup _group;
    private readonly IList<IPayable> _legs;
    private readonly IList<IReadOnlyDictionary<string, double>> _weights;

    public SubgroupSplitStructure(DynamicGroup group, IList<IReadOnlyDictionary<string, double>> weights,
        IList<IPayable> payables) : base(payables)
    {
        if (weights.Count != payables.Count)
            throw new ArgumentException(
                $"A SUBGROUP_SPLIT states {weights.Count} sets of shares for {payables.Count} legs; state one per leg.");
        if (weights.Any(w => w.Count == 0 || w.Values.Any(v => !double.IsFinite(v) || v < 0)))
            throw new ArgumentException(
                "A SUBGROUP_SPLIT leg states its share, in percent, of each subgroup it is paid from.");
        foreach (var subgroup in weights.SelectMany(w => w.Keys).Distinct())
        {
            var total = weights.Sum(w => w.TryGetValue(subgroup, out var v) ? v : 0.0);
            if (Math.Abs(total - 100.0) > ShareTolerance * 100)
                throw new ArgumentException(
                    $"A SUBGROUP_SPLIT's shares of subgroup {subgroup} total {total}%, not 100%.");
        }

        _group = group;
        _legs = payables;
        _weights = weights;
    }

    protected override IList<double> Shares(SplitAmount kind, DateTime cfDate)
    {
        var subgroups = _group.CurrentPeriodCashflows?.Subgroups;
        if (subgroups == null || subgroups.Count == 0)
            throw new InvalidOperationException(
                "A SUBGROUP_SPLIT pays each subgroup's principal, and the collateral was not stated by subgroup.");
        double Amount(SubgroupPrincipal p)
        {
            return kind switch
            {
                SplitAmount.Scheduled => p.Scheduled,
                SplitAmount.Unscheduled => p.Unscheduled,
                SplitAmount.Recovery => p.Recovery,
                _ => p.Scheduled + p.Unscheduled + p.Recovery
            };
        }

        var shares = _weights
            // A subgroup with no row this period (its collateral paid off) paid no principal.
            .Select(w => w.Sum(kv =>
                kv.Value / 100.0 * (subgroups.TryGetValue(kv.Key, out var p) ? Amount(p) : 0.0)))
            .ToList();
        // No subgroup paid principal of this kind this period: the amount (if any) is split by
        // the legs' stated shares, so nothing handed to the split is left unpaid.
        if (shares.Sum() <= 0)
            return _weights.Select(w => w.Values.Sum()).ToList();
        return shares;
    }

    public override string Describe(int level)
    {
        var tabs = string.Concat(Enumerable.Repeat("\t", level));
        return tabs + "SUBGROUP_SPLIT(\n" + string.Join(",\n",
            _legs.Select((p, i) =>
                $"{tabs}\t{string.Join(", ", _weights[i].Select(kv => $"{kv.Value}% of {kv.Key}"))}:\n" +
                p.Describe(level + 1))) + ")";
    }

    public override XElement DescribeXml()
    {
        var element = new XElement("SUBGROUP_SPLIT");
        for (var i = 0; i < _legs.Count; i++)
        {
            var leg = new XElement("LEG");
            foreach (var (subgroup, share) in _weights[i])
                leg.Add(new XElement("SHARE", new XAttribute("subgroup", subgroup), new XAttribute("pct", share)));
            leg.Add(_legs[i].DescribeXml());
            element.Add(leg);
        }

        return element;
    }
}
