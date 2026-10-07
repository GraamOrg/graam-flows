using FluentAssertions;
using GraamFlows;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Assumptions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.Reinvestment;

/// <summary>
/// Uncapped reinvestment (<see cref="ReinvestmentConfig.ReinvestAllEligibleProceeds" />).
///
/// A target sizes each purchase as MAX(0, target − poolBalance) of CASH. Bought below par that
/// cash buys more face than the gap, so the pool overshoots and the next period's proceeds beyond
/// the smaller gap pass through as paydown: a pool held "at par" pays the purchase discount to the
/// notes every period. A transaction whose documents set a par level only as a floor on trading
/// reinvests everything instead, and its pool accretes. The flag expresses that; the default
/// (a target) is unchanged.
/// </summary>
public class ReinvestAllEligibleProceedsTests
{
    private static readonly DateTime Proj = new(2026, 6, 1);
    private const double Start = 1_000_000.0;
    private const double Amort = 10_000.0;
    private const int Periods = 60;
    private const int WindowMonths = 24;
    private const double Price = 99.5;

    private static List<PeriodCashflows> BasePool()
    {
        var pool = new List<PeriodCashflows>();
        for (var p = 0; p < Periods; p++)
        {
            var begin = Start - Amort * p;
            pool.Add(new PeriodCashflows
            {
                CashflowDate = Proj.AddMonths(p), GroupNum = "1", BeginBalance = begin,
                Balance = begin - Amort, ScheduledPrincipal = Amort, Interest = begin * 0.05 / 12.0
            });
        }

        return pool;
    }

    private static IAssetAssumptions ZeroAssumps()
    {
        var anchor = DateUtil.CalcAbsT(Proj);
        return new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchor, 0.0),
            DefaultTypeEnum.CDR, new ConstVector(anchor, 0.0),
            new ConstVector(anchor, 0.0));
    }

    private static ReinvestmentConfig Config(bool uncapped) => new()
    {
        ReinvestStartDate = Proj,
        ReinvestEndDate = Proj.AddMonths(WindowMonths),
        Target = uncapped ? 0.0 : Start,
        ReinvestAllEligibleProceeds = uncapped,
        Templates = new[]
        {
            new ReinvestTemplate
            {
                AllocationPct = 100, Price = Price, AmortizationType = AmortizationType.Bullet,
                CouponRate = 5.0, TermMonths = 36
            }
        }
    };

    private static ReinvestmentResult Loop(bool uncapped) =>
        CfCore.BuildReinvestment(BasePool(), Config(uncapped), Proj, ZeroAssumps(), null);

    [Fact]
    public void A_par_target_releases_the_purchase_discount_as_paydown()
    {
        // The mechanism the flag exists for, pinned on the default path: after the first purchase
        // overshoots par, every later purchase spends less than the proceeds it had.
        // Exactly: what each purchase leaves unspent is the face the previous one bought above
        // its cash (the pool sat that far over target), about 0.5% of the proceeds at 99.5.
        var purchases = Loop(uncapped: false).Purchases;
        purchases.Should().HaveCountGreaterThan(2);
        for (var i = 1; i < purchases.Count; i++)
        {
            var released = purchases[i].ProceedsAvailable - purchases[i].CashSpent;
            var overshoot = purchases[i - 1].FaceBought - purchases[i - 1].CashSpent;
            released.Should().BeApproximately(overshoot, 1e-6);
            released.Should().BeGreaterThan(Amort * 0.004, "the leak is material, not float noise");
        }
    }

    [Fact]
    public void Uncapped_reinvests_every_eligible_dollar()
    {
        var purchases = Loop(uncapped: true).Purchases;
        purchases.Should().HaveCountGreaterThan(2);
        foreach (var p in purchases)
            p.CashSpent.Should().BeApproximately(p.ProceedsAvailable, 1e-9);
    }

    [Fact]
    public void Uncapped_lets_the_pool_accrete_above_par_by_exactly_the_discount()
    {
        var purchases = Loop(uncapped: true).Purchases;
        purchases.Should().HaveCount(WindowMonths + 1, "one purchase per window period");
        var accretion = Amort * (100.0 / Price - 1.0);
        for (var i = 1; i < purchases.Count; i++)
            purchases[i].PoolBalanceBefore.Should().BeApproximately(Start - Amort + accretion * i, 1e-6);
    }

    [Fact]
    public void An_uncapped_purchase_reports_no_target()
    {
        var uncapped = Loop(uncapped: true).Purchases;
        var capped = Loop(uncapped: false).Purchases;
        uncapped.Should().NotBeEmpty();
        capped.Should().NotBeEmpty();
        uncapped.Should().OnlyContain(p => p.TargetBalance == null);
        capped.Should().OnlyContain(p => p.TargetBalance == Start);
    }

    [Fact]
    public void A_target_and_the_flag_together_are_refused()
    {
        var both = Config(uncapped: true) with { Target = Start };
        var act = () => both.Validate("D");
        act.Should().Throw<InvalidOperationException>().WithMessage("*reinvestAllEligibleProceeds*");

        var schedule = Config(uncapped: true) with { TargetSchedule = new[] { Start } };
        var act2 = () => schedule.Validate("D");
        act2.Should().Throw<InvalidOperationException>().WithMessage("*reinvestAllEligibleProceeds*");

        var flagOnly = () => Config(uncapped: true).Validate("D");
        flagOnly.Should().NotThrow();
    }

    [Fact]
    public void The_wire_flag_maps_and_defaults_off()
    {
        static ReinvestmentDto Dto(bool? flag, double target) => new()
        {
            ReinvestEndDate = Proj.AddMonths(WindowMonths),
            Target = target,
            ReinvestAllEligibleProceeds = flag,
            Templates = new List<ReinvestTemplateDto> { new() { AllocationPct = 100, TermMonths = 36 } }
        };

        ReinvestmentConfigMapper.Map(Dto(true, 0.0), "D")!.ReinvestAllEligibleProceeds.Should().BeTrue();
        ReinvestmentConfigMapper.Map(Dto(null, Start), "D")!.ReinvestAllEligibleProceeds.Should().BeFalse();
        ReinvestmentConfigMapper.Map(Dto(false, Start), "D")!.CapAt(0).Should().Be(Start);
    }
}
