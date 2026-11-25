using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     The PREPAYMENT_PENALTY step: the prepayment penalties the collateral collects
///     (<see cref="PeriodCashflowDto.PrepaymentPenalty" />) paid to the classes a deal names, as
///     interest ("the Trustee will pay 100% of any Prepayment Penalties ... to Class IO"). The
///     interest-only class's coupon is its notional times its rate; the penalty is paid on top of
///     it, whole. These tests drive the DTO -> controller -> engine path.
/// </summary>
public class PrepaymentPenaltyStepTests
{
    private static readonly DateTime FirstPayDate = new(2026, 10, 16);
    private const double A = 80_000_000, B = 20_000_000, Pool = A + B;
    private const double Wac = 5.5;

    private static PayableStructureDto Seq(params string[] tranches) => new()
        { Type = "SEQ", Tranches = tranches.ToList() };

    private static TrancheDto Class(string name, double balance, int order, string cashflowType = "PI",
        double coupon = 4.5) => new()
    {
        TrancheName = name, OriginalBalance = balance, TrancheType = "Offered", CashflowType = cashflowType,
        CouponType = "Fixed", FixedCoupon = coupon, SubordinationOrder = order, ClassReference = name,
        FirstPayDate = FirstPayDate, PayFrequency = 12, PayDay = FirstPayDate.Day, DayCount = "30/360"
    };

    private static double Penalty(int period) => period < 24 ? 1_000.0 * (period + 1) : 0.0;

    private static WaterfallRequest Request(bool penalties = true, List<PrepaymentPenaltyShareDto>? shares = null,
        List<string>? order = null)
    {
        var steps = new List<WaterfallStepDto>
        {
            new() { Type = "INTEREST", Structure = Seq("A", "B", "IO") },
            new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A", "B") },
            new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A", "B") },
            new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A", "B") },
            new() { Type = "WRITEDOWN", Structure = Seq("B", "A") }
        };
        var executionOrder = order ?? new List<string>
            { "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN" };
        if (shares != null)
        {
            steps.Add(new WaterfallStepDto { Type = "PREPAYMENT_PENALTY", Shares = shares });
            if (order == null)
                executionOrder.Insert(1, "PREPAYMENT_PENALTY");
        }

        return new WaterfallRequest
        {
            ProjectionDate = FirstPayDate.AddMonths(-1),
            CollateralCashflows = Collateral(penalties),
            Deal = new DealDto
            {
                DealName = "PREPAYMENT_PENALTY_TEST",
                WaterfallType = "ComposableStructure",
                InterestTreatment = "Guaranteed",
                Tranches = new List<TrancheDto>
                {
                    Class("A", A, 0), Class("B", B, 1),
                    // Interest-only on the pool: 0.5% on a notional that amortizes with it.
                    Class("IO", Pool, 2, "IO", 0.5)
                },
                UnifiedWaterfall = new UnifiedWaterfallDto { ExecutionOrder = executionOrder, Steps = steps }
            }
        };
    }

    private static List<PeriodCashflowDto> Collateral(bool penalties)
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = Pool;
        for (var i = 0; i < 120 && balance > 1.0; i++)
        {
            var interest = balance * Wac / 1200;
            var sched = i == 119 ? balance : balance * 0.005;
            var unsched = i == 119 ? 0.0 : balance * 0.01;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = "1",
                BeginBalance = balance, Balance = balance - sched - unsched,
                ScheduledPrincipal = sched, UnscheduledPrincipal = unsched,
                Interest = interest, NetInterest = interest, Wac = Wac,
                PrepaymentPenalty = penalties ? Penalty(i) : 0.0
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

    private static List<PrepaymentPenaltyShareDto> AllToIo() => new() { new() { Tranche = "IO", Pct = 100 } };

    [Fact]
    public void The_IO_Is_Paid_Its_Coupon_And_Every_Penalty_As_Interest()
    {
        var with = Ok(Request(shares: AllToIo())).TrancheCashflows;
        var without = Ok(Request(penalties: false)).TrancheCashflows;

        for (var i = 0; i < without["IO"].Count; i++)
        {
            var label = $"period {i + 1}";
            with["IO"][i].Interest.Should().BeApproximately(without["IO"][i].Interest + Penalty(i), 1e-6, label);
            with["IO"][i].PrepaymentPenalty.Should().BeApproximately(Penalty(i), 1e-6, label);
            with["IO"][i].Balance.Should().BeApproximately(without["IO"][i].Balance, 1e-6, label);
            foreach (var c in new[] { "A", "B" })
            {
                with[c][i].Interest.Should().Be(without[c][i].Interest, $"{c} {label}");
                with[c][i].Balance.Should().Be(without[c][i].Balance, $"{c} {label}");
                with[c][i].PrepaymentPenalty.Should().Be(0);
            }
        }

        with["IO"].Sum(c => c.PrepaymentPenalty).Should().BeApproximately(
            Enumerable.Range(0, 24).Sum(Penalty), 1e-6, "every penalty dollar is paid");
    }

    [Fact]
    public void A_Split_Pays_Each_Class_Its_Share()
    {
        var cf = Ok(Request(shares: new()
        {
            new() { Tranche = "IO", Pct = 60 },
            new() { Tranche = "B", Pct = 40 }
        })).TrancheCashflows;

        cf["IO"][3].PrepaymentPenalty.Should().BeApproximately(Penalty(3) * 0.6, 1e-6);
        cf["B"][3].PrepaymentPenalty.Should().BeApproximately(Penalty(3) * 0.4, 1e-6);
    }

    [Fact]
    public void A_Penalty_No_Step_Pays_Is_Refused()
    {
        Refusal(Request()).Should().Contain("pays them to no class");
    }

    [Fact]
    public void A_Step_With_No_Shares_Is_Refused()
    {
        Refusal(Request(shares: new())).Should().Contain("'shares'");
    }

    [Fact]
    public void Shares_That_Do_Not_Total_100_Are_Refused()
    {
        Refusal(Request(shares: new() { new() { Tranche = "IO", Pct = 90 } })).Should().Contain("not 100%");
    }

    [Fact]
    public void A_Share_Naming_A_Class_The_Deal_Does_Not_Carry_Is_Refused()
    {
        Refusal(Request(shares: new() { new() { Tranche = "X", Pct = 100 } })).Should().Contain("does not carry");
    }

    [Fact]
    public void A_Step_Ahead_Of_INTEREST_Is_Refused()
    {
        var order = new List<string>
        {
            "PREPAYMENT_PENALTY", "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY",
            "WRITEDOWN"
        };
        Refusal(Request(shares: AllToIo(), order: order)).Should().Contain("after INTEREST");
    }

    [Fact]
    public void Shares_With_No_Step_In_The_Order_Are_Refused()
    {
        var order = new List<string>
            { "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN" };
        Refusal(Request(shares: AllToIo(), order: order)).Should().Contain("no PREPAYMENT_PENALTY step");
    }
}
