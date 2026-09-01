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
    public static (double Wal, double BalanceWal) Compute(
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
            return (0, 0);

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
        return (calc.WeightedAverageLife(), calc.BalanceWeightedAverageLife());
    }
}
