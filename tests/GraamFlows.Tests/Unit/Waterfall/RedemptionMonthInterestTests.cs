using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// A call whose Redemption Price pays "interest for the month in which the notes are redeemed … to
/// and including the Payment Date" (<c>redemptionInterest: ToPaymentDate</c>).
///
/// The fixture's notes are 30/360, paid on the 25th, with a 24-day delay: each accrual period ends
/// on the 1st, 24 days before the Payment Date that pays it. On the Payment Date the deal is called,
/// a note has therefore already run 24 days of the next period, and this call pays them on the
/// balance it redeems: balance × coupon × 24/360. The pool pays every coupon in full (no shortfall)
/// and amortizes A, so "the balance redeemed" and "the period's opening balance" differ.
/// </summary>
public class RedemptionMonthInterestTests
{
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate; // the 25th
    private static readonly DateTime CallDate = FirstPayDate.AddMonths(3);
    private const int Delay = 24;

    private static CollateralCashflows Pool()
    {
        var builder = new TestCollateralBuilder().WithGroupNum("1");
        var balance = 100_000_000.0;
        for (var i = 0; i < 6; i++)
        {
            builder.WithPeriod(date: FirstPayDate.AddMonths(i), beginBalance: balance,
                scheduledPrincipal: 2_000_000, unscheduledPrincipal: 0, interest: 700_000,
                defaultedPrincipal: 0, recoveryPrincipal: 0);
            balance -= 2_000_000;
        }

        return builder.Build();
    }

    private static Dictionary<string, Dictionary<DateTime, TrancheCashflow>> Run(string? redemptionInterest,
        int payDelay = Delay, bool terminate = true, bool exchange = false, bool cleanUp = false)
    {
        var builder = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 8.0, subOrder: 1);
        if (exchange)
            builder.WithExchangeClass("AB", subOrder: 50, "A", "B");
        builder.WithPayDelay(payDelay).WithSequentialWaterfall("A", "B");
        if (terminate && cleanUp)
            // The pool factor is 0.92 after the Payment Date three months in, below 93%: the
            // clean-up fires there.
            builder.WithCleanUpTermination(93, redemptionInterest);
        else if (terminate)
            builder.WithDateTermination(CallDate, redemptionInterest: redemptionInterest);

        var (_, cf) = builder.BuildAndRun(Pool());
        return cf.TrancheCashflows.ToDictionary(t => t.Key.TrancheName, t => t.Value.Cashflows);
    }

    private static TrancheCashflow Last(Dictionary<DateTime, TrancheCashflow> rows) =>
        rows[rows.Keys.Max()];

    /// <summary>What the call owes the class for the month of redemption, read off the uncalled run.</summary>
    private static double Owed(TrancheCashflow uncalledSameDate, int days = Delay) =>
        uncalledSameDate.Balance * uncalledSameDate.Coupon * .01 * days / 360.0;

    [Fact]
    public void TheCallPaysTheDaysFromTheEndOfTheAccrualPeriodToThePaymentDate()
    {
        var called = Run("ToPaymentDate");
        var uncalled = Run(null, terminate: false);

        foreach (var name in new[] { "A", "B" })
        {
            var row = Last(called[name]);
            var same = uncalled[name][row.CashflowDate];
            var owed = Owed(same);
            owed.Should().BeGreaterThan(10_000, $"{name} must be outstanding at the call for this to mean anything");

            (row.Interest - same.Interest).Should().BeApproximately(owed, 0.01);
            row.RedemptionInterest.Should().BeApproximately(owed, 0.01);
            row.Balance.Should().BeApproximately(0, 0.01, "the balance is still retired");
        }
    }

    [Fact]
    public void ItIsChargedOnTheBalanceRedeemedNotThePeriodsOpeningBalance()
    {
        var row = Last(Run("ToPaymentDate")["A"]);
        var same = Run(null, terminate: false)["A"][row.CashflowDate];
        same.BeginBalance.Should().BeGreaterThan(same.Balance + 1_000_000, "A must amortize on the call date");

        row.RedemptionInterest.Should().BeApproximately(same.Balance * 5.0 * .01 * Delay / 360.0, 0.01);
    }

    [Fact]
    public void EveryEarlierPaymentDateIsUntouched()
    {
        var called = Run("ToPaymentDate");
        var plain = Run(null);
        foreach (var name in new[] { "A", "B" })
            foreach (var (date, row) in called[name].Where(kv => kv.Key < CallDate))
            {
                row.Interest.Should().Be(plain[name][date].Interest);
                row.RedemptionInterest.Should().Be(0);
            }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("None")]
    public void WithoutItTheCallPaysWhatItAlwaysHas(string? value)
    {
        var plain = Run(null);
        var stated = Run(value);
        foreach (var name in new[] { "A", "B" })
        {
            var row = Last(stated[name]);
            row.RedemptionInterest.Should().Be(0);
            row.Interest.Should().Be(Last(plain[name]).Interest);
            row.ScheduledPrincipal.Should().Be(Last(plain[name]).ScheduledPrincipal);
            row.UnscheduledPrincipal.Should().Be(Last(plain[name]).UnscheduledPrincipal);
        }
    }

    [Fact]
    public void ANoteWithNoDelayHasNothingToAdd()
    {
        var called = Run("ToPaymentDate", payDelay: 0);
        var plain = Run(null, payDelay: 0);
        foreach (var name in new[] { "A", "B" })
        {
            Last(called[name]).RedemptionInterest.Should().Be(0);
            Last(called[name]).Interest.Should().Be(Last(plain[name]).Interest);
        }
    }

    [Fact]
    public void TheCleanUpLegPaysItToo()
    {
        var called = Run("ToPaymentDate", cleanUp: true);
        var plain = Run(null, cleanUp: true);
        var uncalled = Run(null, terminate: false);

        var row = Last(called["B"]);
        Last(plain["B"]).CashflowDate.Should().Be(row.CashflowDate, "both runs are called on the same date");
        row.RedemptionInterest.Should().BeApproximately(Owed(uncalled["B"][row.CashflowDate]), 0.01);
    }

    [Fact]
    public void AnExchangeViewCarriesItsComponentsRedemptionInterest()
    {
        var called = Run("ToPaymentDate", exchange: true);
        var plain = Run(null, exchange: true);

        var ab = Last(called["AB"]);
        var expected = Last(called["A"]).RedemptionInterest + Last(called["B"]).RedemptionInterest;
        expected.Should().BeGreaterThan(0);
        (ab.Interest - plain["AB"][ab.CashflowDate].Interest).Should().BeApproximately(expected, 0.01);
    }

    [Fact]
    public void AValueTheEngineDoesNotKnowIsRefused()
    {
        var act = () => Run("ThroughMonthEnd");
        act.Should().Throw<DealModelingException>().WithMessage("*redemptionInterest 'ThroughMonthEnd'*");
    }
}
