using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// A redemption price is par PLUS accrued and unpaid interest. Termination used to retire
/// balances only, so a class carrying an accumulated interest shortfall at the call forfeited it.
///
/// Termination now pays shortfalls out of the proceeds (the collateral balance at the end of the
/// terminating period) in seniority order, each level's unpaid interest before its principal.
/// What the proceeds do not reach stays on <c>AccumInterestShortfall</c>.
///
/// The fixture starves the junior classes: the pool pays 400,000 of interest a month, A is due
/// 80,000,000 × 5% / 12 = 333,333 and takes it, B (20,000,000 at 8%, due 133,333) gets the
/// 66,667 left, and C (when present) gets nothing — so B and C accrue a shortfall every period
/// and the call lands on it.
/// </summary>
public class TerminationInterestShortfallTests
{
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    private static CollateralCashflows Pool(double balance)
    {
        var builder = new TestCollateralBuilder().WithGroupNum("1");
        for (var i = 0; i < 6; i++)
            builder.WithPeriod(date: FirstPayDate.AddMonths(i), beginBalance: balance,
                scheduledPrincipal: 0, unscheduledPrincipal: 0, interest: 400_000,
                defaultedPrincipal: 0, recoveryPrincipal: 0);
        return builder.Build();
    }

    private static Dictionary<string, Dictionary<DateTime, TrancheCashflow>> Run(double poolBalance,
        bool withC, bool terminate = true)
    {
        var builder = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 8.0, subOrder: 1);
        if (withC)
            builder.WithTranche("C", 5_000_000, 10.0, subOrder: 2)
                .WithSequentialWaterfall("A", "B", "C");
        else
            builder.WithSequentialWaterfall("A", "B");
        if (terminate)
            builder.WithDateTermination(FirstPayDate.AddMonths(3));

        var (_, cf) = builder.BuildAndRun(Pool(poolBalance));
        return cf.TrancheCashflows.ToDictionary(t => t.Key.TrancheName, t => t.Value.Cashflows);
    }

    private static (TrancheCashflow Prior, TrancheCashflow Terminating) LastTwo(
        Dictionary<DateTime, TrancheCashflow> rows)
    {
        var dates = rows.Keys.OrderBy(d => d).ToList();
        return (rows[dates[^2]], rows[dates[^1]]);
    }

    /// <summary>What the class is owed at the call: carried in, plus this period's unpaid accrual.</summary>
    private static double OwedAtCall(Dictionary<DateTime, TrancheCashflow> rows)
    {
        var (prior, terminating) = LastTwo(rows);
        return prior.AccumInterestShortfall + terminating.InterestShortfall;
    }

    [Fact]
    public void AClassCarryingAShortfallIsPaidItAtTermination()
    {
        var b = Run(100_000_000, withC: false)["B"];
        var owed = OwedAtCall(b);
        owed.Should().BeGreaterThan(200_000, "the fixture must have starved B for this to mean anything");

        var (_, terminating) = LastTwo(b);
        terminating.InterestShortfallPayback.Should().BeApproximately(owed, 0.01);
        terminating.AccumInterestShortfall.Should().BeApproximately(0, 0.01);
        terminating.Balance.Should().BeApproximately(0, 0.01, "the balance is still retired");
    }

    [Fact]
    public void TheShortfallIsPaidInTheTerminatingPeriodsInterest()
    {
        var called = Run(100_000_000, withC: false)["B"];
        var (_, terminating) = LastTwo(called);

        // The same period uncalled pays B only the 66,667 the pool had left over.
        var uncalled = Run(100_000_000, withC: false, terminate: false)["B"];
        var sameDate = uncalled[terminating.CashflowDate];

        (terminating.Interest - sameDate.Interest)
            .Should().BeApproximately(OwedAtCall(called), 0.01);
    }

    [Fact]
    public void ProceedsExhaustedAboveAClassLeaveItsShortfallUnpaid()
    {
        // 100,000,000 of collateral: A's par, B's shortfall and B's par take all of it, so
        // nothing reaches C. B is still paid in full — it is senior.
        var classes = Run(100_000_000, withC: true);

        var (_, b) = LastTwo(classes["B"]);
        b.AccumInterestShortfall.Should().BeApproximately(0, 0.01);

        var owedC = OwedAtCall(classes["C"]);
        owedC.Should().BeGreaterThan(0);
        var (_, c) = LastTwo(classes["C"]);
        c.InterestShortfallPayback.Should().Be(0);
        c.AccumInterestShortfall.Should().BeApproximately(owedC, 0.01, "the unpaid shortfall stays visible");
        c.Balance.Should().BeApproximately(0, 0.01, "balance retirement is not gated on the proceeds");
    }

    [Fact]
    public void ProceedsThatRunOutPartWayPayAClassPartOfItsShortfall()
    {
        // The pool's interest does not depend on its balance, so B's shortfall at the call is
        // the same on every run. Size the collateral to A's par + B's shortfall + B's par +
        // 50,000: that 50,000 is all that reaches C.
        var owedB = OwedAtCall(Run(100_000_000, withC: true)["B"]);
        var classes = Run(100_000_000 + owedB + 50_000, withC: true);

        var owedC = OwedAtCall(classes["C"]);
        owedC.Should().BeGreaterThan(50_000);
        var (_, c) = LastTwo(classes["C"]);
        c.InterestShortfallPayback.Should().BeApproximately(50_000, 0.01);
        c.AccumInterestShortfall.Should().BeApproximately(owedC - 50_000, 0.01);
    }
}
