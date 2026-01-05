namespace GraamFlows.Objects.DataObjects;

/// <summary>
/// Configuration for OC turbo paydown step.
/// Target OC = MAX(TargetPct * PoolBalance, FloorAmt)
/// </summary>
public record OcTargetConfig
{
    /// <summary>Target OC as percentage of pool balance (e.g., 0.2335 = 23.35%)</summary>
    public double TargetPct { get; init; }

    /// <summary>Floor OC amount in dollars</summary>
    public double FloorAmt { get; init; }
}
