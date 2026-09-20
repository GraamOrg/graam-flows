using FluentAssertions;
using GraamFlows;
using GraamFlows.Assumptions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.Reinvestment;

/// <summary>
/// The POST-REINVESTMENT window: a second, narrower window running on from the reinvestment
/// end date.
///
/// A CLO indenture commonly keeps reinvesting after the Reinvestment Period ends, but only out
/// of UNSCHEDULED proceeds — prepayments and credit-risk sale proceeds — with scheduled
/// amortisation excluded and passed through as paydown. Before this window existed there were
/// only two ways to model that, and both are wrong in a way nothing reports: stopping at the
/// reinvestment end understates liability interest and WAL, while extending the single window
/// overstates the pool because it reinvests scheduled principal too.
///
/// The base pool here amortises scheduled principal ONLY, so "scheduled is excluded after the
/// period end" has an unambiguous signature: reinvestment stops dead at the boundary. A pool
/// with both kinds of principal could not distinguish "the narrow set applied" from "the window
/// ended".
/// </summary>
public class PostReinvestmentWindowTests
{
    private static readonly DateTime FirstProj = new(2026, 6, 1);
    private const double Start = 1_000_000.0;
    private const double AmortPerPeriod = 10_000.0;
    private const int BasePeriods = 60;
    private const int WindowMonths = 24;

    /// <summary>A pool amortising a flat 10,000/period of SCHEDULED principal, nothing else.</summary>
    private static List<PeriodCashflows> ScheduledOnlyPool()
    {
        var pool = new List<PeriodCashflows>();
        for (var p = 0; p < BasePeriods; p++)
        {
            var begin = Start - AmortPerPeriod * p;
            pool.Add(new PeriodCashflows
            {
                CashflowDate = FirstProj.AddMonths(p),
                GroupNum = "1",
                BeginBalance = begin,
                Balance = begin - AmortPerPeriod,
                ScheduledPrincipal = AmortPerPeriod,
                UnscheduledPrincipal = 0,
                Interest = begin * 0.05 / 12.0
            });
        }

        return pool;
    }

    /// <summary>The same pool, but every dollar of principal is UNSCHEDULED.</summary>
    private static List<PeriodCashflows> UnscheduledOnlyPool()
    {
        var pool = ScheduledOnlyPool();
        foreach (var cf in pool)
        {
            cf.UnscheduledPrincipal = cf.ScheduledPrincipal;
            cf.ScheduledPrincipal = 0;
        }

        return pool;
    }

