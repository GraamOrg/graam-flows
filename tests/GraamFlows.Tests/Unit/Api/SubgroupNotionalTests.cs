using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     A notional class whose notional follows one subgroup's collateral ("50% of the Subgroup X
///     Trust Assets"): payFrom "Notional", notionalReference "SUBGROUP_X". Its notional falls as
///     subgroup X's balance does, whatever the other subgroup does. The collateral is stated by
///     subgroup (SUBGROUP_SPLIT's rows). These tests drive the DTO -> controller -> engine path.
/// </summary>
public class SubgroupNotionalTests
{
    private static readonly DateTime FirstPayDate = new(2026, 10, 20);
    private const double X = 40_000_000, Y = 60_000_000;

    private static TrancheDto Class(string name, double balance, int order, bool io = false, string? notional = null) => new()
    {
        TrancheName = name, OriginalBalance = balance, TrancheType = "Offered", CashflowType = io ? "IO" : "PI",
        CouponType = "Fixed", FixedCoupon = io ? 1.0 : 4.0, SubordinationOrder = order, FirstPayDate = FirstPayDate,
        PayFrequency = 12, PayDay = FirstPayDate.Day, DayCount = "30/360",
        PayFrom = io ? "Notional" : null, NotionalReference = notional
    };

    private static WaterfallRequest Request(string notional = "SUBGROUP_X", int? xPaysOffAt = null) => new()
    {
        ProjectionDate = FirstPayDate.AddMonths(-1),
        CollateralCashflows = Collateral(xPaysOffAt),
        Deal = new DealDto
        {
            DealName = "SUBGROUP_NOTIONAL_TEST",
            WaterfallType = "ComposableStructure",
            InterestTreatment = "Guaranteed",
            Tranches = new List<TrancheDto>
                { Class("A", X + Y, 0), Class("SX", 0.5 * X, 1, io: true, notional: notional) },
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = new List<string>
                    { "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN" },
                Steps = new List<WaterfallStepDto>
                {
                    new() { Type = "INTEREST", Structure = new PayableStructureDto { Type = "SEQ", Tranches = new List<string> { "A", "SX" } } },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = new PayableStructureDto { Type = "SINGLE", Tranche = "A" } },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = new PayableStructureDto { Type = "SINGLE", Tranche = "A" } },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = new PayableStructureDto { Type = "SINGLE", Tranche = "A" } },
                    new() { Type = "WRITEDOWN", Structure = new PayableStructureDto { Type = "SEQ", Tranches = new List<string> { "A" } } }
                }
            }
        }
    };

    /// <summary>X: 1%/month scheduled + 6% prepaid; Y: 1% + 1%. X optionally pays off in full.</summary>
    private static List<PeriodCashflowDto> Collateral(int? xPaysOffAt)
    {
        var periods = new List<PeriodCashflowDto>();
        double x = X, y = Y;
        for (var i = 0; i < 360 && x + y > 1.0; i++)
        {
            var date = FirstPayDate.AddMonths(i);
            PeriodCashflowDto Row(string sub, double begin, double sched, double prin) => new()
            {
                Period = i + 1, CashflowDate = date, GroupNum = "1", Subgroup = sub, BeginBalance = begin,
                Balance = begin - prin, ScheduledPrincipal = sched, UnscheduledPrincipal = prin - sched,
                Interest = begin * 5.0 / 1200, NetInterest = begin * 5.0 / 1200, Wac = 5.0
            };

            var last = i == 359;
            if (x > 0.005)
            {
                var payoff = last || i + 1 == xPaysOffAt;
                var xs = payoff ? x : x * 0.01;
                var xp = payoff ? x : x * 0.07;
                periods.Add(Row("X", x, xs, xp));
                x -= xp;
            }

            var ys = last ? y : y * 0.01;
            var yp = last ? y : y * 0.02;
            periods.Add(Row("Y", y, ys, yp));
            y -= yp;
        }

        return periods;
    }

    private static ActionResult<WaterfallResponse> Run(WaterfallRequest request) =>
        new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(request);

    private static WaterfallResponse Ok(WaterfallRequest request)
    {
        var ok = Run(request).Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<WaterfallResponse>().Subject;
    }

    private static string Refusal(WaterfallRequest request)
    {
        var bad = Run(request).Result.Should().BeAssignableTo<ObjectResult>().Subject;
        bad.StatusCode.Should().BeGreaterThanOrEqualTo(400);
        return bad.Value!.ToString()!;
    }

    [Fact]
    public void The_notional_falls_as_its_subgroups_balance_does()
    {
        var request = Request();
        var cf = Ok(request).TrancheCashflows["SX"];
        var xRows = request.CollateralCashflows.Where(r => r.Subgroup == "X").OrderBy(r => r.Period).ToList();
        for (var i = 0; i < 60; i++)
            cf[i].Balance.Should().BeApproximately(0.5 * xRows[i].Balance, 0.01, $"period {i + 1}");
    }

    [Fact]
    public void A_paid_off_subgroups_notional_is_zero_from_then_on()
    {
        var cf = Ok(Request(xPaysOffAt: 24)).TrancheCashflows["SX"];
        cf[23].Balance.Should().BeApproximately(0, 0.01);
        cf.Skip(24).Take(12).Should().OnlyContain(r => Math.Abs(r.Balance) < 0.01);
    }

    [Fact]
    public void A_subgroup_the_collateral_does_not_state_is_refused()
    {
        Refusal(Request("SUBGROUP_Z")).Should().Contain("follows subgroup Z, which the collateral does not state");
    }

    [Fact]
    public void A_subgroup_reference_mixed_with_classes_is_refused()
    {
        Refusal(Request("SUBGROUP_X,A")).Should().Contain("State \"SUBGROUP_X\" alone");
    }
}
