namespace GraamFlows.Objects.DataObjects;

/// <summary>
/// A Deferrable Note: interest the Priority of Payments leaves unpaid on it, while any of its
/// Priority Classes is outstanding, is not a shortfall carried to the next Payment Date but
/// deferred interest ADDED TO ITS PRINCIPAL BALANCE — it bears interest at the class's rate from
/// then on, and is paid first from interest proceeds (after the class's current interest) or from
/// principal proceeds ahead of the class's principal (a CLO indenture's "Secured Note Deferred
/// Interest"). With no Priority Class outstanding the unpaid interest stays a shortfall.
/// </summary>
public record DeferrableNoteConfig
{
    /// <summary>The deferrable class.</summary>
    public string Class { get; init; } = "";

    /// <summary>Its Priority Classes: every class ranking senior to it.</summary>
    public IReadOnlyList<string> PriorityClasses { get; init; } = Array.Empty<string>();
}
