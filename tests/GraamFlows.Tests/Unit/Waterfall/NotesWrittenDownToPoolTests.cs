using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// An RMBS indenture pays its trust fees out of principal when interest cannot cover them, and
/// then allocates the amount the notes stand above the pool by — the Applied Realized Loss Amount —
/// to the most junior class. Measured on a non-QM deal's published yield table: its last class,
/// alone on a pool too small for the fixed fees, published 4.53% at 40 CPR. Paid from interest
/// only, the model gave 5.98%. With the fees taken from principal and nothing written down, the
/// yield tied and the class ended the run with a third of its balance never paid and never lost.
/// </summary>
public class NotesWrittenDownToPoolTests
{
    private static readonly DateTime FirstPay = TestConstants.DefaultFirstPayDate;

    /// <summary>100M pool repaying 1M a month, with 40k of interest against a 100k fee.</summary>
    private static CollateralCashflows Pool()
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 100_000_000.0;
        for (var i = 0; i < 6; i++)
        {
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: bal, scheduledPrincipal: 1_000_000,
                unscheduledPrincipal: 0, interest: 40_000);
            bal -= 1_000_000;
        }

        return b.Build();
    }

    private static TestDealBuilder Deal() =>
        new TestDealBuilder()
            .WithTranche("A", 95_000_000, 0.0, subOrder: 0)
            .WithTranche("B", 5_000_000, 0.0, subOrder: 1)
            .WithExpenseTranche("Fee", 0, formula: "100000")
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A'), SINGLE('B')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A'), SINGLE('B')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A'), SINGLE('B')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A'), SINGLE('B')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('B'), SINGLE('A')))")
            .WithExpensesFromPrincipal("Fee");

    private static List<TrancheCashflow> Rows(DealCashflows cf, string name) =>
        cf.TrancheCashflows.First(t => t.Key.TrancheName == name).Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).ToList();

    [Fact]
    public void Principal_a_fee_took_ahead_of_the_notes_is_written_down_on_the_junior_class()
    {
        var pool = Pool().PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();
        var cf = Deal().WithNotesWrittenDownToPool().BuildAndRun(Pool()).Cashflows;
        var a = Rows(cf, "A");
        var b = Rows(cf, "B");

        // 60k of each month's fee comes out of principal: A is paid 940k, and B takes the 60k.
        for (var i = 1; i < 5; i++)
        {
            a[i].ScheduledPrincipal.Should().BeApproximately(940_000, 0.01);
            b[i].Writedown.Should().BeApproximately(60_000, 0.01, $"period {i + 1}: B is the first class written down");
            (a[i].Balance + b[i].Balance).Should().BeApproximately(pool[i].Balance, 0.01,
                $"period {i + 1}: the notes stand at the pool's balance, not above it");
        }
    }

    [Fact]
    public void Unstated_the_notes_are_left_above_the_pool_as_before()
    {
        var pool = Pool().PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();
        var cf = Deal().BuildAndRun(Pool()).Cashflows;
        Rows(cf, "B").Should().OnlyContain(r => Math.Abs(r.Writedown) < 0.01);
        var last = pool.Count - 2;
        (Rows(cf, "A")[last].Balance + Rows(cf, "B")[last].Balance).Should().BeGreaterThan(pool[last].Balance + 1.0);
    }

    [Fact]
    public void With_nothing_taken_from_principal_it_writes_nothing_down()
    {
        // The fee is not topped up from principal here, so the notes fall with the pool.
        var cf = new TestDealBuilder()
            .WithTranche("A", 95_000_000, 0.0, subOrder: 0)
            .WithTranche("B", 5_000_000, 0.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithNotesWrittenDownToPool()
            .BuildAndRun(Pool()).Cashflows;
        Rows(cf, "A").Should().OnlyContain(r => Math.Abs(r.Writedown) < 0.01);
        Rows(cf, "B").Should().OnlyContain(r => Math.Abs(r.Writedown) < 0.01);
    }
}
