namespace GraamFlows.Objects.DataObjects;

/// <summary>
/// Configuration for reserve account funding and target calculation.
/// Target = TargetPct * CutoffPoolBalance, capped at note balance if CapAtNoteBalance is true.
/// </summary>
public record ReserveAccountConfig
{
    /// <summary>Target reserve as percentage of cutoff pool balance (e.g., 0.01 = 1.00%)</summary>
    public double TargetPct { get; init; }

    /// <summary>Base for target calculation: "CutoffPoolBalance" or "CurrentPoolBalance"</summary>
    public string TargetBase { get; init; } = "CutoffPoolBalance";

    /// <summary>Pool balance as of cutoff date (used when TargetBase is CutoffPoolBalance)</summary>
    public double CutoffPoolBalance { get; init; }

    /// <summary>If true, reserve balance cannot exceed aggregate note principal</summary>
    public bool CapAtNoteBalance { get; init; } = true;

    /// <summary>Calculate target reserve amount</summary>
    public double CalculateTarget(double currentPoolBalance)
    {
        var baseBalance = TargetBase == "CurrentPoolBalance" ? currentPoolBalance : CutoffPoolBalance;
        return TargetPct * baseBalance;
    }

    /// <summary>Calculate effective target (applying note balance cap if configured)</summary>
    public double CalculateEffectiveTarget(double currentPoolBalance, double noteBalance)
    {
        var target = CalculateTarget(currentPoolBalance);
        return CapAtNoteBalance ? Math.Min(target, noteBalance) : target;
    }
}
