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

    /// <summary>
    /// Initial pool balance for calculating OC target when UseInitialBalance is true.
    /// If set, OC target = MAX(TargetPct * InitialPoolBalance, FloorAmt) instead of current pool balance.
    /// This matches prospectus language like "10.15% of pool balance as of cut-off date".
    /// </summary>
    public double? InitialPoolBalance { get; init; }

    /// <summary>
    /// When true, use InitialPoolBalance for OC target calculation instead of current pool balance.
    /// Default is false (use current pool balance).
    /// </summary>
    public bool UseInitialBalance => InitialPoolBalance.HasValue && InitialPoolBalance.Value > 0;
}
