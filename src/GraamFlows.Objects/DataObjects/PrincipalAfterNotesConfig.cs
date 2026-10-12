namespace GraamFlows.Objects.DataObjects;

/// <summary>
/// Expenses a Principal Priority of Payments pays from principal proceeds AFTER the notes and before
/// the residual — a CLO's clause paying, after the Reinvestment Period, a Subordinated Collateral
/// Management Fee the interest proceeds left unpaid. Each expense is one the deal pays after interest
/// (<see cref="IDeal.ExpensesAfterInterest" />), whose unpaid amount is carried; it is paid only from
/// principal the named notes no longer need (their outstanding balance is paid first).
/// </summary>
public record PrincipalAfterNotesConfig
{
    /// <summary>Expenses paid, in this order; each is one of the deal's after-interest expenses.</summary>
    public IReadOnlyList<string> Expenses { get; init; } = Array.Empty<string>();

    /// <summary>The notes repaid ahead of them (a CLO's secured notes).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>First Payment Date it applies on (the day after the Reinvestment Period); null: always.</summary>
    public DateTime? FromDate { get; init; }
}
