using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     A SPLIT structure: principal split by FIXED shares ("concurrently, as follows: 1. 30% ...
///     2. 70% ..."), not by balance. Leg one is SEQ(A1, A2), leg two is B; each is paid its share of
///     the period's principal whatever the balances, and once a leg is retired the other takes its
///     share. These tests drive the DTO -> controller -> engine path.
/// </summary>
public class SplitStructureTests
{
    private static readonly DateTime FirstPayDate = new(2026, 10, 16);

    // Leg one is 30% of the principal but only 20% of the balance, so a balance split would pay it
    // 20% and a share split pays it 30% — and retires it first.
    private const double A1 = 10_000_000, A2 = 10_000_000, B = 80_000_000, Pool = A1 + A2 + B;

    private static TrancheDto Class(string name, double balance, int order) => new()
    {
        TrancheName = name, OriginalBalance = balance, TrancheType = "Offered", CashflowType = "PI",
        CouponType = "Fixed", FixedCoupon = 4.0, SubordinationOrder = order, FirstPayDate = FirstPayDate,
        PayFrequency = 12, PayDay = FirstPayDate.Day, DayCount = "30/360"
    };

    private static PayableStructureDto Single(string t) => new() { Type = "SINGLE", Tranche = t };

    private static PayableStructureDto Split(List<double>? shares = null, string legTwo = "B") => new()
    {
        Type = "SPLIT",
        Shares = shares ?? new List<double> { 30, 70 },
        Children = new List<PayableStructureDto>
        {
            new() { Type = "SEQ", Children = new List<PayableStructureDto> { Single("A1"), Single("A2") } },
            Single(legTwo)
        }
    };

    private static WaterfallRequest Request(PayableStructureDto? principal = null) => new()
    {
        ProjectionDate = FirstPayDate.AddMonths(-1),
        CollateralCashflows = Collateral(),
        Deal = new DealDto
        {
            DealName = "SPLIT_TEST",
            WaterfallType = "ComposableStructure",
            InterestTreatment = "Guaranteed",
            Tranches = new List<TrancheDto> { Class("A1", A1, 0), Class("A2", A2, 1), Class("B", B, 2) },
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = new List<string>
                    { "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN" },
                Steps = new List<WaterfallStepDto>
                {
                    new() { Type = "INTEREST", Structure = new PayableStructureDto { Type = "SEQ", Tranches = new List<string> { "A1", "A2", "B" } } },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = principal ?? Split() },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = principal ?? Split() },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = principal ?? Split() },
                    new() { Type = "WRITEDOWN", Structure = new PayableStructureDto { Type = "SEQ", Tranches = new List<string> { "B", "A2", "A1" } } }
                }
            }
        }
    };

    /// <summary>2%/month scheduled + 3%/month prepaid, to zero.</summary>
    private static List<PeriodCashflowDto> Collateral()
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = Pool;
        for (var i = 0; i < 240 && balance > 1.0; i++)
        {
            var sched = i == 239 ? balance : balance * 0.02;
            var unsched = i == 239 ? 0.0 : balance * 0.03;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = "1",
                BeginBalance = balance, Balance = balance - sched - unsched, ScheduledPrincipal = sched,
                UnscheduledPrincipal = unsched, Interest = balance * 5.0 / 1200, NetInterest = balance * 5.0 / 1200, Wac = 5.0
            });
            balance -= sched + unsched;
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
        var bad = Run(request).Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        return bad.Value!.ToString()!;
    }

    private static double Principal(TrancheCashflowDto r) => r.ScheduledPrincipal + r.UnscheduledPrincipal;

    [Fact]
    public void Each_leg_is_paid_its_share_whatever_the_balances_until_it_is_retired()
    {
        var cf = Ok(Request()).TrancheCashflows;
        var collateral = Collateral();
        var legOneRetired = false;
        for (var i = 0; i < collateral.Count; i++)
        {
            var prin = collateral[i].ScheduledPrincipal + collateral[i].UnscheduledPrincipal;
            var legOne = Principal(cf["A1"][i]) + Principal(cf["A2"][i]);
            var legTwo = Principal(cf["B"][i]);
            (legOne + legTwo).Should().BeApproximately(prin, 0.01, $"period {i + 1}: every dollar is paid");
            var legOneBegin = cf["A1"][i].BeginBalance + cf["A2"][i].BeginBalance;
            if (!legOneRetired && legOneBegin > prin * 0.30 + 0.01)
                legOne.Should().BeApproximately(prin * 0.30, 0.01, $"period {i + 1}: leg one takes 30%");
            else
                legOneRetired = true;
            // Within leg one, A1 is paid first.
            if (cf["A1"][i].BeginBalance > legOne + 0.01)
                Principal(cf["A2"][i]).Should().Be(0, $"period {i + 1}");
        }

        legOneRetired.Should().BeTrue("leg one is 20% of the balance and takes 30% of the principal");
        cf["B"][^1].Balance.Should().BeApproximately(0, 0.01);
    }

    [Fact]
    public void A_balance_split_would_pay_leg_one_less()
    {
        var split = Ok(Request()).TrancheCashflows;
        var prorata = Ok(Request(new PayableStructureDto
        {
            Type = "PRORATA",
            Children = new List<PayableStructureDto>
            {
                new() { Type = "SEQ", Children = new List<PayableStructureDto> { Single("A1"), Single("A2") } },
                Single("B")
            }
        })).TrancheCashflows;
        Principal(split["A1"][0]).Should().BeGreaterThan(Principal(prorata["A1"][0]) * 1.4);
    }

    [Fact]
    public void Shares_that_do_not_total_100_are_refused()
    {
        Refusal(Request(Split(new List<double> { 30, 60 }))).Should().Contain("not 100%");
    }

    [Fact]
    public void Shares_that_do_not_match_the_legs_are_refused()
    {
        Refusal(Request(Split(new List<double> { 100 }))).Should().Contain("one share per child");
    }

    [Fact]
    public void A_leg_naming_a_class_the_deal_does_not_carry_is_refused()
    {
        Refusal(Request(Split(legTwo: "X"))).Should().Contain("does not carry");
    }
}
