using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Util;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// A deal distributes on its own calendar. Collateral is projected monthly; a deal whose tranches
/// pay quarterly (a managed pool's Payment Dates) collects over a three-month Collection Period
/// and distributes it on the Payment Date, so its notes accrue the whole period and receive
/// principal only on pay dates.
///
/// Before this the engine distributed every collateral month whatever the tranches' PayFrequency,
/// and each note accrued one month back from its pay date. A quarterly deal paid principal
/// monthly: notes retired early and accrued less interest — measured on a reference tie-out as the
/// whole remaining rated-class gap, concentrated in the amortization years.
///
/// A monthly deal (PayFrequency 12, every deal before this) never holds anything; the existing
/// suite running unchanged is that pin.
/// </summary>
public class DistributionCalendarTests
{
    private static readonly DateTime FirstPay = TestConstants.DefaultFirstPayDate; // 2024-02-25

    private static CollateralCashflows Pool(int months = 36) =>
        new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(FirstPay, months, 100_000_000, cpr: 10.0, cdr: 0.0, wac: 8.0)
            .Build();

    private static (IDeal Deal, DealCashflows Cf) Run(int payFrequency, CollateralCashflows pool,
        string? feeFormula = null)
    {
        var builder = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1);
        if (feeFormula != null)
            builder = builder.WithExpenseTranche("Fee", 0, formula: feeFormula);
        return builder
            .WithSequentialWaterfall("A", "B")
            .WithPayFrequency(payFrequency, FirstPay)
            .BuildAndRun(pool);
    }

    private static List<TrancheCashflow> Rows(DealCashflows cf, string tranche) =>
        cf.TrancheCashflows.First(t => t.Key.TrancheName == tranche).Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).ToList();

    private static double Principal(IEnumerable<TrancheCashflow> rows) =>
        rows.Sum(r => r.ScheduledPrincipal + r.UnscheduledPrincipal);

    private static double PoolPrincipal(CollateralCashflows pool) =>
        pool.PeriodCashflows.Sum(p => p.ScheduledPrincipal + p.UnscheduledPrincipal + p.RecoveryPrincipal);

    [Fact]
    public void A_quarterly_deal_distributes_only_on_its_payment_dates()
    {
        var (_, cf) = Run(4, Pool(37)); // 2024-02 .. 2027-02: ends ON a pay month
        var months = Rows(cf, "A").Select(r => r.CashflowDate).ToList();

        months.Should().NotBeEmpty();
        months.Should().OnlyContain(d => PayCalendar.IsPayMonth(d, FirstPay, 3),
            "every distribution falls in a pay month: Feb, May, Aug, Nov");
        months.Should().HaveCount(13, "37 collateral months from a pay month are 13 distributions");
    }

    [Fact]
    public void Every_collateral_dollar_still_reaches_the_bonds()
    {
        var pool = Pool();
        var (_, quarterly) = Run(4, pool);
        var (_, monthly) = Run(12, pool);

        var q = Principal(Rows(quarterly, "A")) + Principal(Rows(quarterly, "B"));
        var m = Principal(Rows(monthly, "A")) + Principal(Rows(monthly, "B"));
        q.Should().BeApproximately(m, 1.0, "holding moves principal in time, never in amount");
        q.Should().BeApproximately(Math.Min(PoolPrincipal(pool), 100_000_000), 1.0);
    }

    [Fact]
    public void A_quarterly_note_accrues_the_whole_quarter()
    {
        var (_, cf) = Run(4, Pool());
        // Skip the first distribution: it accrues from settlement, one month before first pay.
        foreach (var r in Rows(cf, "A").Skip(1).Where(r => r.BeginBalance > 1))
        {
            r.AccrualDays.Should().Be(90, "30/360 over a three-month period");
            r.Interest.Should().BeApproximately(r.BeginBalance * 0.05 * 90 / 360, 0.01);
        }
    }

    [Fact]
    public void Principal_held_to_the_pay_date_earns_the_senior_more_interest()
    {
        var pool = Pool();
        var quarterly = Rows(Run(4, pool).Cf, "A").Sum(r => r.Interest);
        var monthly = Rows(Run(12, pool).Cf, "A").Sum(r => r.Interest);
        quarterly.Should().BeGreaterThan(monthly,
            "the senior's balance stays outstanding until each pay date instead of falling monthly");
    }

    [Fact]
    public void A_projection_ending_mid_period_is_distributed_not_stranded()
    {
        var pool = Pool(35); // ends one month into the twelfth quarter
        var (_, cf) = Run(4, pool);

        // Distributed on the NEXT Payment Date (the pay months are Feb/May/Aug/Nov): the last
        // collections are a Collection Period's, paid when it ends — never on an off-calendar month.
        var lastCollateral = pool.PeriodCashflows.Max(p => p.CashflowDate);
        var lastPay = Rows(cf, "A").Last().CashflowDate;
        PayCalendar.IsPayMonth(lastPay, FirstPay, 3).Should().BeTrue();
        lastPay.Should().BeAfter(lastCollateral).And.BeBefore(lastCollateral.AddMonths(3));
        (Principal(Rows(cf, "A")) + Principal(Rows(cf, "B")))
            .Should().BeApproximately(Math.Min(PoolPrincipal(pool), 100_000_000), 1.0);
    }

    [Fact]
    public void A_fee_on_the_collection_period_reads_its_opening_balance()
    {
        // "the Fee Basis Amount at the beginning of the Collection Period" — the accumulated
        // distribution carries its EARLIEST month's begin balance, not its last one.
        var pool = Pool();
        var (_, cf) = Run(4, pool, feeFormula: "begin_balance * 0.012 / 4");
        var byMonth = pool.PeriodCashflows.ToDictionary(p => (p.CashflowDate.Year, p.CashflowDate.Month));

        foreach (var row in Rows(cf, "Fee").Skip(1).Take(4))
        {
            var opening = row.CashflowDate.AddMonths(-2);
            var expected = byMonth[(opening.Year, opening.Month)].BeginBalance * 0.012 / 4;
            row.Expense.Should().BeApproximately(expected, 0.01);
        }
    }

    [Fact]
    public void A_fee_prorated_for_the_period_charges_every_month_it_covers()
    {
        // `period_months` is the number of collateral months the distribution spends: 3 for a
        // quarter, 1 for a monthly deal, so one formula is right on either calendar.
        var pool = Pool(37);
        const string fee = "begin_balance * 0.012 / 12 * period_months";
        var quarterly = Rows(Run(4, pool, feeFormula: fee).Cf, "Fee").Skip(1).ToList();
        var monthly = Rows(Run(12, pool, feeFormula: fee).Cf, "Fee").Skip(1).ToList();

        quarterly.Should().OnlyContain(r => r.Expense > 0);
        var byMonth = pool.PeriodCashflows.ToDictionary(p => (p.CashflowDate.Year, p.CashflowDate.Month));
        foreach (var row in quarterly.Take(4))
        {
            var opening = row.CashflowDate.AddMonths(-2);
            row.Expense.Should().BeApproximately(
                byMonth[(opening.Year, opening.Month)].BeginBalance * 0.012 / 12 * 3, 0.01);
        }
        foreach (var row in monthly.Take(4))
            row.Expense.Should().BeApproximately(
                byMonth[(row.CashflowDate.Year, row.CashflowDate.Month)].BeginBalance * 0.012 / 12, 0.01);
    }

    [Fact]
    public void Tranches_on_different_calendars_are_refused()
    {
        var act = () => new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithPayFrequency(4, FirstPay)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1) // added after: stays monthly
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(Pool());
        act.Should().Throw<DealModelingException>().WithMessage("*different payment frequencies*");
    }

    [Fact]
    public void An_expense_row_does_not_state_a_calendar()
    {
        // The controller builds expense rows with PayFrequency 12; a quarterly deal must not
        // read them as a second calendar. Added AFTER WithPayFrequency so it keeps its 12, as
        // the controller's row does.
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithPayFrequency(4, FirstPay)
            .WithExpenseTranche("Fee", 0, formula: "1000")
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(Pool(37));
        deal.Tranches.Single(t => t.TrancheName == "Fee").PayFrequency.Should().Be(12);
        Rows(cf, "A").Should().OnlyContain(r => PayCalendar.IsPayMonth(r.CashflowDate, FirstPay, 3));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(12, 1)]
    [InlineData(4, 3)]
    [InlineData(2, 6)]
    [InlineData(1, 12)]
    [InlineData(5, 1)]
    public void The_period_length_comes_from_payments_a_year(int payFrequency, int months)
    {
        PayCalendar.MonthsPerPeriod(payFrequency).Should().Be(months);
    }
}
