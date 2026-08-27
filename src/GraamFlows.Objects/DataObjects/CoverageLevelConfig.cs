namespace GraamFlows.Objects.DataObjects;

/// <summary>
/// One level of a CLO OC/IC coverage-test cascade (senior→junior order on the deal).
///
/// A CLO tests coverage per LEVEL, not once for the whole stack: the Class A/B tests
/// sit senior to the Class C tests, and a failing test at a level diverts available
/// interest to sequential principal paydown of the note stack (senior-first) BEFORE
/// any junior level's interest is paid — the interest→principal diversion cure.
///
/// The OC numerator is the deal's adjusted collateral principal amount ("ACPA")
/// scheduled/deal variable when the deal carries one (harmony computes and discloses
/// any CCC haircut; the engine stays ratings-agnostic), else the current collateral
/// balance. See <c>ComposableStructure.PayCoverageCascadeInterestStep</c>.
/// </summary>
public record CoverageLevelConfig
{
    /// <summary>Level name, e.g. "A/B", "C", "D" — used to label trigger results.</summary>
    public string Level { get; init; } = "";

    /// <summary>
    /// Note classes AT OR ABOVE this level, senior-first (the OC denominator set).
    /// Levels are cumulative: a junior level's list contains the senior level's.
    /// </summary>
    public IReadOnlyList<string> Tranches { get; init; } = Array.Empty<string>();

    /// <summary>OC trigger in percent (121.58 means 121.58%); null = no OC test at this level.</summary>
    public double? OcTriggerPct { get; init; }

    /// <summary>IC trigger in percent; null = no IC test at this level.</summary>
    public double? IcTriggerPct { get; init; }
}
