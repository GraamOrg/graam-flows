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
    /// When set with <see cref="CutoffPoolBalance"/>: at or below this share of the cutoff pool
    /// (0.10 = 10%) the target OC is the whole pool, so the turbo pays the notes with all the
    /// excess. Deals that stop targeting OC near the end say so ("… the Pool Balance … is less
    /// than or equal to 10% of the Cutoff Date Pool Balance, the lesser of the Note Balance and
    /// all remaining Available Funds").
    /// </summary>
    public double? FullTurboPoolPct { get; init; }

    /// <summary>The cutoff pool balance <see cref="FullTurboPoolPct"/> is a share of.</summary>
    public double? CutoffPoolBalance { get; init; }

    /// <summary>
    /// The deal's yield supplement overcollateralization, or null. With it, the overcollateralization
    /// the turbo builds and the release gives back is the Adjusted Pool Balance (the pool less the
    /// period's YSOC) less the notes, and the target's share of the current pool is a share of the
    /// Adjusted Pool Balance. <see cref="FloorAmt"/> is a dollar amount, so the caller states it on
    /// whichever cutoff balance the document names.
    /// </summary>
    public YsocSchedule? Ysoc { get; init; }

    /// <summary>
    /// Calculate target OC given a pool balance.
    /// </summary>
    public double CalculateTargetOc(double poolBalance) => CalculateTargetOc(poolBalance, poolBalance);

    /// <summary>
    /// Calculate target OC given the pool balance and the balance the overcollateralization is
    /// measured on (<paramref name="ocBase" />: the Adjusted Pool Balance on a YSOC deal, else the
    /// pool). The full-turbo threshold reads the pool, as the documents state it.
    /// </summary>
    public double CalculateTargetOc(double poolBalance, double ocBase)
    {
        if (FullTurboPoolPct is > 0 && CutoffPoolBalance is > 0
            && poolBalance <= FullTurboPoolPct.Value * CutoffPoolBalance.Value)
            return ocBase;

        var targetPoolBalance = UseInitialBalance ? InitialPoolBalance!.Value : ocBase;

        return FormulaType?.ToLowerInvariant() switch
        {
            "sum_of" => TargetPct * targetPoolBalance + FloorAmt,
            _ => Math.Max(TargetPct * targetPoolBalance, FloorAmt)
        };
    }
}
