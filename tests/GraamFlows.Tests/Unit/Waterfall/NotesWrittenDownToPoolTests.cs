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

    // ---- The same amount, stated on the WRITEDOWN step ----------------------------------------
    //
    // `size: overPool` on the WRITEDOWN step is this option's amount taken in place of the
    // period's loss, where the option adds it at the end of the period. With no step paying the
    // notes out of excess ahead of the write-down, the two are the same number.

    private static readonly string[] Order =
    {
        "EXPENSE", "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN"
    };

    /// <summary>100M pool, 1M a month scheduled, and 500k defaulted at 40% recovery from month 2.</summary>
    private static CollateralCashflows LossPool()
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 100_000_000.0;
        for (var i = 0; i < 6; i++)
        {
            var defaulted = i == 0 ? 0 : 500_000.0;
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: bal, scheduledPrincipal: 1_000_000,
                unscheduledPrincipal: 0, interest: 40_000, defaultedPrincipal: defaulted,
                recoveryPrincipal: defaulted * 0.4);
            bal -= 1_000_000 + defaulted;
        }

        return b.Build();
    }

    private static void AssertSameRows(DealCashflows option, DealCashflows step)
    {
        foreach (var name in new[] { "A", "B" })
        {
            var o = Rows(option, name);
            var s = Rows(step, name);
            s.Should().HaveSameCount(o);
            for (var i = 0; i < o.Count; i++)
            {
                var at = $"{name} period {i + 1}";
                s[i].ScheduledPrincipal.Should().BeApproximately(o[i].ScheduledPrincipal, 0.01, at);
                s[i].UnscheduledPrincipal.Should().BeApproximately(o[i].UnscheduledPrincipal, 0.01, at);
                s[i].Interest.Should().BeApproximately(o[i].Interest, 0.01, at);
                s[i].Writedown.Should().BeApproximately(o[i].Writedown, 0.01, at);
                s[i].Balance.Should().BeApproximately(o[i].Balance, 0.01, at);
            }
        }
    }

    [Fact]
    public void Sized_overPool_on_the_step_a_fee_taken_from_principal_gives_the_options_numbers()
    {
        var option = Deal().WithExecutionOrder(Order).WithNotesWrittenDownToPool().BuildAndRun(Pool()).Cashflows;
        var step = Deal().WithExecutionOrder(Order).WithWritedownSize(WritedownSize.OverPool)
            .BuildAndRun(Pool()).Cashflows;

        Rows(step, "B").Sum(r => r.Writedown).Should().BeGreaterThan(100_000, "the fee has to bite");
        AssertSameRows(option, step);
    }

    [Fact]
    public void Sized_overPool_on_the_step_collateral_losses_and_the_fee_give_the_options_numbers()
    {
        var option = Deal().WithExecutionOrder(Order).WithNotesWrittenDownToPool().BuildAndRun(LossPool())
            .Cashflows;
        var step = Deal().WithExecutionOrder(Order).WithWritedownSize(WritedownSize.OverPool)
            .BuildAndRun(LossPool()).Cashflows;

        // 300k of loss and 60k of fee a month: more than the fee alone.
        Rows(step, "B")[2].Writedown.Should().BeApproximately(360_000, 0.01);
        AssertSameRows(option, step);
    }

    [Fact]
    public void Stated_both_ways_the_amount_is_written_down_once()
    {
        var step = Deal().WithExecutionOrder(Order).WithWritedownSize(WritedownSize.OverPool)
            .BuildAndRun(LossPool()).Cashflows;
        var both = Deal().WithExecutionOrder(Order).WithWritedownSize(WritedownSize.OverPool)
            .WithNotesWrittenDownToPool().BuildAndRun(LossPool()).Cashflows;

        AssertSameRows(step, both);
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
