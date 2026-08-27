using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Reinvestment;

/// <summary>
/// HTTP wiring for the reinvestment loop (graam-flows#62; found via harmony #4501).
/// BuildDeal mapped + validated deal.reinvestment, but /api/Waterfall never RAN the
/// loop — reinvestment ON vs OFF produced byte-identical waterfalls while an invalid
/// config still 400'd. These pin the controller path itself: the posted base pool
/// gains the reinvested cohorts' cashflows before the waterfall distributes.
/// </summary>
public class ReinvestmentHttpWiringTests
{
    private static readonly DateTime Proj = new(2026, 6, 1);

    private static WaterfallRequest Request(bool withReinvestment)
    {
        // One senior tranche over an amortizing pool; a keep-flat par-target
        // config redeploys principal during a 24-month window.
        var deal = new DealDto
        {
            DealName = "HTTP_REINVEST_TEST",
            WaterfallType = "Sequential",
            Tranches = new List<TrancheDto>
            {
                new()
                {
                    TrancheName = "A", OriginalBalance = 1_000_000, TrancheType = "Offered",
                    CashflowType = "PI", CouponType = "Fixed", FixedCoupon = 4.0,
                    SubordinationOrder = 1, FirstPayDate = Proj.AddMonths(1)
                }
            },
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = new List<string>
                {
                    "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED",
                    "PRINCIPAL_RECOVERY", "WRITEDOWN"
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
        if (withReinvestment)
            deal.Reinvestment = new ReinvestmentDto
            {
                ReinvestStartDate = Proj,
                ReinvestEndDate = Proj.AddMonths(24),
                Target = 1_000_000,
                Templates = new List<ReinvestTemplateDto>
                {
                    new()
                    {
                        AllocationPct = 100,
                        Price = 100.0,
                        AmortizationType = GraamFlows.Objects.TypeEnum.AmortizationType.Bullet,
                        InterestRateType = GraamFlows.Objects.TypeEnum.InterestRateType.FRM,
                        CouponRate = 5.0,
                        TermMonths = 60
                    }
                }
            };

        var pool = new List<PeriodCashflowDto>();
        for (var p = 0; p < 60; p++)
        {
            var begin = 1_000_000.0 - 10_000.0 * p;
            pool.Add(new PeriodCashflowDto
            {
                CashflowDate = Proj.AddMonths(p),
                GroupNum = "1",
                BeginBalance = begin,
                Balance = begin - 10_000.0,
                ScheduledPrincipal = 10_000.0,
                UnscheduledPrincipal = 0,
                Interest = begin * 0.05 / 12.0
            });
        }

        return new WaterfallRequest
        {
            Deal = deal,
            CollateralCashflows = pool,
            ProjectionDate = Proj
        };
    }

    private static PayableStructureDto Seq(params string[] tranches) => new()
    {
        Type = "SEQ",
        Tranches = tranches.ToList()
    };

    private static WaterfallResponse Run(WaterfallRequest request)
    {
        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);
        var action = controller.Execute(request);
        if (action.Result is BadRequestObjectResult bad)
            throw new Xunit.Sdk.XunitException($"Waterfall 400: {bad.Value}");
        var ok = action.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<WaterfallResponse>().Subject;
    }

    [Fact]
    public void Reinvestment_on_changes_the_waterfall_off_the_same_pool()
    {
        var off = Run(Request(withReinvestment: false));
        var on = Run(Request(withReinvestment: true));

        double Interest(WaterfallResponse r) =>
            r.TrancheCashflows.Values.SelectMany(c => c).Sum(c => c.Interest);

        // The reinvested cohorts add collateral, so the deal collects (and the
        // waterfall distributes) MORE interest than the raw amortizing pool.
        Interest(on).Should().BeGreaterThan(Interest(off));
    }

    [Fact]
    public void No_config_path_is_untouched_and_deterministic()
    {
        double Total(WaterfallResponse r) =>
            r.TrancheCashflows.Values.SelectMany(c => c).Sum(c => c.Interest + c.ScheduledPrincipal + c.UnscheduledPrincipal);

        Total(Run(Request(false))).Should().Be(Total(Run(Request(false))));
    }
}
