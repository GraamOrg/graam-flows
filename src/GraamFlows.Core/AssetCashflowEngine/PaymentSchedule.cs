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
        if (asset.NextPaymentDate is not { } stated)
            throw new ArgumentException(
                $"Asset {asset.AssetId} pays {asset.PaymentFrequency} times a year but states no next payment " +
                "date: its payment months cannot be placed, and guessing them moves its cash between periods.");
        var next = PaymentDate(stated, MonthsBetween(asset.PaymentFrequency), firstProjDate, 0);
        var startT = DateUtil.CalcAbsT(firstProjDate);
        for (var p = 0; p < maxPeriods; p++)
        {
            var row = firstProjDate.AddMonths(p);
            if ((cutoff?.Invoke(row) ?? row).Date >= next.Date)
                return startT + p;
        }

        return startT + maxPeriods; // never within the horizon: it pays only at maturity / payoff
    }

    /// <summary>
    ///     The asset's <paramref name="k" />-th payment date of the projection (0 = the first): its
    ///     stated next payment date rolled forward by whole payment periods past the projection's
    ///     start (a tape's date that has already gone by is the previous payment, not the next), then
    ///     <paramref name="k" /> periods on. Counted from the anchor, so a month-end date stays on
    ///     month ends.
    /// </summary>
    public static DateTime PaymentDate(DateTime stated, int monthsBetween, DateTime firstProjDate, int k)
    {
        var start = firstProjDate.AddMonths(-1).Date; // row 0 accrues from one month before its date
        var skipped = 0;
        while (stated.Date.AddMonths((skipped) * monthsBetween) <= start)
            skipped++;
        return stated.Date.AddMonths((skipped + k) * monthsBetween);
    }

    /// <summary>
    ///     The absolute period of the projection row that collects <paramref name="date" />: the first
    ///     row whose collection cutoff (<paramref name="cutoff" /> of the row's date; the date itself
    ///     when null) is on or after it. <c>startT + maxPeriods</c> when no row within the horizon does.
    /// </summary>
    public static int RowCollecting(DateTime date, DateTime firstProjDate, int maxPeriods,
        Func<DateTime, DateTime>? cutoff)
    {
        var startT = DateUtil.CalcAbsT(firstProjDate);
        for (var p = 0; p < maxPeriods; p++)
        {
            var row = firstProjDate.AddMonths(p);
            if ((cutoff?.Invoke(row) ?? row).Date >= date.Date)
                return startT + p;
        }

        return startT + maxPeriods;
    }

    /// <summary>
    ///     Make a stated maturity date the asset's maturity: its term (and an interest-only term that
    ///     ran to the old maturity) is set so it repays in the row that collects the date. A term read
    ///     off a whole-month remaining term misplaces a loan maturing near a collection cutoff by a row
    ///     — and with it a quarter on a quarterly deal. Idempotent. A maturity before the asset's
    ///     origination row is ignored.
    /// </summary>
    public static void ApplyMaturityDate(IAsset asset, DateTime firstProjDate, int maxPeriods,
        Func<DateTime, DateTime>? cutoff)
    {
        if (asset.MaturityDate is not { } maturity)
            return;
        var term = RowCollecting(maturity, firstProjDate, maxPeriods, cutoff) - DateUtil.CalcAbsT(asset.OriginalDate);
        if (term < 1)
            return;
        if (asset.IOTerm is { } io && io >= asset.OriginalAmortizationTerm)
            asset.IOTerm = term;
        asset.OriginalAmortizationTerm = term;
    }

    /// <summary>True when absolute period <paramref name="absT" /> is one of the asset's payment rows.</summary>
    public static bool IsPaymentPeriod(int absT, int firstPayAbsT, int monthsBetween) =>
        monthsBetween <= 1 || (absT >= firstPayAbsT && (absT - firstPayAbsT) % monthsBetween == 0);

    /// <summary>
    ///     Concentrate a per-period hazard (SMM / MDR, indexed from <paramref name="startAbsT" />) onto
    ///     the asset's payment rows, in place: a payment row carries <c>1 − Π(1 − h)</c> over the months
    ///     since the previous payment row, every other row zero.
    ///
    ///     <paramref name="statedCalendar" /> — the asset states its next payment date, so the first
    ///     payment is the end of a payment period that began before the projection did: it carries
    ///     EXACTLY one period's hazard (the <paramref name="monthsBetween" /> months ending at it; months
    ///     before the projection take the payment month's hazard), and rows before it carry none. A
    ///     quarterly loan paying in the projection's first month carried one month's hazard — a
    ///     reference model's three — and a monthly loan whose first payment falls after the first
    ///     row's cutoff prepaid a row early; either way the pool kept the wrong balance for life.
    ///     Without it (bought collateral, placed by its purchase) the first payment carries the months
    ///     since the start, as before. A monthly asset is concentrated only with a stated calendar.
    /// </summary>
    public static void Concentrate(double[] hazard, int startAbsT, int firstPayAbsT, int monthsBetween,
        bool statedCalendar = false)
    {
        if (hazard == null || (monthsBetween <= 1 && !statedCalendar))
            return;
        var h = hazard.Select(x => Math.Clamp(x, 0.0, 1.0)).ToArray();
        var survive = 1.0;
        var first = true;
        for (var p = 0; p < h.Length; p++)
        {
            var absT = startAbsT + p;
            var pays = absT >= firstPayAbsT && (absT - firstPayAbsT) % Math.Max(monthsBetween, 1) == 0;
            if (!pays)
            {
                survive *= 1.0 - h[p]; // a stated calendar's first payment recomputes it (below)
                hazard[p] = 0.0;
                continue;
            }

            if (first && statedCalendar)
            {
                survive = 1.0;
                for (var j = 0; j < Math.Max(monthsBetween, 1); j++)
                    survive *= 1.0 - (p - j >= 0 ? h[p - j] : h[p]);
            }
            else
            {
                survive *= 1.0 - h[p];
            }

            first = false;
            hazard[p] = 1.0 - survive;
            survive = 1.0;
        }
    }
}
