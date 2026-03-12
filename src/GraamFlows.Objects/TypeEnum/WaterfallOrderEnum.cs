namespace GraamFlows.Objects.TypeEnum;

/// <summary>
/// Controls how INTEREST and PRINCIPAL steps are iterated in ComposableStructure.
/// Standard: Pay all interest, then all principal (e.g., EART)
/// InterestFirst: Interleave by seniority — interest[i] then principal[i] (e.g., SDART)
/// PrincipalFirst: Interleave by seniority — principal[i] then interest[i]
/// </summary>
public enum WaterfallOrderEnum
{
    Standard,
    InterestFirst,
    PrincipalFirst
}
