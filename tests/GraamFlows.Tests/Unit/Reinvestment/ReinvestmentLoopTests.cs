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
/// Cohort-orchestration reinvestment loop (graam-flows#49). Drives
/// CfCore.BuildReinvestmentCashflows directly with a hand-built amortizing base
/// pool so the mechanics are isolated from the full deal/waterfall path:
/// eligible proceeds buy bullet cohorts up to a plain balance target, the pool
/// balance is sustained during the window, purchases stop at the window end, and
/// reinvested cash is conserved.
/// </summary>
public class ReinvestmentLoopTests
{
    private static readonly DateTime FirstProj = new(2026, 6, 1);
    private const double Start = 1_000_000.0;
    private const double AmortPerPeriod = 10_000.0;
    private const int BasePeriods = 60;

    // A base pool that amortizes a flat 10,000/period of scheduled principal.
    private static List<PeriodCashflows> AmortizingBasePool()
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

    private static IAssetAssumptions ZeroAssumps()
    {
        var anchor = DateUtil.CalcAbsT(FirstProj);
        return new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchor, 0.0),
            DefaultTypeEnum.CDR, new ConstVector(anchor, 0.0),
            new ConstVector(anchor, 0.0));
    }

    private static ReinvestmentConfig KeepFlatConfig(int windowMonths = 47) => new()
    {
        ReinvestStartDate = FirstProj,
        ReinvestEndDate = FirstProj.AddMonths(windowMonths),
        Target = Start,
        Templates = new[]
        {
            new ReinvestTemplate
            {
                AllocationPct = 100,
                IsSynthetic = true,      // priced at par
                AmortizationType = AmortizationType.Bullet,
                CouponRate = 5.0,
                TermMonths = 60
            }
        }
    };

    private static double BalanceAt(IEnumerable<PeriodCashflows> cfs, int period)
    {
        var date = FirstProj.AddMonths(period);
        return cfs.Where(c => c.CashflowDate == date).Sum(c => c.Balance);
    }

    [Fact]
    public void Reinvestment_SustainsPoolBalanceNearTarget_DuringWindow()
    {
        var basePool = AmortizingBasePool();
        var reinvest = CfCore.BuildReinvestmentCashflows(
            basePool, KeepFlatConfig(), FirstProj, ZeroAssumps(), rateProvider: null);

        reinvest.Should().NotBeEmpty("proceeds should buy replacement collateral");

        // Reinvestment lags one period (proceeds at t buy a cohort that appears
        // at t+1), so the combined pool holds at target minus one period of
        // amortization throughout the window.
        for (var p = 1; p <= 47; p++)
        {
            var total = BalanceAt(basePool, p) + BalanceAt(reinvest, p);
            total.Should().BeApproximately(Start - AmortPerPeriod, 1.0,
                $"combined balance at period {p} should hold near the target");
        }
    }

    [Fact]
    public void Reinvestment_ConservesCash_PrincipalReturnedTiesToReinvested()
    {
        var basePool = AmortizingBasePool();
        var cfg = KeepFlatConfig();
        var reinvest = CfCore.BuildReinvestmentCashflows(
            basePool, cfg, FirstProj, ZeroAssumps(), rateProvider: null);

        // Eligible proceeds available to reinvest across the window (scheduled
        // principal, the default eligible set). 48 periods * 10,000.
        var windowProceeds = 0.0;
        for (var p = 0; p <= 47; p++)
            windowProceeds += basePool[p].ScheduledPrincipal;

        // Every dollar reinvested comes back as cohort principal (synthetic par,
        // bullets balloon within the horizon). Principal returned must not exceed
        // the eligible proceeds — cash is conserved.
        var reinvestPrincipal = reinvest.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal);
        reinvestPrincipal.Should().BeApproximately(windowProceeds, 5.0);
        reinvestPrincipal.Should().BeLessThanOrEqualTo(windowProceeds + 0.01);
    }

    [Fact]
    public void Reinvestment_PaysCouponOnReinvestedBalance()
    {
        var basePool = AmortizingBasePool();
        var reinvest = CfCore.BuildReinvestmentCashflows(
            basePool, KeepFlatConfig(), FirstProj, ZeroAssumps(), rateProvider: null);

        // Mid-window the reinvested balance is material, so it earns its 5% coupon.
        var midInterest = reinvest.Where(c => c.CashflowDate == FirstProj.AddMonths(24)).Sum(c => c.Interest);
        midInterest.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Reinvestment_NoPurchasesAfterWindowEnd()
    {
        // Short window: reinvest only for the first 12 months.
        var basePool = AmortizingBasePool();
        var reinvest = CfCore.BuildReinvestmentCashflows(
            basePool, KeepFlatConfig(windowMonths: 11), FirstProj, ZeroAssumps(), rateProvider: null);

        // 12 purchases (t = 0..11) of 10,000 par each = 120,000 of reinvested face.
        // Bullets (term 60) don't repay within the window, so peak reinvested
        // balance tops out around 120,000 and never grows past it after the window.
        var peak = reinvest.Max(c => c.Balance);
        peak.Should().BeApproximately(120_000, 1.0);

        // Well after the window but before the earliest cohort matures (~period
        // 60), the reinvested balance is still the same 120,000 — no new buys.
        BalanceAt(reinvest, 40).Should().BeApproximately(120_000, 1.0);
    }

    [Fact]
    public void Reinvestment_EmptyWindow_ReturnsNothing()
    {
        var basePool = AmortizingBasePool();
        var cfg = KeepFlatConfig() with
        {
            ReinvestStartDate = null,
            ReinvestEndDate = FirstProj.AddMonths(-1) // window closes before the projection starts
        };

        var reinvest = CfCore.BuildReinvestmentCashflows(
            basePool, cfg, FirstProj, ZeroAssumps(), rateProvider: null);
        reinvest.Should().BeEmpty();
    }
}
