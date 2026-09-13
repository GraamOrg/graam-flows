using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Assumptions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Reinvestment;

/// <summary>
/// Reporting the reinvestment loop.
///
/// There is no separate reinvested-collateral stream: bought collateral joins the pool, so its
/// later cashflows are ordinary collateral cashflows, and collateral principal is reported AFTER
/// purchases. The act of buying is reported on its own, as one purchase record per period.
///
/// Before this, /api/Waterfall merged the bought collateral into the pool, distributed it, and
/// returned only tranche cashflows — the stream it distributed and the cash it spent buying were
/// both discarded, so a caller could not reconcile a reinvesting run to its collateral at all.
/// </summary>
public class ReinvestmentReportingTests
{
    private static readonly DateTime Proj = new(2026, 6, 1);
    private const double Start = 1_000_000.0;
    private const double Amort = 10_000.0;
    private const int Periods = 60;
    private const int WindowMonths = 24;

    // --- the loop itself ------------------------------------------------------------------------

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

    private static ReinvestmentConfig Config(double price = 100.0, double holdback = 0.0) => new()
    {
        ReinvestStartDate = Proj,
        ReinvestEndDate = Proj.AddMonths(WindowMonths),
        Target = Start,
        Holdback = holdback,
        Templates = new[]
        {
            new ReinvestTemplate
            {
                AllocationPct = 60, Price = price, AmortizationType = AmortizationType.Bullet,
                CouponRate = 5.0, TermMonths = 36
            },
            new ReinvestTemplate
            {
                AllocationPct = 40, Price = price, AmortizationType = AmortizationType.Bullet,
                CouponRate = 6.0, TermMonths = 36
            }
        }
    };

    [Fact]
    public void The_existing_cashflow_builder_returns_exactly_what_it_did()
    {
        var pool = BasePool();
        var legacy = CfCore.BuildReinvestmentCashflows(pool, Config(), Proj, ZeroAssumps(), null);
        var result = CfCore.BuildReinvestment(pool, Config(), Proj, ZeroAssumps(), null);

        result.Cashflows.Should().HaveCount(legacy.Count);
        for (var i = 0; i < legacy.Count; i++)
        {
            result.Cashflows[i].CashflowDate.Should().Be(legacy[i].CashflowDate);
            result.Cashflows[i].Balance.Should().Be(legacy[i].Balance);
            result.Cashflows[i].ScheduledPrincipal.Should().Be(legacy[i].ScheduledPrincipal);
            result.Cashflows[i].Interest.Should().Be(legacy[i].Interest);
        }
    }

    [Fact]
    public void Every_purchase_is_paid_for_by_the_principal_it_redirects()
    {
        var result = CfCore.BuildReinvestment(BasePool(), Config(holdback: 0.25), Proj, ZeroAssumps(), null);

        result.Purchases.Should().NotBeEmpty();
        foreach (var p in result.Purchases)
        {
            (p.FromScheduledPrincipal + p.FromUnscheduledPrincipal + p.FromRecoveryPrincipal)
                .Should().BeApproximately(p.CashSpent, 1e-6);
            p.CashSpent.Should().BeLessThanOrEqualTo(p.ProceedsAvailable + 1e-6,
                "a purchase can never spend more than the proceeds available after holdback");
            p.ByTemplate.Sum(t => t.CashSpent).Should().BeApproximately(p.CashSpent, 1e-6);
            p.ByTemplate.Select(t => t.TemplateIndex).Should().Equal(0, 1);
            p.ByTemplate[0].CashSpent.Should().BeApproximately(0.6 * p.CashSpent, 1e-6);
        }
    }

    [Fact]
    public void Face_bought_is_cash_over_the_purchase_price()
    {
        var result = CfCore.BuildReinvestment(BasePool(), Config(price: 98.0), Proj, ZeroAssumps(), null);

        foreach (var p in result.Purchases)
        {
            foreach (var t in p.ByTemplate)
            {
                t.Price.Should().Be(98.0);
                t.FaceBought.Should().BeApproximately(t.CashSpent / 0.98, 1e-6);
            }

            p.FaceBought.Should().BeApproximately(p.ByTemplate.Sum(t => t.FaceBought), 1e-6);
            p.FaceBought.Should().BeGreaterThan(p.CashSpent);
        }
    }

    [Fact]
    public void Purchases_top_the_pool_back_to_target_inside_the_window_only()
    {
        var result = CfCore.BuildReinvestment(BasePool(), Config(), Proj, ZeroAssumps(), null);

        result.Purchases.Should().NotBeEmpty();
        foreach (var p in result.Purchases)
        {
            p.CashflowDate.Should().BeOnOrAfter(Proj).And.BeOnOrBefore(Proj.AddMonths(WindowMonths));
            p.TargetBalance.Should().Be(Start);
            // At par with proceeds covering the gap, the purchase closes it exactly.
            (p.PoolBalanceBefore + p.FaceBought).Should().BeApproximately(Start, 1e-6);
        }
    }

    // --- the endpoint -----------------------------------------------------------------------------