    private static IAssetAssumptions ZeroAssumps()
    {
        var anchor = DateUtil.CalcAbsT(FirstProj);
        return new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchor, 0.0),
            DefaultTypeEnum.CDR, new ConstVector(anchor, 0.0),
            new ConstVector(anchor, 0.0));
    }

    private static ReinvestmentConfig Config(int? postMonths = null) => new()
    {
        ReinvestStartDate = FirstProj,
        ReinvestEndDate = FirstProj.AddMonths(WindowMonths),
        PostReinvestmentEndDate = postMonths is { } m ? FirstProj.AddMonths(m) : null,
        Target = Start,
        EligibleProceeds = EligibleProceeds.ScheduledPrincipal | EligibleProceeds.Prepayments,
        Templates = new[]
        {
            new ReinvestTemplate
            {
                AllocationPct = 100,
                IsSynthetic = true,
                AmortizationType = AmortizationType.Bullet,
                CouponRate = 5.0,
                TermMonths = 60
            }
        }
    };

    /// <summary>Combined pool balance at a projection period — the idiom the loop tests use.</summary>
    private static double CombinedBalanceAt(
        IEnumerable<PeriodCashflows> basePool, IEnumerable<PeriodCashflows> reinvest, int period)
    {
        var date = FirstProj.AddMonths(period);
        return basePool.Where(c => c.CashflowDate == date).Sum(c => c.Balance)
             + reinvest.Where(c => c.CashflowDate == date).Sum(c => c.Balance);
    }

    // ---------------------------------------------------------------- the window itself

    // Measured on COMBINED pool balance against the target — the idiom the sibling loop tests
    // use: while reinvestment is live the pool holds at target; once it stops the pool amortises
    // away. A probe period well past the boundary separates the two cleanly.
    private const int Probe = WindowMonths + 6;

    [Fact]
    public void WithNoPostWindow_ReinvestmentStopsAtTheReinvestmentEnd()
    {
        // The pre-existing behaviour, pinned so adding the window cannot change the default.
        var basePool = UnscheduledOnlyPool();
        var cfg = Config();
        var reinvest = CfCore.BuildReinvestmentCashflows(
            basePool, cfg, FirstProj, ZeroAssumps(), rateProvider: null);

        cfg.EffectiveEndDate.Should().Be(cfg.ReinvestEndDate);
        CombinedBalanceAt(basePool, reinvest, Probe).Should().BeLessThan(Start - 1.0,
            "with no post window the pool amortises once the reinvestment period ends");
    }

    [Fact]
    public void WithAPostWindow_UnscheduledProceedsKeepBuyingPastTheReinvestmentEnd()
    {
        var basePool = UnscheduledOnlyPool();
        var reinvest = CfCore.BuildReinvestmentCashflows(
            basePool, Config(postMonths: WindowMonths + 12), FirstProj, ZeroAssumps(),
            rateProvider: null);

        CombinedBalanceAt(basePool, reinvest, Probe).Should().BeApproximately(Start, 1.0,
            "unscheduled principal is eligible in the post window, so the pool holds at target");
    }

    [Fact]
    public void ScheduledPrincipalIsNotReinvestedAfterTheReinvestmentEnd()
    {
        // The whole point of the second window. Scheduled-only pool: the pool must amortise past
        // the boundary even though the post window is open for another year — while still having
        // been sustained DURING the reinvestment period.
        var basePool = ScheduledOnlyPool();
        var reinvest = CfCore.BuildReinvestmentCashflows(
            basePool, Config(postMonths: WindowMonths + 12), FirstProj, ZeroAssumps(),
            rateProvider: null);

        CombinedBalanceAt(basePool, reinvest, WindowMonths).Should().BeApproximately(Start, 1.0,
            "scheduled principal IS eligible during the reinvestment period");
        CombinedBalanceAt(basePool, reinvest, Probe).Should().BeLessThan(Start - 1.0,
            "scheduled amortisation pays down after the period, it is not reinvested");
    }

    [Fact]
    public void OptingScheduledIn_ReinvestsItAfterTheEndToo()
    {
        // The narrow default is a default, not a rule — a deal whose indenture permits scheduled
        // proceeds post-period can say so, and then the pool holds.
        var basePool = ScheduledOnlyPool();
        var cfg = Config(postMonths: WindowMonths + 12) with
        {
            PostReinvestmentEligibleProceeds =
                EligibleProceeds.ScheduledPrincipal | EligibleProceeds.Prepayments
        };
        var reinvest = CfCore.BuildReinvestmentCashflows(
            basePool, cfg, FirstProj, ZeroAssumps(), rateProvider: null);

        CombinedBalanceAt(basePool, reinvest, Probe).Should().BeApproximately(Start, 1.0);
    }

    [Fact]
    public void ReinvestmentStopsAtThePostWindowEnd()
    {
        // The second window ends too. Without this the effective-end bound could run to the
        // horizon and the deal would revolve forever.
        var basePool = UnscheduledOnlyPool();
        var reinvest = CfCore.BuildReinvestmentCashflows(
            basePool, Config(postMonths: WindowMonths + 6), FirstProj, ZeroAssumps(),
            rateProvider: null);

        CombinedBalanceAt(basePool, reinvest, WindowMonths + 6).Should()
            .BeApproximately(Start, 1.0, "still inside the post window");
        CombinedBalanceAt(basePool, reinvest, WindowMonths + 18).Should()
            .BeLessThan(Start - 1.0, "past the post window the pool amortises");
    }

    // ---------------------------------------------------------------- config surface

    [Fact]
    public void APostEndBeforeTheReinvestmentEndIsRefused()
    {
        var cfg = Config() with { PostReinvestmentEndDate = FirstProj.AddMonths(WindowMonths - 1) };

        var act = () => cfg.Validate("TEST");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*postReinvestmentEndDate must be on or after reinvestEndDate*");
    }

    [Fact]
    public void EffectiveEndIgnoresAPostEndThatDoesNotExtend()
    {
        // Equal dates are legal but add no window; the effective end must not move, or the loop
        // runs a zero-length second window under narrower eligibility.
        var cfg = Config() with { PostReinvestmentEndDate = FirstProj.AddMonths(WindowMonths) };

        cfg.EffectiveEndDate.Should().Be(cfg.ReinvestEndDate);
    }

    [Fact]
    public void EligibilitySwitchesAtTheBoundaryNotBefore()
    {
        var cfg = Config(postMonths: WindowMonths + 12);

        cfg.EligibleOn(cfg.ReinvestEndDate).Should().Be(cfg.EligibleProceeds,
            "the reinvestment end date is still INSIDE the main window");
        cfg.EligibleOn(cfg.ReinvestEndDate.AddDays(1)).Should()
            .Be(cfg.PostReinvestmentEligibleProceeds);
    }

    [Fact]
    public void ThePostWindowDefaultsToUnscheduledOnly()
    {
        // Pins the default set itself: scheduled excluded, prepayments and recoveries in.
        var cfg = new ReinvestmentConfig { ReinvestEndDate = FirstProj };

        cfg.PostReinvestmentEligibleProceeds.Should()
            .Be(EligibleProceeds.Prepayments | EligibleProceeds.Recoveries);
    }

    [Fact]
    public void TheHorizonCoversCollateralBoughtInThePostWindow()
    {
        // The projection horizon is sized off the LAST date reinvestment can occur. Size it off
        // the reinvestment end instead and a cohort bought in the post window has its tail
        // truncated: the cash was redirected out of distributable principal, but the collateral
        // it bought stops existing partway through its life.
        //
        // Probed DEEP on purpose. A 60-month bullet bought at period 36 matures at 96, while a
        // horizon sized off the reinvestment end (24) stops at 86 — so only a probe beyond 86
        // can tell the two apart. Every other test here sits near the window boundary and is
        // blind to this; sizing the horizon wrongly survived all of them.
        var basePool = UnscheduledOnlyPool();
        var reinvest = CfCore.BuildReinvestmentCashflows(
            basePool, Config(postMonths: WindowMonths + 12), FirstProj, ZeroAssumps(),
            rateProvider: null);

        CombinedBalanceAt(basePool, reinvest, 90).Should().BeGreaterThan(0.0,
            "collateral bought in the post window must live out its full 60-month term");
    }
}
