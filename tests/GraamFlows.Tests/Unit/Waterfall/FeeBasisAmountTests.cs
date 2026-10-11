using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// A managed pool's fees are struck on "the Fee Basis Amount at the beginning of the Collection
/// Period", per annum on a 360-day year of 30-day months, "prorated for the related Interest Accrual
/// Period". Two formula variables carry that:
///
/// * <c>fee_basis</c> — when a Collection Period opens, the previous period's principal collections
///   are still in the collection account (they are paid or reinvested on its Payment Date, after
///   the new period has begun), so the basis is the previous period's opening balance less what it
///   lost. Struck on the new period's pool instead (<c>begin_balance</c>), a deal's amortisation
///   years charged a quarter early: measured against a reference model's fees on a reinvesting CLO,
///   ~6% light on each manager's fee; with <c>fee_basis</c>, within 0.02% of its basis every period.
/// * <c>period_years_30360</c> — the Interest Accrual Period on 30/360: from the notes' settlement
///   date for the first distribution (a 207-day stub, not seven whole months), then Payment Date to
///   Payment Date.
///
/// <c>begin_balance</c> and <c>period_months</c> are unchanged.
/// </summary>
public class FeeBasisAmountTests
{
    private static readonly DateTime FirstPay = TestConstants.DefaultFirstPayDate; // 2024-02-25

    private static CollateralCashflows Pool(double cdr = 0, int months = 37)
    {
        var pool = new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(FirstPay, months, 100_000_000, cpr: 10.0, cdr: cdr, wac: 8.0)
            .Build();
        foreach (var p in pool.PeriodCashflows)
            p.CollateralLoss = p.DefaultedPrincipal - p.RecoveryPrincipal;
        return pool;
    }

    private static List<TrancheCashflow> Fees(CollateralCashflows pool, string formula, int payFrequency = 4,
        DateTime? settle = null)
    {
        var builder = new TestDealBuilder();
        if (settle is { } s)
            builder = builder.WithFirstSettleDate(s); // before the tranches, which take it
        builder = builder
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithExpenseTranche("Fee", 0, formula: formula)
            .WithSequentialWaterfall("A", "B")
            .WithPayFrequency(payFrequency, FirstPay);
        var (_, cf) = builder.BuildAndRun(pool);
        return cf.TrancheCashflows.First(t => t.Key.TrancheName == "Fee").Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).Where(r => r.Expense > 0).ToList();
    }

    private static Dictionary<(int, int), PeriodCashflows> ByMonth(CollateralCashflows pool) =>
        pool.PeriodCashflows.ToDictionary(p => (p.CashflowDate.Year, p.CashflowDate.Month));

    [Fact]
    public void The_basis_is_the_previous_collection_periods_opening_balance()
    {
        var pool = Pool();
        var rows = Fees(pool, "fee_basis * 0.012 / 4");
        var month = ByMonth(pool);
        // The pool starts ON the first Payment Date (a one-month first period), so from the third
        // distribution on, each has a whole previous quarter.
        foreach (var row in rows.Skip(2).Take(6))
        {
            // This quarter opens two months before its Payment Date; the previous one three before that.
            var prevOpening = row.CashflowDate.AddMonths(-5);
            row.Expense.Should().BeApproximately(month[(prevOpening.Year, prevOpening.Month)].BeginBalance * 0.012 / 4, 0.01,
                "the previous quarter's principal is still cash when this quarter opens");
        }
    }

    [Fact]
    public void Less_what_the_previous_period_lost()
    {
        var pool = Pool(cdr: 6);
        var rows = Fees(pool, "fee_basis * 0.012 / 4");
        var month = ByMonth(pool);
        var row = rows[3];
        var prevMonths = Enumerable.Range(3, 3).Select(k => row.CashflowDate.AddMonths(-k))
            .Select(d => month[(d.Year, d.Month)]).ToList();
        var opening = prevMonths.OrderBy(p => p.CashflowDate).First().BeginBalance;
        var loss = prevMonths.Sum(p => p.CollateralLoss);
        loss.Should().BeGreaterThan(0);
        row.Expense.Should().BeApproximately((opening - loss) * 0.012 / 4, 0.01);
    }

    [Fact]
    public void The_first_distribution_reads_its_own_periods_opening_balance()
    {
        var pool = Pool();
        var first = Fees(pool, "fee_basis * 0.012 / 4")[0];
        first.Expense.Should().BeApproximately(pool.PeriodCashflows[0].BeginBalance * 0.012 / 4, 0.01,
            "no earlier period: the pool it opened with");
    }

    [Fact]
    public void A_first_period_folded_from_earlier_months_opens_at_its_earliest_month()
    {
        // A pool projected from before the first Payment Date folds its early months into the first
        // distribution (a CLO's seven-month first Collection Period): the basis is the pool it opened
        // with, not the balance of its last month.
        var pool = new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(FirstPay.AddMonths(-6), 43, 100_000_000, cpr: 10.0, cdr: 0, wac: 8.0)
            .Build();
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithExpenseTranche("Fee", 0, formula: "fee_basis * 0.012 / 4")
            .WithSequentialWaterfall("A", "B")
            .WithPayFrequency(4, FirstPay)
            .WithFirstPeriodCollateral("Fold")
            .BuildAndRun(pool);
        var first = cf.TrancheCashflows.First(t => t.Key.TrancheName == "Fee").Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).First(r => r.Expense > 0);
        first.Expense.Should().BeApproximately(100_000_000 * 0.012 / 4, 0.01);
    }

    [Fact]
    public void The_interest_accrual_period_is_30_360_from_the_notes_settlement()
    {
        // Notes settled on the 28th of the month before the first Payment Date (the 25th): a 27-day
        // first period on 30/360, then whole quarters.
        var settle = FirstPay.AddMonths(-1).AddDays(3);
        var rows = Fees(Pool(), "1000000 * period_years_30360", settle: settle);
        rows[0].Expense.Should().BeApproximately(1_000_000 * 27 / 360.0, 1e-6);
        rows.Skip(1).Take(4).Should().OnlyContain(r => Math.Abs(r.Expense - 250_000) < 1e-6);
        var monthly = Fees(Pool(), "1000000 * period_years_30360", payFrequency: 12);
        monthly.Skip(1).Take(4).Should().OnlyContain(r => Math.Abs(r.Expense - 1_000_000 / 12.0) < 1e-6);
    }

    [Fact]
    public void Begin_balance_is_unchanged()
    {
        var pool = Pool();
        var rows = Fees(pool, "begin_balance * 0.012 / 4");
        var month = ByMonth(pool);
        foreach (var row in rows.Skip(1).Take(4))
        {
            var opening = row.CashflowDate.AddMonths(-2);
            row.Expense.Should().BeApproximately(month[(opening.Year, opening.Month)].BeginBalance * 0.012 / 4, 0.01);
        }
    }
}
