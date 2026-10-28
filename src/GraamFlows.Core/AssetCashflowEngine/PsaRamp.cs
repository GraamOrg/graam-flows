using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.Util;

namespace GraamFlows.AssetCashflowEngine;

/// <summary>
///     The PSA prepayment convention: a speed, where 100 PSA is a CPR that ramps from 0.2% in a
///     loan's first month by 0.2% a month to 6% at month 30, and stays at 6%. 200 PSA is twice that
///     CPR in every month, 50 PSA half.
///
///     The ramp runs on each asset's own age, counted from its <see cref="IAsset.OriginalDate" />,
///     not on the pool's WALA: a loan seasoned 40 months is already at 6% CPR in the first projected
///     period while a new loan in the same pool starts at the bottom of the ramp. An asset that
///     states no origination date, or one after the projection date, starts at age 1.
/// </summary>
public static class PsaRamp
{
    /// <summary>The CPR (percent) 100 PSA reaches at the end of the ramp.</summary>
    public const double TerminalCpr = 6.0;

    /// <summary>The age in months at which the ramp reaches <see cref="TerminalCpr" />.</summary>
    public const int RampMonths = 30;

    /// <summary>
    ///     Whether the asset's age can be read from its origination date as of
    ///     <paramref name="startTime" />: it states one, and it is not after the projection.
    /// </summary>
    public static bool HasOriginalDate(IAsset asset, int startTime) =>
        asset.OriginalDate != default && DateUtil.CalcAbsT(asset.OriginalDate) <= startTime;

    /// <summary>The asset's age in months in projection row 0, at least 1.</summary>
    public static int FirstRowAge(IAsset asset, int startTime) =>
        HasOriginalDate(asset, startTime)
            ? Math.Max(startTime - DateUtil.CalcAbsT(asset.OriginalDate), 1)
            : 1;

    /// <summary>
    ///     The annual CPR (percent) that <paramref name="psaSpeed" /> means at <paramref name="age" />
    ///     months, capped at 100.
    /// </summary>
    public static double Cpr(double psaSpeed, int age) =>
        Math.Min(psaSpeed / 100.0 * TerminalCpr * Math.Min(Math.Max(age, 1), RampMonths) / RampMonths, 100.0);

    /// <summary>
    ///     The monthly prepayment hazard in each projection row for an asset whose age in row 0 is
    ///     <paramref name="firstRowAge" />, de-annualized the way a CPR is.
    /// </summary>
    public static double[] MonthlyHazards(IAnchorableVector? speed, int maxPeriods, int startTime, int firstRowAge)
    {
        var result = new double[maxPeriods];
        for (var row = 0; row < maxPeriods; row++)
        {
            var psa = speed?.ValueAt(row, startTime + row) ?? 0.0;
            result[row] = MathUtil.AnnualPercentToMonthlyHazard(Cpr(psa, firstRowAge + row));
        }

        return result;
    }
}
