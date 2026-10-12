using FluentAssertions;
using GraamFlows.Api.Transformers;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// A CLO's Principal Priority of Payments opens by paying what the interest proceeds left unpaid of
/// the senior notes' interest, ahead of any principal (and a junior class's, once it is the
/// Controlling Class). Measured on a deal's last Payment Date, when the collateral's final quarter
/// of interest ran short: a senior class's unpaid coupon was carried past its final principal
/// payment, never paid, and the residual took it.
/// </summary>
public class InterestFromPrincipalTests
{
    private static readonly DateTime FirstPay = TestConstants.DefaultFirstPayDate;

    // A 5M at 6% (25,000 a month on 30/360) ranks ahead of B 3M at 12% (30,000 a month).
    private static CollateralCashflows Pool(double[] interest, double[] sched, double[]? prepay = null,
        double[]? recov = null)
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 10_000_000.0;
        for (var i = 0; i < interest.Length; i++)
        {
            var un = prepay?[i] ?? 0;
            var rec = recov?[i] ?? 0;
            // A recovery on a default of the same size: cash, no loss.
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: bal, scheduledPrincipal: sched[i],
                unscheduledPrincipal: un, interest: interest[i], defaultedPrincipal: rec, recoveryPrincipal: rec);
            bal -= sched[i] + un + rec;
        }

        return b.Build();
    }

    private static TestDealBuilder Deal(params string[] fromPrincipal) =>
        new TestDealBuilder()
            .WithTranche("A", 5_000_000, 6.0, subOrder: 0)
            .WithTranche("B", 3_000_000, 12.0, subOrder: 1)
            .WithTranche("R", 2_000_000, 0.0, subOrder: 2, cashflowType: "PI", couponType: "Residual")
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A'), SINGLE('B')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('R')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('R')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('R')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('R'), SINGLE('B'), SINGLE('A')))")
            .WithPayRule("ExcessStruct", "SET_EXCESS_STRUCT(SINGLE('R'))")
            .Pipe(b => fromPrincipal.Length == 0 ? b : b.WithInterestFromPrincipal(fromPrincipal));

    private static List<TrancheCashflow> Rows(DealCashflows cf, string name) =>
        cf.TrancheCashflows.First(t => t.Key.TrancheName == name).Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).ToList();

    private static double Prin(TrancheCashflow r) => r.ScheduledPrincipal + r.UnscheduledPrincipal;

    [Fact]
    public void Unpaid_interest_is_paid_from_principal_ahead_of_the_notes_principal()
    {
        // Interest 40,000 leaves B 15,000 short; 1,000,000 of principal arrives.
        var plain = Deal().BuildAndRun(Pool(new[] { 40_000.0 }, new[] { 1_000_000.0 })).Cashflows;
        Rows(plain, "B")[0].AccumInterestShortfall.Should().BeApproximately(15_000, 0.01, "without it, carried");
        Prin(Rows(plain, "A")[0]).Should().BeApproximately(1_000_000, 0.01);

        var cf = Deal("A", "B").BuildAndRun(Pool(new[] { 40_000.0 }, new[] { 1_000_000.0 })).Cashflows;
        var b = Rows(cf, "B")[0];
        b.Interest.Should().BeApproximately(30_000, 0.01, "its whole coupon, 15,000 of it from principal");
        b.InterestShortfallPayback.Should().BeApproximately(15_000, 0.01);
        b.AccumInterestShortfall.Should().BeApproximately(0, 0.01);
        Prin(Rows(cf, "A")[0]).Should().BeApproximately(985_000, 0.01, "the principal left after it");
    }

    [Fact]
    public void Senior_first_and_only_as_far_as_the_principal_goes()
    {
        // Interest 20,000 leaves A 5,000 and B 30,000 short; 12,000 of principal (scheduled, unscheduled
        // and recoveries): A's 5,000, then 7,000 of B's.
        var cf = Deal("A", "B")
            .BuildAndRun(Pool(new[] { 20_000.0 }, new[] { 2_000.0 }, new[] { 3_000.0 }, new[] { 7_000.0 })).Cashflows;
        Rows(cf, "A")[0].InterestShortfallPayback.Should().BeApproximately(5_000, 0.01);
        Rows(cf, "A")[0].AccumInterestShortfall.Should().BeApproximately(0, 0.01);
        Rows(cf, "B")[0].InterestShortfallPayback.Should().BeApproximately(7_000, 0.01);
        Rows(cf, "B")[0].AccumInterestShortfall.Should().BeApproximately(23_000, 0.01, "the rest is carried");
        Prin(Rows(cf, "A")[0]).Should().BeApproximately(0, 0.01, "no principal is left for the notes");
    }

    [Fact]
    public void What_earlier_Payment_Dates_left_unpaid_is_paid_too()
    {
        // Month 1 leaves B 15,000 short with no principal to pay it; month 2 leaves another 15,000
        // short (it is paid 15,000 of a 45,000 ask), and principal pays all 30,000 carried.
        var cf = Deal("A", "B")
            .BuildAndRun(Pool(new[] { 40_000.0, 40_000 }, new[] { 0.0, 1_000_000 })).Cashflows;
        var b = Rows(cf, "B");
        b[0].AccumInterestShortfall.Should().BeApproximately(15_000, 0.01);
        b[1].InterestShortfallPayback.Should().BeApproximately(30_000, 0.01);
        b[1].AccumInterestShortfall.Should().BeApproximately(0, 0.01);
        Prin(Rows(cf, "A")[1]).Should().BeApproximately(970_000, 0.01);
    }

    [Fact]
    public void Only_the_named_classes()
    {
        var cf = Deal("A").BuildAndRun(Pool(new[] { 40_000.0 }, new[] { 1_000_000.0 })).Cashflows;
        Rows(cf, "B")[0].AccumInterestShortfall.Should().BeApproximately(15_000, 0.01);
        Prin(Rows(cf, "A")[0]).Should().BeApproximately(1_000_000, 0.01);
    }

    [Fact]
    public void A_deferrable_class_with_a_senior_outstanding_is_capitalized_not_paid_from_principal_until_it_is_the_most_senior()
    {
        // B is deferrable behind A. Month 1: A outstanding, B's 15,000 is capitalized (principal, not
        // a carried shortfall) and 1,000,000 of principal goes to A. Month 2 repays A in full; month 3
        // leaves B 10,000 short with nothing senior to it, and principal pays it as interest.
        var cf = Deal("A", "B")
            .WithDeferrableNotes(new DeferrableNoteConfig { Class = "B", PriorityClasses = new[] { "A" } })
            .BuildAndRun(Pool(new[] { 40_000.0, 100_000, 20_000 }, new[] { 1_000_000.0, 4_000_000, 1_000_000 }))
            .Cashflows;
        var b = Rows(cf, "B");
        b[0].CapitalizedInterest.Should().BeApproximately(15_000, 0.01);
        b[0].InterestShortfallPayback.Should().Be(0);
        Prin(Rows(cf, "A")[0]).Should().BeApproximately(1_000_000, 0.01);

        Rows(cf, "A")[1].Balance.Should().BeApproximately(0, 0.01);
        b[2].CapitalizedInterest.Should().Be(0);
        b[2].InterestShortfallPayback.Should().BeApproximately(10_000, 0.01);
        b[2].AccumInterestShortfall.Should().BeApproximately(0, 0.01);
    }

    [Fact]
    public void The_config_names_tranches_once_each()
    {
        var names = new[] { "A", "B" };
        ExpensesAfterInterestMapper.MapInterestFromPrincipal(new() { "A", "B" }, names, "d").Should().Equal("A", "B");
        ExpensesAfterInterestMapper.MapInterestFromPrincipal(null, names, "d").Should().BeEmpty();
        FluentActions.Invoking(() => ExpensesAfterInterestMapper.MapInterestFromPrincipal(new() { "A", "X" }, names, "d"))
            .Should().Throw<ArgumentException>().WithMessage("*not a tranche*");
        FluentActions.Invoking(() => ExpensesAfterInterestMapper.MapInterestFromPrincipal(new() { "A", "a" }, names, "d"))
            .Should().Throw<ArgumentException>().WithMessage("*twice*");
    }
}
