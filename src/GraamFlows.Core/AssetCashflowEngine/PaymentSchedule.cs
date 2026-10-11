using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Util;

namespace GraamFlows.AssetCashflowEngine;

/// <summary>
///     An asset that pays less often than monthly (a quarterly or semi-annual loan) on the monthly
///     projection grid.
///
///     Such a loan accrues interest every month but PAYS it on its payment dates, and it prepays,
///     defaults and recovers on them too: a quarterly borrower refinances at the end of an interest
///     period, so its balance — and the interest it earns — stays whole through the period. Projected
///     monthly, the loan instead lost balance mid-period every month, and a pool of them under-earned
///     by the part of each period after a monthly prepayment or maturity.
///
///     The asset's payment periods are the projection rows its payment dates fall in. A payment date
///     belongs to the first row whose COLLECTION CUTOFF is on or after it (a managed pool's Collection
///     Period ends some business days before each Payment Date, so a loan paying on the 6th of a month
///     whose cutoff is the 5th is collected in the next row). Hazards over the months since the last
///     payment are concentrated onto the payment row — <c>1 − Π(1 − h)</c> — so the period's total
///     prepayment and default are unchanged; only their timing moves.
/// </summary>
public static class PaymentSchedule
{
    /// <summary>Months between payments for a payments-per-year frequency (12, 6, 4, 3, 2, 1).</summary>
    public static int MonthsBetween(int paymentFrequency) => paymentFrequency switch
    {
        12 or 0 => 1,
        6 => 2,
        4 => 3,
        3 => 4,
        2 => 6,
        1 => 12,
        _ => throw new ArgumentException(
            $"Asset payment frequency {paymentFrequency} is not one of 12, 6, 4, 3, 2, 1 payments a year.")
    };

    /// <summary>
    ///     The absolute period (absT) of the asset's first payment row: the asset's own
    ///     <see cref="IAsset.FirstPaymentAbsT" /> when set, else the first projection row whose
    ///     collection cutoff (<paramref name="cutoff" /> of the row's date; the date itself when null)
    ///     is on or after <see cref="IAsset.NextPaymentDate" />.
    /// </summary>
    public static int FirstPaymentAbsT(IAsset asset, DateTime firstProjDate, int maxPeriods,
        Func<DateTime, DateTime>? cutoff)
    {
        if (asset.FirstPaymentAbsT is { } fixedT)
            return fixedT;
        if (asset.NextPaymentDate is not { } next)
            throw new ArgumentException(
                $"Asset {asset.AssetId} pays {asset.PaymentFrequency} times a year but states no next payment " +
                "date: its payment months cannot be placed, and guessing them moves its cash between periods.");
        var startT = DateUtil.CalcAbsT(firstProjDate);
        for (var p = 0; p < maxPeriods; p++)
        {
            var row = firstProjDate.AddMonths(p);
            if ((cutoff?.Invoke(row) ?? row).Date >= next.Date)
                return startT + p;
        }

        return startT + maxPeriods; // never within the horizon: it pays only at maturity / payoff
    }

    /// <summary>True when absolute period <paramref name="absT" /> is one of the asset's payment rows.</summary>
    public static bool IsPaymentPeriod(int absT, int firstPayAbsT, int monthsBetween) =>
        monthsBetween <= 1 || (absT >= firstPayAbsT && (absT - firstPayAbsT) % monthsBetween == 0);

    /// <summary>
    ///     Concentrate a per-period hazard (SMM / MDR, indexed from <paramref name="startAbsT" />) onto
    ///     the asset's payment rows, in place: a payment row carries <c>1 − Π(1 − h)</c> over the months
    ///     since the previous payment row, every other row zero.
    /// </summary>
    public static void Concentrate(double[] hazard, int startAbsT, int firstPayAbsT, int monthsBetween)
    {
        if (monthsBetween <= 1 || hazard == null)
            return;
        var survive = 1.0;
        for (var p = 0; p < hazard.Length; p++)
        {
            survive *= 1.0 - Math.Clamp(hazard[p], 0.0, 1.0);
            if (IsPaymentPeriod(startAbsT + p, firstPayAbsT, monthsBetween))
            {
                hazard[p] = 1.0 - survive;
                survive = 1.0;
            }
            else
            {
                hazard[p] = 0.0;
            }
        }
    }
}
