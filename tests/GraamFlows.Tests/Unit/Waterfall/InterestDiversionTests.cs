using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Assumptions;
using GraamFlows.Factories;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall.MarketTranche;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// A CLO's reinvestment-period interest diversion test: while the junior OC ratio is below its
/// trigger, the lesser of a share of the remaining interest and the cure buys collateral instead
/// of reaching the residual. The cure BUYS collateral, so the waterfall and the reinvestment loop
/// run to a fixed point (<see cref="CfCore.RunReinvestingWaterfall" />).
/// </summary>
public class InterestDiversionTests
{
    private static readonly DateTime FirstPay = TestConstants.DefaultFirstPayDate;
    private const double Pool = 100_000_000;
    private const double SeniorFace = 80_000_000;
    private const double JuniorFace = 15_000_000; // notes 95M: the ratio is 105.26%
    private const double MonthlyInterest = 800_000;
    private const int Periods = 24;

    /// <summary>A pool holding its balance with no principal, so the ratio stays put.</summary>
    private static CollateralCashflows FlatPool(int periods = Periods)
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        for (var i = 0; i < periods; i++)
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: Pool, scheduledPrincipal: 0,
                unscheduledPrincipal: 0, interest: MonthlyInterest);
        return b.Build();
    }

    private static InterestDiversionConfig Test(double trigger = 106.0, double cap = 50.0, int endMonths = 12) => new()
    {
        Tranches = new[] { "A", "E" }, TriggerPct = trigger, MaxPctOfInterest = cap,
        EndDate = FirstPay.AddMonths(endMonths), PurchasePricePct = 99.5
    };

    private static TestDealBuilder Deal(InterestDiversionConfig? test) =>
        new TestDealBuilder()
            .WithTranche("A", SeniorFace, 5.0, subOrder: 0)
            .WithTranche("E", JuniorFace, 8.0, subOrder: 1)
            .WithTranche("R", 5_000_000, 0.0, subOrder: 2, cashflowType: "PI", couponType: "Residual")
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A'), SINGLE('E')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A'), SINGLE('E')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A'), SINGLE('E')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A'), SINGLE('E')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('E'), SINGLE('A')))")
            .WithPayRule("ExcessStruct", "SET_EXCESS_STRUCT(SINGLE('R'))")
            .Pipe(b => test == null ? b : b.WithInterestDiversion(test));

    private static SortedDictionary<DateTime, TrancheCashflow> Rows(DealCashflows cf, string name) =>
        new(cf.TrancheCashflows.First(t => t.Key.TrancheName == name).Value.Cashflows);

    [Fact]
    public void A_failing_test_diverts_the_lesser_of_the_cap_and_the_cure_from_the_residual()
    {
        var plain = Rows(Deal(null).BuildAndRun(FlatPool()).Cashflows, "R");
        var (_, cf) = Deal(Test()).BuildAndRun(FlatPool());
        var r = Rows(cf, "R");

        cf.InterestDiversions.Should().NotBeEmpty();
        foreach (var d in cf.InterestDiversions)
        {
            d.RatioPct.Should().BeApproximately(Pool / (SeniorFace + JuniorFace) * 100, 1e-9);
            d.CureCash.Should().BeApproximately((1.06 * (SeniorFace + JuniorFace) - Pool) * 0.995, 1e-6);
            var remaining = plain[d.Date].Interest; // what the residual would otherwise have received
            d.Diverted.Should().BeApproximately(Math.Min(0.5 * remaining, d.CureCash), 1e-6);
            r[d.Date].Interest.Should().BeApproximately(remaining - d.Diverted, 1e-6);
        }
    }

    [Fact]
    public void The_cap_binds_when_the_cure_exceeds_it_and_the_cure_when_it_does_not()
    {
        var capped = Deal(Test(cap: 50)).BuildAndRun(FlatPool()).Cashflows.InterestDiversions.First();
        capped.Diverted.Should().BeLessThan(capped.CureCash, "half the remaining interest is less than the cure");
        var cured = Deal(Test(trigger: 105.3, cap: 100)).BuildAndRun(FlatPool()).Cashflows.InterestDiversions.First();
        cured.Diverted.Should().BeApproximately(cured.CureCash, 1e-6, "a small shortfall is cured in full");
    }

    [Fact]
    public void A_passing_ratio_and_a_date_past_the_end_divert_nothing()
    {
        Deal(Test(trigger: 105.0)).BuildAndRun(FlatPool()).Cashflows.InterestDiversions.Should().BeEmpty();
        var (_, cf) = Deal(Test(endMonths: 3)).BuildAndRun(FlatPool());
        cf.InterestDiversions.Should().NotBeEmpty();
        cf.InterestDiversions.Should().OnlyContain(d => d.Date <= FirstPay.AddMonths(3));
    }

    [Fact]
    public void The_ratio_is_measured_before_its_own_cure()
    {
        // The same pool with this date's cure already bought and booked on the row (as the second
        // pass sees it): the test must read the same ratio and divert the same amount, or the
        // passes would oscillate between diverting and not.
        var pool = FlatPool();
        var first = Deal(Test()).BuildAndRun(pool).Cashflows.InterestDiversions.First();
        var booked = FlatPool();
        var row = booked.PeriodCashflows.First(p => p.CashflowDate == first.Date);
        var face = first.Diverted / 0.995;
        row.Balance += face;
        row.AdditionalPurchaseFace = face;
        var again = Deal(Test()).BuildAndRun(booked).Cashflows.InterestDiversions.First(d => d.Date == first.Date);
        again.RatioPct.Should().BeApproximately(first.RatioPct, 1e-9);
        again.Diverted.Should().BeApproximately(first.Diverted, 1e-6);
    }

    // --- the fixed point, end to end ------------------------------------------------------------

    private static ReinvestmentConfig Reinvest() => new()
    {
        ReinvestStartDate = FirstPay, ReinvestEndDate = FirstPay.AddMonths(12), ReinvestAllEligibleProceeds = true,
        Templates = new[]
        {
            new ReinvestTemplate
            {
                AllocationPct = 100, Price = 99.5, AmortizationType = AmortizationType.Bullet,
                CouponRate = 9.0, TermMonths = 36
            }
        }
    };

    private static IAssetAssumptions Zero() => new AssetAssumptions(
        PrepaymentTypeEnum.CPR, new ConstVector(DateUtil.CalcAbsT(FirstPay), 0.0),
        DefaultTypeEnum.CDR, new ConstVector(DateUtil.CalcAbsT(FirstPay), 0.0),
        new ConstVector(DateUtil.CalcAbsT(FirstPay), 0.0));

    private static (CollateralCashflows Collateral, IList<ReinvestmentPurchase> Purchases, DealCashflows Waterfall,
        int Passes) RunToFixedPoint(InterestDiversionConfig? test, CollateralCashflows posted)
    {
        var deal = Deal(test).WithReinvestment(Reinvest()).Build();
        var assumps = DealLevelAssumptions.CreateConstAssumptions(FirstPay, DateUtil.CalcAbsT(FirstPay), 0, 0, 0);
        var rates = new ConstantTestRateProvider(5.0);
        return CfCore.RunReinvestingWaterfall(deal, posted, deal.ReinvestmentConfig!, FirstPay, Zero(), rates,
            cc => WaterfallFactory.GetWaterfall(deal.CashflowEngine)
                .Waterfall(deal, rates, FirstPay, cc, assumps, new TrancheAllocator()));
    }

    [Fact]
    public void Without_a_test_the_runner_is_one_pass_over_the_posted_rows()
    {
        var posted = FlatPool();
        var run = RunToFixedPoint(null, posted);
        run.Passes.Should().Be(1);
        run.Waterfall.InterestDiversions.Should().BeEmpty();
    }

    [Fact]
    public void Diverted_cash_buys_collateral_and_the_passes_settle()
    {
        var run = RunToFixedPoint(Test(endMonths: 2), FlatPool());
        var diversions = run.Waterfall.InterestDiversions;
        diversions.Should().NotBeEmpty();
        run.Passes.Should().BeInRange(2, diversions.Count + 1);

        foreach (var d in diversions)
        {
            var bought = run.Purchases.Single(p => p.CashflowDate == d.Date);
            bought.FromAdditionalCash.Should().BeApproximately(d.Diverted, 1e-6, "the diverted cash is what bought it");
            var row = run.Collateral.PeriodCashflows.Single(p => p.CashflowDate == d.Date);
            row.AdditionalPurchaseFace.Should().BeApproximately(d.Diverted / 0.995, 1e-6);
        }
    }

    [Fact]
    public void Later_passes_start_from_the_posted_pool_not_the_merged_one()
    {
        // Merging mutates the posted rows. A second pass over them would merge the first pass's
        // collateral in again — measured on a live CLO, the base pool vanished from mid-life on.
        var run = RunToFixedPoint(Test(endMonths: 2), FlatPool());
        var extraFace = run.Waterfall.InterestDiversions.Sum(d => d.Diverted / 0.995);
        // On the posted pool's last date (the bought bullets run on past it, to their maturity).
        var lastPosted = FirstPay.AddMonths(Periods - 1);
        var last = run.Collateral.PeriodCashflows.Single(p => p.CashflowDate == lastPosted);
        last.Balance.Should().BeApproximately(Pool + extraFace, 1.0,
            "the pool plus exactly the collateral the diversions bought — nothing counted twice or lost");
        run.Collateral.PeriodCashflows.Sum(p => p.Interest).Should().BeGreaterThan(MonthlyInterest * Periods,
            "the bought collateral earns on top of the posted pool's interest");
    }

    [Fact]
    public void Additional_cash_is_spent_on_top_of_principal_and_never_drawn_from_it()
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        for (var i = 0; i < 6; i++)
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: Pool - 1e6 * i, scheduledPrincipal: 1e6,
                unscheduledPrincipal: 0, interest: 0);
        var pool = b.Build().PeriodCashflows.ToList();
        var date = FirstPay.AddMonths(2);
        var plain = CfCore.BuildReinvestment(pool, Reinvest(), FirstPay, Zero(), null);
        var extra = CfCore.BuildReinvestment(pool, Reinvest(), FirstPay, Zero(), null,
            new Dictionary<DateTime, double> { [date] = 50_000 });

        var p0 = plain.Purchases.Single(p => p.CashflowDate == date);
        var p1 = extra.Purchases.Single(p => p.CashflowDate == date);
        p1.CashSpent.Should().BeApproximately(p0.CashSpent + 50_000, 1e-6);
        p1.FromAdditionalCash.Should().BeApproximately(50_000, 1e-6);
        (p1.FromScheduledPrincipal + p1.FromUnscheduledPrincipal + p1.FromRecoveryPrincipal)
            .Should().BeApproximately(p0.FromScheduledPrincipal + p0.FromUnscheduledPrincipal + p0.FromRecoveryPrincipal, 1e-6,
                "the pool's principal is drawn exactly as before");
        p1.FaceBought.Should().BeApproximately(p0.FaceBought + 50_000 / 0.995, 1e-6);
    }

    [Fact]
    public void The_wire_requires_reinvestment_and_defaults_from_it()
    {
        var dto = new InterestDiversionDto { Tranches = new() { "A", "E" }, TriggerPct = 103.75, MaxPctOfInterest = 50 };
        FluentActions.Invoking(() => InterestDiversionMapper.Map(dto, null, "D")).Should()
            .Throw<InvalidOperationException>().WithMessage("*requires a reinvestment config*");

        var cfg = InterestDiversionMapper.Map(dto, Reinvest(), "D")!;
        cfg.EndDate.Should().Be(FirstPay.AddMonths(12), "the reinvestment period's end");
        cfg.PurchasePricePct.Should().Be(99.5, "the templates' price");
        cfg.MaxPctOfInterest.Should().Be(50);
        InterestDiversionMapper.Map(null, Reinvest(), "D").Should().BeNull();
        FluentActions.Invoking(() => InterestDiversionMapper.Map(
                new InterestDiversionDto { Tranches = new() { "A" }, TriggerPct = 0 }, Reinvest(), "D"))
            .Should().Throw<InvalidOperationException>().WithMessage("*triggerPct*");
    }

    [Fact]
    public void A_path_that_cannot_buy_the_cure_refuses_the_test()
    {
        var deal = Deal(Test()).WithReinvestment(Reinvest()).Build();
        FluentActions.Invoking(() => new CfCore(FirstPay, deal).GenerateAssetCashflows(new ConstantTestRateProvider(5.0),
                DealLevelAssumptions.CreateConstAssumptions(FirstPay, DateUtil.CalcAbsT(FirstPay), 0, 0, 0)))
            .Should().Throw<NotSupportedException>().WithMessage("*interestDiversion*");
    }
}

internal static class BuilderPipe
{
    public static T Pipe<T>(this T x, Func<T, T> f) => f(x);
}
