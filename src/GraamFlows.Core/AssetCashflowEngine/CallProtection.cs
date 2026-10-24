using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Util;

namespace GraamFlows.AssetCashflowEngine;

/// <summary>
///     An asset's call protection under the CPY prepayment convention.
///
///     A CMBS prospectus states its decrement tables in CPY: "CPR following the expiration of any
///     applicable lockout period, any applicable period in which defeasance is permitted and any
///     applicable yield maintenance period" (the Wells Fargo shelf's CPP adds "any fixed penalty
///     period"). Which month that is differs by loan, so the convention needs each loan's own dates;
///     the asset carries them (<see cref="IAsset.LockoutEndDate" /> and its siblings) and the
///     convention is a scenario switch, like CPR or SMM.
///
///     A run applied the speed as plain CPR from the first period when a caller could not supply the
///     dates as per-asset vectors, and every non-zero column of a published grid then scored a faster
///     scenario than the one published: a pool that is locked out for nine of its ten years paid off
///     in two.
/// </summary>
public static class CallProtection
{
    /// <summary>
    ///     The date the asset's call protection ends - the latest of its stated end dates - or null
    ///     when it states none.
    /// </summary>
    public static DateTime? OpenDate(IAsset asset)
    {
        DateTime? open = null;
        foreach (var end in new[]
                 {
                     asset.LockoutEndDate, asset.DefeasanceEndDate, asset.YieldMaintenanceEndDate,
                     asset.PrepaymentPremiumEndDate
                 })
            if (end is { } d && (open == null || d > open))
                open = d;
        return open;
    }

    /// <summary>
    ///     Zero the asset's prepayment hazard in every projection row before the month its call
    ///     protection ends; the speed applies from that month on. <paramref name="smm" /> is indexed by
    ///     projection row, row 0 being absolute period <paramref name="startTime" />. An asset with no
    ///     stated end, or one that ended before the projection, is left as it is. Returns whether any
    ///     row was held.
    /// </summary>
    public static bool HoldUntilOpen(double[] smm, IAsset asset, int startTime)
    {
        if (OpenDate(asset) is not { } open)
            return false;
        var held = Math.Min(DateUtil.CalcAbsT(open) - startTime, smm.Length);
        for (var row = 0; row < held; row++)
            smm[row] = 0.0;
        return held > 0;
    }
}