    private static WaterfallRequest Request(bool reinvest, bool include, double price = 100.0)
    {
        var deal = new DealDto
        {
            DealName = "REINVEST_REPORTING_TEST",
            WaterfallType = "Sequential",
            Tranches = new List<TrancheDto>
            {
                new()
                {
                    TrancheName = "A", OriginalBalance = Start, TrancheType = "Offered",
                    CashflowType = "PI", CouponType = "Fixed", FixedCoupon = 4.0,
                    SubordinationOrder = 1, FirstPayDate = Proj.AddMonths(1)
                }
            },
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = new List<string>
                {
                    "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN"
                },
                Steps = new List<WaterfallStepDto>
                {
                    new() { Type = "INTEREST", Structure = Seq("A") },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A") },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A") },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A") },
                    new() { Type = "WRITEDOWN", Structure = Seq("A") }
                }
            }
        };
        if (reinvest)
            deal.Reinvestment = new ReinvestmentDto
            {
                ReinvestStartDate = Proj,
                ReinvestEndDate = Proj.AddMonths(WindowMonths),
                Target = Start,
                Templates = new List<ReinvestTemplateDto>
                {
                    new()
                    {
                        AllocationPct = 100, Price = price, AmortizationType = AmortizationType.Bullet,
                        InterestRateType = InterestRateType.FRM, CouponRate = 5.0, TermMonths = 24
                    }
                }
            };

        var pool = new List<PeriodCashflowDto>();
        for (var p = 0; p < Periods; p++)
        {
            var begin = Start - Amort * p;
            pool.Add(new PeriodCashflowDto
            {
                CashflowDate = Proj.AddMonths(p), GroupNum = "1", BeginBalance = begin,
                Balance = begin - Amort, ScheduledPrincipal = Amort, Interest = begin * 0.05 / 12.0
            });
        }

        return new WaterfallRequest
        {
            Deal = deal, CollateralCashflows = pool, ProjectionDate = Proj, IncludeCollateralCashflows = include
        };
    }

    private static PayableStructureDto Seq(params string[] tranches) => new() { Type = "SEQ", Tranches = tranches.ToList() };

    private static WaterfallResponse Run(WaterfallRequest request)
    {
        var action = new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(request);
        if (action.Result is BadRequestObjectResult bad)
            throw new Xunit.Sdk.XunitException($"Waterfall 400: {bad.Value}");
        return action.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<WaterfallResponse>().Subject;
    }

    private static double Principal(PeriodCashflowDto c) =>
        c.ScheduledPrincipal + c.UnscheduledPrincipal + c.RecoveryPrincipal;

    [Fact]
    public void Collateral_is_not_returned_unless_asked_for()
    {
        var response = Run(Request(reinvest: true, include: false));

        response.CollateralCashflows.Should().BeNull();
        response.AssetReinvestment.Should().BeNull();
    }

    [Fact]
    public void Without_reinvestment_the_returned_collateral_is_the_posted_pool()
    {
        var request = Request(reinvest: false, include: true);
        var response = Run(request);

        response.AssetReinvestment.Should().BeEmpty();
        response.CollateralCashflows.Should().HaveCount(Periods);
        for (var i = 0; i < Periods; i++)
        {
            var got = response.CollateralCashflows![i];
            var posted = request.CollateralCashflows[i];
            got.CashflowDate.Should().Be(posted.CashflowDate);
            got.Balance.Should().Be(posted.Balance);
            got.ScheduledPrincipal.Should().Be(posted.ScheduledPrincipal);
            got.Interest.Should().Be(posted.Interest);
        }
    }

    [Fact]
    public void Returned_collateral_principal_is_net_of_each_purchase()
    {
        var request = Request(reinvest: true, include: true);
        var response = Run(request);

        response.AssetReinvestment.Should().NotBeEmpty();
        var maturitiesReinvested = 0;
        foreach (var purchase in response.AssetReinvestment!)
        {
            var posted = request.CollateralCashflows.Single(c => c.CashflowDate == purchase.CashflowDate);
            var returned = response.CollateralCashflows!.Where(c => c.CashflowDate == purchase.CashflowDate)
                .Sum(Principal);
            // Every dollar of eligible principal collected this period — the posted pool's AND any
            // bought collateral maturing — is either spent buying or left in the returned stream.
            // (No holdback and no ineligible principal in this deal.)
            returned.Should().BeApproximately(purchase.ProceedsAvailable - purchase.CashSpent, 1e-6);
            if (purchase.ProceedsAvailable > Principal(posted) + 1e-6)
                maturitiesReinvested++;
        }

        maturitiesReinvested.Should().BeGreaterThan(0,
            "a 24-month bullet bought at the start matures inside the window and is reinvested too — "
            + "the case a posted-pool-only identity gets wrong");
    }

    [Fact]
    public void Inside_the_window_the_returned_pool_is_held_at_target()
    {
        var response = Run(Request(reinvest: true, include: true));

        foreach (var purchase in response.AssetReinvestment!)
        {
            var balance = response.CollateralCashflows!.Where(c => c.CashflowDate == purchase.CashflowDate)
                .Sum(c => c.Balance);
            balance.Should().BeApproximately(purchase.TargetBalance, 1e-6);
        }
    }

    [Fact]
    public void The_notes_receive_exactly_the_principal_the_returned_collateral_carries()
    {
        var response = Run(Request(reinvest: true, include: true));

        var collateral = response.CollateralCashflows!.Sum(Principal);
        var notes = response.TrancheCashflows.Values.SelectMany(c => c)
            .Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal);
        notes.Should().BeApproximately(collateral, 1.0,
            "the returned stream is the one the waterfall distributed, so it reconciles to the notes");
    }

    [Fact]
    public void A_discount_purchase_adds_exactly_its_accretion_to_collateral_principal()
    {
        var request = Request(reinvest: true, include: true, price: 97.5);
        var response = Run(request);

        var accretion = response.AssetReinvestment!.Sum(p => p.FaceBought - p.CashSpent);
        accretion.Should().BeGreaterThan(0);
        var returned = response.CollateralCashflows!.Sum(Principal);
        var posted = request.CollateralCashflows.Sum(Principal);
        (returned - posted).Should().BeApproximately(accretion, 1.0,
            "bought below par, the bullets repay more face than the cash that bought them");
    }
}
