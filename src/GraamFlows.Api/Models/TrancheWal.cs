using GraamFlows.Objects.DataObjects;
// Aliased exactly as PricingController does: `Cashflow` is ambiguous between the
// DataObjects record and the Util.Finance CALCULATOR, and it is the calculator we want.
using CashflowCalculator = GraamFlows.Util.Finance.Cashflow;

namespace GraamFlows.Api.Models;

/// <summary>
///     The WAL pair on a tranche summary, computed by the ENGINE's own functions.
///
///     One helper because <c>TrancheSummaryDto</c> is built at FOUR sites — two in
///     <c>WaterfallController</c> (dynamic tranches, then class-only tranches) and two in the
///     CLI's <c>WaterfallRunner</c>. A field that only some of them populate is worse than one
///     none of them do, because the response looks complete: that is exactly how
///     <c>reserveConfig</c> came to be CLI-only and <c>FirstPeriodCollateralPolicy</c> came to
///     be API-only. Four call sites, one implementation.
/// </summary>
public static class TrancheWal
{
    /// <param name="cashflows">the tranche's cashflow rows, in period order.</param>
    /// <param name="settleDate">
    ///     the date the holder's clock starts. <c>WeightedAverageLife()</c> drops cashflows
    ///     before it, so this is not cosmetic: on OBX 2025-NQM6 harmony projects from
    ///     2025-03-02 and settles on 2025-04-14, and the two answers differ.
    /// </param>
    /// <param name="isIo">
    ///     true when the holder receives no principal. The engine then weights by the balance
    ///     change instead, which is what gives a notional strip a WAL at all — principal
    ///     weighting returns nothing for it.
    /// </param>
    /// <returns>
    ///     the two WALs, or <c>null</c> for either when there is nothing to weight.
    ///
    ///     NULL rather than 0.0 on purpose. The engine's own functions return 0 when the
    ///     weighted total is below a cent, and 0.0 in a non-nullable field is indistinguishable
    ///     from a real answer — a class whose balance went entirely to WRITEDOWN reports
    ///     `Wal 0.0000` beside a perfectly good `BalanceWal 2.2793`, and STACR 2025-DNA1's
    ///     B2H and B3H do exactly that. A consumer that falls back when the field is ABSENT
    ///     would not fall back on a present-and-zero value, so the wrong number would win
    ///     silently. Null says "this stream has no such life", which is the truth.
    /// </returns>
    public static (double? Wal, double? BalanceWal) Compute(
        IEnumerable<TrancheCashflowDto> cashflows, DateTime settleDate, bool isIo)
    {
        var rows = new List<ICashflow>();
        foreach (var cf in cashflows)
            rows.Add(new CashflowImpl
            {
                CashflowDate = cf.CashflowDate,
                Interest = cf.Interest,
                Principal = cf.ScheduledPrincipal + cf.UnscheduledPrincipal,
                Balance = cf.Balance,
                // The DTO already carries the opening balance per period, so the balance
                // change an IO WAL needs is present without re-deriving it from the previous
                // row — which would go wrong on the first period, where there is none.
                PrevBalance = cf.BeginBalance,
                Cashflow = cf.Interest + cf.ScheduledPrincipal + cf.UnscheduledPrincipal
            });

        if (rows.Count == 0)
            return (null, null);

        var stream = new CashflowStreamImpl
        {
            Cashflows = rows,
            SettleDate = settleDate,
            Balance = rows[0].PrevBalance,
            IsIo = isIo
        };
        // `Cashflow` is the engine's cashflow CALCULATOR (PricingController aliases it as
        // CashflowCalculator). Null market rates: WAL needs no curve.
        var calc = new CashflowCalculator(stream, null);

        // Mirrors the engine's own `totalCf < .01` guard so the two cannot disagree about
        // what "nothing to weight" means. `WeightedAverageLife` weights the balance change
        // for an IO stream and the principal otherwise; `BalanceWeightedAverageLife` always
        // weights the balance change.
        var inScope = rows.Where(r => r.CashflowDate >= settleDate).ToList();
        var principalTotal = inScope.Where(r => r.Principal >= 0).Sum(r => r.Principal);
        var balanceTotal = inScope.Where(r => r.Principal >= 0).Sum(r => r.PrevBalance - r.Balance);

        var walTotal = isIo ? balanceTotal : principalTotal;
        return (
            walTotal < .01 ? null : calc.WeightedAverageLife(),
            balanceTotal < .01 ? null : calc.BalanceWeightedAverageLife());
    }
}
