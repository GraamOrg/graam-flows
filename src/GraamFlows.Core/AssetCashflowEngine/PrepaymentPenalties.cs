using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Util;

namespace GraamFlows.AssetCashflowEngine;

/// <summary>
///     An asset's prepayment penalty schedule (<see cref="IAsset.PrepaymentPenalties" />) read onto
///     the projection rows, for the <see cref="Amortizer" />: the percent of a voluntary prepayment
///     the borrower pays as a penalty in each row.
/// </summary>
public static class PrepaymentPenalties
{
    /// <summary>
    ///     The penalty percent by projection row, row 0 being absolute period
    ///     <paramref name="startTime" />, or null when the asset states no schedule. A row takes the
    ///     last step that starts in or before its month; a row before the first step takes none.
    ///     A malformed schedule (steps out of date order, a percent outside 0-100) is refused, not
    ///     read: a dropped or reordered step does not fail, it moves every later percent.
    /// </summary>
    public static double[]? PctByRow(IAsset asset, int startTime, int rows)
    {
        if (asset.PrepaymentPenalties is not { Count: > 0 } steps)
            return null;

        var starts = new int[steps.Count];
        for (var i = 0; i < steps.Count; i++)
        {
            var pct = steps[i].Pct;
            if (!double.IsFinite(pct) || pct < 0 || pct > 100)
                throw new ArgumentException(
                    $"Asset {asset.AssetId}: prepayment penalty step {i + 1} is {pct}%; a penalty is a " +
                    "percent of the principal prepaid, from 0 to 100.");
            starts[i] = DateUtil.CalcAbsT(steps[i].From);
            if (i > 0 && starts[i] <= starts[i - 1])
                throw new ArgumentException(
                    $"Asset {asset.AssetId}: prepayment penalty step {i + 1} ({steps[i].From:yyyy-MM}) " +
                    $"does not start after step {i} ({steps[i - 1].From:yyyy-MM}); state the steps in " +
                    "date order, one per month at most.");
        }

        var byRow = new double[rows];
        for (var i = 0; i < steps.Count; i++)
        {
            var from = Math.Max(starts[i] - startTime, 0);
            var to = i + 1 < steps.Count ? Math.Min(starts[i + 1] - startTime, rows) : rows;
            for (var row = from; row < to; row++)
                byRow[row] = steps[i].Pct;
        }

        return byRow;
    }
}
