using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     The step switch had no default case, so a name in `executionOrder` that no step answers
///     to did nothing and the run came back looking fine. A raw "PRINCIPAL" (the step's type,
///     not one of the three names it runs as) paid every note $0 principal. The name is refused
///     instead, by the switch itself, so there is no second list of names to fall out of step.
/// </summary>
public class UnknownExecutionOrderNameTests
{
    private static readonly DateTime Proj = new(2026, 6, 1);
    private const double Pool = 1_000_000;

    private static WaterfallRequest Request(params string[] order)
    {
        var deal = new DealDto
        {
            DealName = "ORDER_NAME_TEST",
            WaterfallType = "Sequential",
            BalanceAtIssuance = Pool,
            Tranches = new List<TrancheDto>
            {
                new()
                {
                    TrancheName = "A", OriginalBalance = Pool, TrancheType = "Offered",
                    CashflowType = "PI", CouponType = "Fixed", FixedCoupon = 4.0,
                    SubordinationOrder = 1, FirstPayDate = Proj.AddMonths(1)
                },
                new()
                {
                    TrancheName = "CERT", OriginalBalance = 0, TrancheType = "Certificate",
                    CashflowType = "PI", CouponType = "None", SubordinationOrder = 2,
                    FirstPayDate = Proj.AddMonths(1)
                }
            },
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = order.ToList(),
                Steps = new List<WaterfallStepDto>
                {
                    new() { Type = "INTEREST", Structure = Seq("A") },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A") },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A") },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A") },
                    new() { Type = "WRITEDOWN", Structure = Seq("A") },
                    new() { Type = "EXCESS_RELEASE", Structure = Seq("CERT") }
                }
            }
        };

        var pool = new List<PeriodCashflowDto>();
        for (var p = 0; p < 100; p++)
        {
            var begin = Pool - 10_000.0 * p;
            pool.Add(new PeriodCashflowDto
            {
                CashflowDate = Proj.AddMonths(p),
                GroupNum = "1",
                BeginBalance = begin,
                Balance = begin - 10_000.0,
                ScheduledPrincipal = 10_000.0,
                UnscheduledPrincipal = 0,
                Interest = begin * 0.05 / 12.0,
                NetInterest = begin * 0.05 / 12.0
            });
        }

        return new WaterfallRequest { Deal = deal, CollateralCashflows = pool, ProjectionDate = Proj };
    }

    private static PayableStructureDto Seq(params string[] tranches) => new()
    {
        Type = "SEQ",
        Tranches = tranches.ToList()
    };

    private static ActionResult<WaterfallResponse> Run(WaterfallRequest request) =>
        new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(request);

    private static string Refusal(WaterfallRequest request)
    {
        var bad = Run(request).Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        return bad.Value!.ToString()!;
    }

    private static List<double> NoteBalances(WaterfallRequest request)
    {
        var ok = Run(request).Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<WaterfallResponse>().Subject;
        return response.TrancheCashflows["A"].Select(c => c.Balance).ToList();
    }

    [Fact]
    public void A_raw_PRINCIPAL_is_refused_and_told_the_names_it_runs_as()
    {
        var refusal = Refusal(Request("INTEREST", "PRINCIPAL", "WRITEDOWN", "EXCESS_RELEASE"));

        refusal.Should().Contain("ORDER_NAME_TEST").And.Contain("'PRINCIPAL'");
        refusal.Should().Contain("PRINCIPAL_SCHEDULED").And.Contain("PRINCIPAL_UNSCHEDULED")
            .And.Contain("PRINCIPAL_RECOVERY");
    }

    [Theory]
    [InlineData("RESERVE")]
    [InlineData("OC_TURBO")]
    [InlineData("INTEREST ")]
    public void Any_other_name_no_step_answers_to_is_refused_by_name(string name)
    {
        Refusal(Request("INTEREST", "PRINCIPAL_SCHEDULED", name, "WRITEDOWN"))
            .Should().Contain($"'{name}'");
    }

    [Fact]
    public void The_names_a_step_answers_to_still_run_in_any_case()
    {
        var balances = NoteBalances(Request("interest", "Principal_Scheduled",
            "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN", "EXCESS_RELEASE"));

        balances[^1].Should().BeApproximately(0, 0.01, "the pool's principal pays the note down");
    }

    [Fact]
    public void A_deal_with_no_order_is_not_refused()
    {
        var request = Request();
        request.Deal.UnifiedWaterfall!.ExecutionOrder = null;

        NoteBalances(request)[^1].Should().BeApproximately(0, 0.01);
    }
}
