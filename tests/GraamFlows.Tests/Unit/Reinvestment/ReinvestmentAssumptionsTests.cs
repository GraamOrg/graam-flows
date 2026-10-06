using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Reinvestment;

/// <summary>
/// Collateral the reinvestment loop BUYS is projected on the run's own assumptions
/// (graam-harmony#5577).
///
/// /api/Waterfall takes collateral cashflows that are already projected, so it never needed
/// prepayment or default assumptions for the posted pool, and built
/// `CreateConstAssumptions(..., 0, 0, 0)` purely to carry trigger forecasts and the settle
/// date. When graam-flows#62 added reinvestment over HTTP it handed that same ZEROED object
/// to the loop, so every bought cohort ran at CPR 0 / CDR 0 / severity 0 — a pool that never
/// prepays and never defaults, inside a run whose posted pool did both.
///
/// Measured on a live CLO before the fix: 71 consecutive periods after the reinvestment
/// window closed with ZERO prepayments, on a pool carrying a 20 CPR.
/// </summary>
public class ReinvestmentAssumptionsTests
{
    private static readonly DateTime Proj = new(2026, 6, 1);

    private static WaterfallRequest Request(AssumptionsDto? assumptions)
    {
        var deal = new DealDto
        {
            DealName = "REINVEST_ASSUMPTIONS_TEST",
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
            },
            Reinvestment = new ReinvestmentDto
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
            ProjectionDate = Proj,
            Assumptions = assumptions,
            IncludeCollateralCashflows = true
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

    private static double BoughtPrepayments(WaterfallResponse r) =>
        r.CollateralCashflows!.Sum(c => c.UnscheduledPrincipal);

    private static double BoughtDefaults(WaterfallResponse r) =>
        r.CollateralCashflows!.Sum(c => c.DefaultedPrincipal);

    // --- the defect itself -------------------------------------------------------------

    [Fact]
    public void Bought_collateral_prepays_when_the_request_supplies_a_cpr()
    {
        // The posted pool carries NO unscheduled principal, so every prepayment in the
        // distributed stream can only have come from collateral the loop bought.
        var withCpr = Run(Request(new AssumptionsDto { Cpr = 20, Cdr = 0, Severity = 0 }));

        BoughtPrepayments(withCpr).Should().BeGreaterThan(0,
            "a bought cohort carrying a 20 CPR must prepay");
    }

    [Fact]
    public void Bought_collateral_defaults_when_the_request_supplies_a_cdr()
    {
        var withCdr = Run(Request(new AssumptionsDto { Cpr = 0, Cdr = 5, Severity = 30 }));

        BoughtDefaults(withCdr).Should().BeGreaterThan(0,
            "a bought cohort carrying a 5 CDR must default");
    }

    [Fact]
    public void Supplying_assumptions_changes_the_bought_cohorts()
    {
        var zeroed = Run(Request(null));
        var supplied = Run(Request(new AssumptionsDto { Cpr = 20, Cdr = 0, Severity = 0 }));

        BoughtPrepayments(zeroed).Should().Be(0, "this is the pre-fix behaviour, pinned");
        BoughtPrepayments(supplied).Should().BeGreaterThan(BoughtPrepayments(zeroed));
    }

    // --- the default stays byte-compatible, and says so --------------------------------

    [Fact]
    public void A_request_without_assumptions_keeps_the_previous_behaviour()
    {
        var zeroed = Run(Request(null));

        BoughtPrepayments(zeroed).Should().Be(0);
        BoughtDefaults(zeroed).Should().Be(0);
    }

    [Fact]
    public void The_response_says_which_basis_the_bought_cohorts_ran_on()
    {
        var zeroed = Run(Request(null));
        var supplied = Run(Request(new AssumptionsDto { Cpr = 20, Cdr = 0, Severity = 0 }));

        zeroed.ReinvestmentAssumptionResolution!.Source.Should().Be("zeroed");
        zeroed.ReinvestmentAssumptionResolution.Detail.Should().Contain("CPR 0");
        supplied.ReinvestmentAssumptionResolution!.Source.Should().Be("supplied");
    }

    [Fact]
    public void A_run_that_does_not_reinvest_discloses_nothing()
    {
        var request = Request(new AssumptionsDto { Cpr = 20 });
        request.Deal.Reinvestment = null;

        Run(request).ReinvestmentAssumptionResolution.Should().BeNull(
            "there is no bought collateral to disclose a basis for");
    }
}
