namespace GraamFlows.Objects.TypeEnum;

/// <summary>
///     Which collateral principal cashflows are eligible to be reinvested during
///     the reinvestment window. A bitmask so a deal can combine sources, e.g.
///     <c>ScheduledPrincipal | Prepayments</c>. Interest is never reinvested.
/// </summary>
[Flags]
public enum EligibleProceeds
{
    None = 0,
    ScheduledPrincipal = 1,
    Prepayments = 2,
    Recoveries = 4
}
