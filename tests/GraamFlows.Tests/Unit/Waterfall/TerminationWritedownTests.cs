using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// graam-harmony#4879 — a terminating period applied its write-down TWICE, and the second
/// application ate the balance the payoff was meant to redeem.
///
/// `WritedownAmt` is a function of the PERIOD, not of what is left to absorb. `BeginPeriod`
/// computes it into `CashflowAllocs.Writedown`, which the WRITEDOWN step consumes;
/// `ExecuteTermination` then computed it AGAIN off the same period cashflow and wrote it down
/// again, capped at the surviving balance. On a class that still had balance at the call, that
/// cap meant the second pass took exactly what `dynClass.Pay(...)` was about to redeem, so the
/// class was written to zero and received nothing.
///
/// It is invisible on WAL — a write-down and a redemption retire the balance on the same date —
/// so it only ever surfaced as a yield miss on the to-call basis, and only under enough credit
/// stress for write-downs to still be running when the call fires. Measured on STACR 2025-DNA1
/// (M2A, CER 2.5%, 5% CPR): the WRITEDOWN step allocated 5,974,359 leaving 806,563, this then
/// re-applied the period's write-down capped at that 806,563, and the payoff found nothing.
/// Published to-call yield -37.92 against a modelled -41.33; with the double application
/// removed, -38.16. Across the published grid: 43 yield cells gained, none regressed.
/// </summary>
public class TerminationWritedownTests
{
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    /// <summary>
    ///     Losses big enough to be writing down the subordinate when the call fires, but not
    ///     big enough to exhaust it — the only regime in which the two behaviours differ.
    /// </summary>
    private static CollateralCashflows LosingPool()
    {
        var builder = new TestCollateralBuilder().WithGroupNum("1");
        var balance = 100_000_000.0;
        for (var i = 0; i < 6; i++)
        {
            builder.WithPeriod(date: FirstPayDate.AddMonths(i), beginBalance: balance,
                scheduledPrincipal: 0, unscheduledPrincipal: 0, interest: 500_000,
                defaultedPrincipal: 2_000_000, recoveryPrincipal: 0);
            balance -= 2_000_000;
        }
        return builder.Build();
    }

    private static Dictionary<DateTime, TrancheCashflow> Run(string tranche)
    {
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            // Sized so the balance SURVIVING the terminating period's own write-down is
            // SMALLER than that write-down: 9,000,000 less three periods of 2,000,000 leaves
            // 3,000,000 at the call, the period takes 2,000,000, and 1,000,000 remains. A
            // second application is capped at that 1,000,000 and takes exactly it — the STACR
            // shape, where the double write-down ate precisely what was to be redeemed. With B
            // at 20,000,000 there is 12,000,000 left and a second write-down still leaves
            // plenty to pay, so the payoff assertion below passes either way and proves nothing.
            .WithTranche("B", 9_000_000, 8.0, subOrder: 1)   // absorbs the write-downs
            .WithSequentialWaterfall("A", "B")
            // The call lands in period 4, with B still carrying balance.
            .WithDateTermination(FirstPayDate.AddMonths(3))
            .BuildAndRun(LosingPool());
        var match = cf.TrancheCashflows.FirstOrDefault(t => t.Key.TrancheName == tranche);
        return match.Value?.Cashflows ?? new Dictionary<DateTime, TrancheCashflow>();
    }

    [Fact]
    public void TheTerminatingPeriodWritesDownTheSameLossOnlyOnce()
    {
        var b = Run("B");
        b.Should().NotBeEmpty("the deal must run for this test to mean anything");

        var terminating = b[b.Keys.Max()];
        var earlier = b.Where(kv => kv.Key < b.Keys.Max()).Select(kv => kv.Value).ToList();
        earlier.Should().NotBeEmpty();

        // Every earlier period absorbs one period's loss. The terminating period must not
        // absorb two — which is what a second `WritedownAmt` call produced.
        var typical = earlier.Max(cf => cf.Writedown);
        terminating.Writedown.Should().BeLessThanOrEqualTo(typical + 0.01,
            "the terminating period must apply ONE period's write-down, not two");
    }

    [Fact]
    public void ABalanceSurvivingTheWritedownIsPaidOutNotWrittenOff()
    {
        var b = Run("B");
        var terminating = b[b.Keys.Max()];

        // The class still had balance after the period's own write-down, so termination must
        // REDEEM it. Before the fix this was 0 and the write-down took it instead.
        (terminating.ScheduledPrincipal + terminating.UnscheduledPrincipal)
            .Should().BeGreaterThan(0, "a class with balance at the call is paid off, not written off");
    }

    [Fact]
    public void TheClassIsStillRetiredAtTermination()
    {
        // The guard against over-correcting: paying out instead of writing down must still
        // leave nothing outstanding. WAL depends on this and did not move.
        var b = Run("B");

        b[b.Keys.Max()].Balance.Should().BeApproximately(0, 0.01);
    }
}
