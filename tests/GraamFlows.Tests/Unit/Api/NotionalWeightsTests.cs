using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     A notional class that follows its classes at different shares of each (<c>notionalWeights</c>):
///     a REMIC's "100% of A and 50% of B", which one proportion over the classes it names cannot
///     carry. The notional falls by the sum of each class's share times its principal, and is
///     written down the same way. Without weights, a notional is paid exactly as before.
/// </summary>
public class NotionalWeightsTests
{
    private static readonly DateTime FirstPayDate = new(2026, 2, 25);
    private const double ABalance = 60_000_000;
    private const double BBalance = 40_000_000;
    private const double PoolBalance = ABalance + BBalance;

    private static WaterfallRequest Request(
        string reference = "A,B", Dictionary<string, double>? weights = null, double? notional = null,
        bool losses = false) => new()
    {
        ProjectionDate = FirstPayDate.AddMonths(-1),
        CollateralCashflows = Collateral(losses),
        Deal = new DealDto
        {
            DealName = "NOTIONAL_WEIGHTS_TEST",
            WaterfallType = "ComposableStructure",
            ClosingDate = FirstPayDate.AddMonths(-1),
            Tranches = new List<TrancheDto>
            {
                Bond("A", ABalance, 0),
                Bond("B", BBalance, 1),
                new()
                {
                    TrancheName = "X",
                    OriginalBalance = notional ?? ABalance + 0.5 * BBalance,
                    TrancheType = "Offered",
                    CashflowType = "IO",
                    CouponType = "Fixed",
                    FixedCoupon = 1.0,
                    ClassReference = "X",
                    NotionalReference = reference,
                    NotionalWeights = weights,
                    SubordinationOrder = 50,
                    FirstPayDate = FirstPayDate,
                    PayFrequency = 12,
                    PayDay = FirstPayDate.Day
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
                    new() { Type = "INTEREST", Structure = Seq("A", "X", "B") },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A", "B") },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A", "B") },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A", "B") },
                    new() { Type = "WRITEDOWN", Structure = Seq("B", "A") }
                }
            }
        }
    };

    private static TrancheDto Bond(string name, double balance, int subOrder) => new()
    {
        TrancheName = name,
        OriginalBalance = balance,
        TrancheType = "Offered",
        CashflowType = "PI",
        CouponType = "Fixed",
        FixedCoupon = 5.0,
        SubordinationOrder = subOrder,
        FirstPayDate = FirstPayDate,
        PayFrequency = 12,
        PayDay = FirstPayDate.Day
    };

    private static PayableStructureDto Seq(params string[] tranches) => new() { Type = "SEQ", Tranches = tranches.ToList() };

    private static List<PeriodCashflowDto> Collateral(bool losses)
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        const double wac = 6.0;
        for (var i = 0; i < 36 && balance > 1.0; i++)
        {
            var interest = balance * wac / 100 / 12;
            var scheduled = i == 35 ? balance : balance * 0.04;
            var unscheduled = i == 35 ? 0.0 : balance * 0.02;
            var loss = losses && i < 35 ? balance * 0.005 : 0.0;
            var end = balance - scheduled - unscheduled - loss;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1,
                CashflowDate = FirstPayDate.AddMonths(i),
                GroupNum = "1",
                BeginBalance = balance,
                Balance = end,
                ScheduledPrincipal = scheduled,
                UnscheduledPrincipal = unscheduled,
                DefaultedPrincipal = loss,
                CollateralLoss = loss,
                Interest = interest,
                NetInterest = interest,
                Wac = wac
            });
            balance = end;
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

    private static readonly Dictionary<string, double> FullAndHalf = new() { ["A"] = 1.0, ["B"] = 0.5 };

    [Fact]
    public void A_weighted_notional_falls_by_each_class_share_of_its_principal()
    {
        var cf = Ok(Request(weights: FullAndHalf)).TrancheCashflows;
        for (var i = 0; i < cf["X"].Count; i++)
            cf["X"][i].Balance.Should().BeApproximately(cf["A"][i].Balance + 0.5 * cf["B"][i].Balance, 0.01,
                $"period {i + 1}");
        cf["X"].Sum(r => r.ScheduledPrincipal + r.UnscheduledPrincipal).Should().BeApproximately(0, 0.01,
            "a notional class is paid no principal");
    }

    [Fact]
    public void A_weighted_notional_is_written_down_by_each_class_share_of_its_writedown()
    {
        var cf = Ok(Request(weights: FullAndHalf, losses: true)).TrancheCashflows;
        cf["B"].Sum(r => r.Writedown).Should().BeGreaterThan(0, "the losses reach B");
        for (var i = 0; i < cf["X"].Count; i++)
            cf["X"][i].Balance.Should().BeApproximately(cf["A"][i].Balance + 0.5 * cf["B"][i].Balance, 0.01,
                $"period {i + 1}");
        // A notional class's writedown is taken in its balance (its Writedown column reads 0, with
        // weights or without): A + 50% of B, B's writedowns included, is the notional each period.
        var noLoss = Ok(Request(weights: FullAndHalf)).TrancheCashflows["X"];
        cf["X"].Last(r => r.Balance > 0.01).Balance.Should().BeLessThan(
            noLoss[cf["X"].FindLastIndex(r => r.Balance > 0.01)].Balance, "the losses reach the notional");
    }

    [Fact]
    public void Without_weights_a_notional_follows_its_classes_at_one_share_as_before()
    {
        // One share for both: its original over theirs (the engine's proportion), exactly.
        var cf = Ok(Request(notional: 50_000_000)).TrancheCashflows;
        const double share = 50_000_000 / PoolBalance;
        for (var i = 0; i < cf["X"].Count; i++)
            cf["X"][i].Balance.Should().BeApproximately(share * (cf["A"][i].Balance + cf["B"][i].Balance), 0.01,
                $"period {i + 1}");
        // The same request with weights of 1 on each class: the same notional, since its original
        // is then the classes' sum.
        var plain = Ok(Request(notional: PoolBalance)).TrancheCashflows["X"];
        var ones = Ok(Request(notional: PoolBalance, weights: new Dictionary<string, double> { ["A"] = 1, ["B"] = 1 }))
            .TrancheCashflows["X"];
        ones.Select(r => r.Balance).Should().Equal(plain.Select(r => r.Balance));
    }

    [Fact]
    public void A_class_followed_with_no_weight_is_refused_never_taken_as_one()
    {
        Refusal(Request(weights: new Dictionary<string, double> { ["A"] = 1.0 }))
            .Should().Contain("with none for B").And.Contain("none is taken as 1");
    }

    [Fact]
    public void A_weight_on_a_class_not_followed_is_refused()
    {
        Refusal(Request("A", new Dictionary<string, double> { ["A"] = 1.0, ["B"] = 0.5 }, ABalance))
            .Should().Contain("for B, which its notionalReference ('A') does not name");
    }

    [Fact]
    public void A_weight_on_a_collateral_reference_is_refused()
    {
        Refusal(Request("GROUP_1", new Dictionary<string, double> { ["GROUP_1"] = 0.5 }, PoolBalance * 0.5))
            .Should().Contain("on 'GROUP_1', a collateral reference");
    }

    [Fact]
    public void A_weight_that_is_not_a_positive_number_is_refused()
    {
        Refusal(Request(weights: new Dictionary<string, double> { ["A"] = 1.0, ["B"] = 0.0 }))
            .Should().Contain("a share is a positive number");
    }
}
