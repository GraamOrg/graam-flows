using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// A Deferrable Note's interest left unpaid while one of its Priority Classes is outstanding is
/// deferred interest added to its principal balance: it bears interest at the class's rate, and it is
/// paid after the class's current interest from interest proceeds, or ahead of its principal from
/// principal proceeds. Measured against a reference model's stressed run: it capitalized a junior
/// class's one unpaid quarter and paid the next quarter's coupon on the raised balance; we carried it
/// as a shortfall, paid it back as interest and never paid interest on it.
/// </summary>
public class DeferrableNotesTests
{
    private static readonly DateTime FirstPay = TestConstants.DefaultFirstPayDate;

    // A 5M at 6% (25,000 a month on 30/360) ranks ahead of E 3M at 12% (30,000 a month).
    private static CollateralCashflows Pool(double[] interest, double[]? principal = null)
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 10_000_000.0;
        for (var i = 0; i < interest.Length; i++)
        {
            var prin = principal?[i] ?? 0;
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: bal, scheduledPrincipal: prin,
                unscheduledPrincipal: 0, interest: interest[i]);
            bal -= prin;
        }

        return b.Build();
    }

    private static readonly DeferrableNoteConfig E = new() { Class = "E", PriorityClasses = new[] { "A" } };

    private static TestDealBuilder Deal(bool deferrable, string prinOrder = "SINGLE('A'), SINGLE('E'), SINGLE('R')") =>
        new TestDealBuilder()
            .WithTranche("A", 5_000_000, 6.0, subOrder: 0)
            .WithTranche("E", 3_000_000, 12.0, subOrder: 1)
            .WithTranche("R", 2_000_000, 0.0, subOrder: 2, cashflowType: "PI", couponType: "Residual")
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A'), SINGLE('E')))")
            .WithPayRule("SchedStruct", $"SET_SCHED_STRUCT(SEQ({prinOrder}))")
            .WithPayRule("PrepayStruct", $"SET_PREPAY_STRUCT(SEQ({prinOrder}))")
            .WithPayRule("RecovStruct", $"SET_RECOV_STRUCT(SEQ({prinOrder}))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('R'), SINGLE('E'), SINGLE('A')))")
            .WithPayRule("ExcessStruct", "SET_EXCESS_STRUCT(SINGLE('R'))")
            .Pipe(b => deferrable ? b.WithDeferrableNotes(E) : b);

    private static List<TrancheCashflow> Rows(DealCashflows cf, string name) =>
        cf.TrancheCashflows.First(t => t.Key.TrancheName == name).Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).ToList();

    [Fact]
    public void Unpaid_interest_is_added_to_the_balance_bears_interest_and_is_repaid_as_principal()
    {
        // Month 1 leaves E 15,000 short; month 2 pays everything.
        var pool = Pool(new[] { 40_000.0, 100_000, 100_000 });
        var plain = Rows(Deal(false).BuildAndRun(pool).Cashflows, "E");
        var cf = Deal(true).BuildAndRun(Pool(new[] { 40_000.0, 100_000, 100_000 })).Cashflows;
        var e = Rows(cf, "E");

        plain[0].AccumInterestShortfall.Should().BeApproximately(15_000, 0.01, "without it, a carried shortfall");
        plain[0].Balance.Should().BeApproximately(3_000_000, 0.01);

        e[0].InterestShortfall.Should().BeApproximately(15_000, 0.01, "still reported as unpaid this period");
        e[0].CapitalizedInterest.Should().BeApproximately(15_000, 0.01);
        e[0].AccumInterestShortfall.Should().BeApproximately(0, 0.01, "not carried as well");
        e[0].Balance.Should().BeApproximately(3_015_000, 0.01, "added to the principal balance");
        var cls = cf.ClassCashflows.First(t => t.Key.TrancheName == "E").Value.Cashflows.OrderBy(c => c.Key).First().Value;
        cls.CapitalizedInterest.Should().BeApproximately(15_000, 0.01, "the class row says so too");
        cls.Balance.Should().BeApproximately(3_015_000, 0.01);

        // Month 2: the coupon accrues on the raised balance; the deferred 15,000 is principal.
        e[1].Interest.Should().BeApproximately(30_150, 0.01, "12%/12 on 3,015,000");
        e[1].ScheduledPrincipal.Should().BeApproximately(15_000, 0.01, "repaid after the current interest");
        e[1].Balance.Should().BeApproximately(3_000_000, 0.01);
        plain[1].Interest.Should().BeApproximately(45_000, 0.01, "the shortfall paid back as interest, no interest on it");
        plain[1].ScheduledPrincipal.Should().BeApproximately(0, 0.01);
    }

    [Fact]
    public void With_no_priority_class_outstanding_it_stays_a_shortfall()
    {
        // Month 1 repays A in full; month 2 leaves E 10,000 short with nothing senior to it.
        var (_, cf) = Deal(true).BuildAndRun(Pool(new[] { 100_000.0, 20_000 }, new[] { 5_000_000.0, 0 }));
        var a = Rows(cf, "A");
        var e = Rows(cf, "E");

        a[0].Balance.Should().BeApproximately(0, 0.01);
        e[1].InterestShortfall.Should().BeApproximately(10_000, 0.01);
        e[1].CapitalizedInterest.Should().Be(0);
        e[1].AccumInterestShortfall.Should().BeApproximately(10_000, 0.01);
        e[1].Balance.Should().BeApproximately(3_000_000, 0.01);
    }

    [Fact]
    public void Nothing_is_deferred_on_a_redemption_date_the_payoff_pays_it_as_interest()
    {
        // Month 1 defers 15,000; the deal terminates in month 2, when interest leaves E 10,000 short.
        // The payoff pays the 10,000 as interest and the 15,000 deferred earlier as principal.
        var (_, cf) = Deal(true).WithDateTermination(FirstPay.AddMonths(1))
            .BuildAndRun(Pool(new[] { 40_000.0, 45_150, 100_000 }));
        var e = Rows(cf, "E");

        e[1].InterestShortfall.Should().BeApproximately(10_000, 0.01);
        e[1].CapitalizedInterest.Should().Be(0);
        e[1].Interest.Should().BeApproximately(30_150, 0.01, "20,150 from interest, 10,000 from the payoff");
        (e[1].ScheduledPrincipal + e[1].UnscheduledPrincipal).Should().BeApproximately(3_015_000, 0.01);
        e[1].Balance.Should().BeApproximately(0, 0.01);
    }

    [Fact]
    public void Principal_paid_to_the_class_repays_its_deferred_interest_first()
    {
        // Principal pays E ahead of A here, so A stays outstanding. Month 1 defers 15,000; month 2 pays
        // exactly the current interest (25,000 + 30,150) and 10,000 of principal to E, which repays
        // 10,000 of the deferred interest; month 3 owes only the 5,000 left, not 15,000 again.
        var (_, cf) = Deal(true, "SINGLE('E'), SINGLE('A'), SINGLE('R')")
            .BuildAndRun(Pool(new[] { 40_000.0, 55_150, 100_000 }, new[] { 0.0, 10_000, 0 }));
        var e = Rows(cf, "E");

        e[1].Interest.Should().BeApproximately(30_150, 0.01);
        e[1].ScheduledPrincipal.Should().BeApproximately(10_000, 0.01);
        e[1].Balance.Should().BeApproximately(3_005_000, 0.01);
        e[2].Interest.Should().BeApproximately(30_050, 0.01, "12%/12 on 3,005,000");
        e[2].ScheduledPrincipal.Should().BeApproximately(5_000, 0.01, "only what is still deferred");
        e[2].Balance.Should().BeApproximately(3_000_000, 0.01);
    }

    [Fact]
    public void The_interest_coverage_test_excludes_deferred_interest_from_the_interest_due()
    {
        // Month 2 owes A 25,000 and E 30,150 of current interest plus 15,000 deferred; IC on 100,000
        // collected is 100,000 / 55,150, not / 70,150.
        var (_, cf) = Deal(true)
            .WithCoverageCascade(new CoverageLevelConfig { Level = "E", Tranches = new[] { "A", "E" }, IcTriggerPct = 100 })
            .BuildAndRun(Pool(new[] { 40_000.0, 100_000, 100_000 }));

        var ic = cf.TriggerResults.Where(t => t.TriggerName == "IC_E").OrderBy(t => t.CashflowDate).ToList();
        ic[1].ActualValue.Should().BeApproximately(100_000 / 55_150.0 * 100, 0.001);
        Rows(cf, "E")[1].ScheduledPrincipal.Should().BeApproximately(15_000, 0.01, "and the deferred interest is still paid");
    }

    [Fact]
    public void The_config_names_tranches_once_each_with_priority_classes_other_than_itself()
    {
        var names = new[] { "A", "B", "E" };
        DeferrableNoteDto Dto(string cls, params string[] priority) => new() { Class = cls, PriorityClasses = priority.ToList() };

        var ok = ExpensesAfterInterestMapper.MapDeferrableNotes(new() { Dto("E", "A", "B") }, names, "d");
        ok.Single().Should().BeEquivalentTo(new DeferrableNoteConfig { Class = "E", PriorityClasses = new[] { "A", "B" } });
        ExpensesAfterInterestMapper.MapDeferrableNotes(null, names, "d").Should().BeEmpty();

        var bad = new[]
        {
            (new List<DeferrableNoteDto> { Dto("X", "A") }, "not a tranche"),
            (new List<DeferrableNoteDto> { Dto("E", "Q") }, "not a tranche"),
            (new List<DeferrableNoteDto> { Dto("E", "A"), Dto("E", "B") }, "twice"),
            (new List<DeferrableNoteDto> { Dto("E") }, "Priority Classes"),
            (new List<DeferrableNoteDto> { Dto("E", "A", "E") }, "Priority Classes"),
        };
        foreach (var (dtos, why) in bad)
            FluentActions.Invoking(() => ExpensesAfterInterestMapper.MapDeferrableNotes(dtos, names, "d"))
                .Should().Throw<ArgumentException>().WithMessage($"*{why}*");
    }
}
