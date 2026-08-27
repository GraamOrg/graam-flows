using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Maps the CLO coverage-cascade request DTO onto the domain
///     <see cref="CoverageLevelConfig" /> list. Shared by the API controller and
///     the CLI runner so the two stay in sync (mirrors
///     <see cref="ReinvestmentConfigMapper" />). Validates the shape before
///     returning; tranche-name resolution happens later against the built deal
///     (ComposableStructure fails loudly on an unknown class).
/// </summary>
public static class CoverageCascadeMapper
{
    public static List<CoverageLevelConfig>? Map(List<CoverageLevelDto>? levels, string dealName)
    {
        if (levels == null || levels.Count == 0)
            return null;

        var configs = new List<CoverageLevelConfig>(levels.Count);
        var seenLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lv in levels)
        {
            if (string.IsNullOrWhiteSpace(lv.Level))
                throw new InvalidOperationException(
                    $"Deal {dealName}: coverageCascade level requires a non-empty 'level' name");

            if (!seenLevels.Add(lv.Level))
                throw new InvalidOperationException(
                    $"Deal {dealName}: coverageCascade level '{lv.Level}' appears more than once");

            if (lv.Tranches == null || lv.Tranches.Count == 0)
                throw new InvalidOperationException(
                    $"Deal {dealName}: coverageCascade level '{lv.Level}' requires at least one tranche " +
                    "(the note classes at or above the level — the OC denominator set)");

            if (lv.OcTriggerPct is <= 0)
                throw new InvalidOperationException(
                    $"Deal {dealName}: coverageCascade level '{lv.Level}' ocTriggerPct must be positive " +
                    $"(percent, e.g. 121.58), got {lv.OcTriggerPct}");

            if (lv.IcTriggerPct is <= 0)
                throw new InvalidOperationException(
                    $"Deal {dealName}: coverageCascade level '{lv.Level}' icTriggerPct must be positive " +
                    $"(percent), got {lv.IcTriggerPct}");

            configs.Add(new CoverageLevelConfig
            {
                Level = lv.Level,
                Tranches = lv.Tranches.ToList(),
                OcTriggerPct = lv.OcTriggerPct,
                IcTriggerPct = lv.IcTriggerPct
            });
        }

        return configs;
    }
}
