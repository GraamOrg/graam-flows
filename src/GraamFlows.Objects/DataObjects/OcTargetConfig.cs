namespace GraamFlows.Objects.DataObjects;

/// <summary>
/// Configuration for OC turbo paydown step.
/// Target OC calculation depends on FormulaType:
/// - "max" (default): MAX(TargetPct * PoolBalance, FloorAmt)
/// - "sum_of": TargetPct * PoolBalance + FloorAmt
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

    /// <summary>
    /// Formula type for OC target calculation:
    /// - "max" (default): Target OC = MAX(TargetPct * PoolBalance, FloorAmt)
    /// - "sum_of": Target OC = TargetPct * PoolBalance + FloorAmt
    /// </summary>
    public string FormulaType { get; init; } = "max";

    /// <summary>
    /// When true, pre-release excess OC by reducing scheduled principal to notes.
    /// Default is false: all scheduled principal flows to notes per prospectus waterfall,
    /// and OC naturally drifts above target post-turbo.
    /// </summary>
    public bool ReleaseExcessOc { get; init; }

    /// <summary>
    /// Calculate target OC given a pool balance.
    /// </summary>
    public double CalculateTargetOc(double poolBalance)
    {
        var targetPoolBalance = UseInitialBalance ? InitialPoolBalance!.Value : poolBalance;

        return FormulaType?.ToLowerInvariant() switch
        {
            "sum_of" => TargetPct * targetPoolBalance + FloorAmt,
            _ => Math.Max(TargetPct * targetPoolBalance, FloorAmt)
        };
    }
}
