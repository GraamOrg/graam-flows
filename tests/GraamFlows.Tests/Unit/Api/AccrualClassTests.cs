using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     An accrual (Z) class and the class its Accrual Amount is directed to (an agency CMO's
///     ZA / VA pair). The Z's coupon is not paid to it: each period it is added to the Z's
///     balance and paid as principal to VA until VA is retired, then to the Z itself. The
///     engine carried this (PayAccrualStructures) but nothing called it, so a Z was paid its
///     coupon in cash like a current-pay class, with no error. These tests drive the
///     DTO -> controller -> engine path and check each period against the rule computed here.
/// </summary>
public class AccrualClassTests
{
    private static readonly DateTime FirstPayDate = new(2026, 10, 16);
    private const double A = 60_000_000, VA = 10_000_000, ZA = 30_000_000, Pool = A + VA + ZA;
    private const double Coupon = 4.5, Wac = 5.5;

    private static PayableStructureDto Seq(params string[] tranches) => new()
        { Type = "SEQ", Tranches = tranches.ToList() };

    private static TrancheDto Class(string name, double balance, int order) => new()
    {
        TrancheName = name, OriginalBalance = balance, TrancheType = "Offered", CashflowType = "PI",
        CouponType = "Fixed", FixedCoupon = Coupon, SubordinationOrder = order,
        FirstPayDate = FirstPayDate, PayFrequency = 12, PayDay = FirstPayDate.Day, DayCount = "30/360"
    };

    private static List<string> Order(bool accrual) => accrual
        ? new() { "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "ACCRUAL", "WRITEDOWN" }
        : new() { "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN" };

    private static WaterfallRequest Request(Action<UnifiedWaterfallDto>? edit = null)
    {
        var waterfall = new UnifiedWaterfallDto
        {
            ExecutionOrder = Order(true),
            Steps = new List<WaterfallStepDto>
            {
                new() { Type = "INTEREST", Structure = Seq("A", "VA", "ZA") },
                new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A", "VA", "ZA") },
                new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A", "VA", "ZA") },
                new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A", "VA", "ZA") },
                new() { Type = "ACCRUAL", Tranche = "ZA", Structure = Seq("VA", "ZA") },
                new() { Type = "WRITEDOWN", Structure = Seq("ZA", "VA", "A") }
            }
        };
        edit?.Invoke(waterfall);
        return new WaterfallRequest
        {
            ProjectionDate = FirstPayDate.AddMonths(-1),
            CollateralCashflows = Collateral(),
            Deal = new DealDto
            {
                DealName = "ACCRUAL_CLASS_TEST",
                WaterfallType = "ComposableStructure",
                InterestTreatment = "Guaranteed",
                Tranches = new List<TrancheDto> { Class("A", A, 0), Class("VA", VA, 1), Class("ZA", ZA, 2) },
                UnifiedWaterfall = waterfall
            }
        };
    }

    /// <summary>Level 2%/month scheduled + 1%/month prepaid: A retires in ~4 years, VA soon after.</summary>
    private static List<PeriodCashflowDto> Collateral()
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = Pool;
        for (var i = 0; i < 240 && balance > 1.0; i++)
        {
            var interest = balance * Wac / 1200;
            var sched = i == 239 ? balance : balance * 0.02;
            var unsched = i == 239 ? 0.0 : balance * 0.01;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = "1",
                BeginBalance = balance, Balance = balance - sched - unsched,
                ScheduledPrincipal = sched, UnscheduledPrincipal = unsched,
                Interest = interest, NetInterest = interest, Wac = Wac
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

    /// <summary>
    ///     The document's rule, period by period: the pool's principal sequentially to A, VA, ZA;
    ///     ZA's coupon added to ZA and paid to VA until retired, then to ZA.
    /// </summary>
    private static List<(double A, double VA, double ZA)> Expected()
    {
        var (a, va, za) = (A, VA, ZA);
        var rows = new List<(double, double, double)>();
        foreach (var p in Collateral())
        {
            var accrual = za * Coupon / 1200;
            var prin = p.ScheduledPrincipal + p.UnscheduledPrincipal;
            var toA = Math.Min(prin, a); a -= toA; prin -= toA;
            var toVa = Math.Min(prin, va); va -= toVa; prin -= toVa;
            za -= Math.Min(prin, za);
            za += accrual;
            var accToVa = Math.Min(accrual, va); va -= accToVa;
            za -= accrual - accToVa;
            rows.Add((a, va, za));
        }

        return rows;
    }

    [Fact]
    public void A_Z_accretes_and_its_accrual_pays_the_class_it_is_directed_to_every_period()
    {
        var cf = Ok(Request()).TrancheCashflows;
        var expected = Expected();

        for (var i = 0; i < expected.Count; i++)
        {
            var label = $"period {i + 1}";
            cf["A"][i].Balance.Should().BeApproximately(expected[i].A, 1.0, label);
            cf["VA"][i].Balance.Should().BeApproximately(expected[i].VA, 1.0, label);
            cf["ZA"][i].Balance.Should().BeApproximately(expected[i].ZA, 1.0, label);
        }

        // Period 1: ZA grows by its coupon, and VA is paid exactly that, with A still outstanding.
        var accrual1 = ZA * Coupon / 1200;
        cf["ZA"][0].Balance.Should().BeApproximately(ZA + accrual1, 0.01);
        (cf["VA"][0].ScheduledPrincipal + cf["VA"][0].UnscheduledPrincipal)
            .Should().BeApproximately(accrual1, 0.01, "VA is paid ZA's Accrual Amount while A takes the pool's principal");
    }

    [Fact]
    public void While_it_accretes_a_Z_receives_no_cash()
    {
        var cf = Ok(Request()).TrancheCashflows;
        var vaOut = cf["VA"].TakeWhile(c => c.Balance > 1.0).Count();
        vaOut.Should().BeGreaterThan(12, "the test needs periods with VA outstanding");

        foreach (var c in cf["ZA"].Take(vaOut))
            (c.Interest + c.ScheduledPrincipal + c.UnscheduledPrincipal).Should().BeApproximately(0, 0.01,
                $"ZA's coupon is added to its balance, not paid ({c.CashflowDate:yyyy-MM})");
    }

    [Fact]
    public void The_classes_are_paid_the_cash_they_would_be_without_the_accrual_moved()
    {
        // Cash is conserved: the Z's coupon is the same cash, paid to VA as principal.
        var withAccrual = Ok(Request()).TrancheCashflows;
        var asCurrentPay = Ok(Request(w =>
        {
            w.Steps.RemoveAll(s => s.Type == "ACCRUAL");
            w.ExecutionOrder = Order(false);
        })).TrancheCashflows;

        for (var i = 0; i < 24; i++)
        {
            double Cash(Dictionary<string, List<TrancheCashflowDto>> cf) => new[] { "A", "VA", "ZA" }
                .Sum(n => cf[n][i].Interest + cf[n][i].ScheduledPrincipal + cf[n][i].UnscheduledPrincipal);
            Cash(withAccrual).Should().BeApproximately(Cash(asCurrentPay), 0.01, $"period {i + 1}");
        }

        withAccrual["ZA"][0].Balance.Should().BeGreaterThan(asCurrentPay["ZA"][0].Balance,
            "the accrual is what makes ZA a Z; without the step it is a current-pay class");
    }

    [Fact]
    public void A_Z_with_no_ACCRUAL_in_the_order_is_refused()
    {
        Refusal(Request(w => w.ExecutionOrder = Order(false)))
            .Should().Contain("ZA").And.Contain("no ACCRUAL step");
    }

    [Fact]
    public void A_Z_outside_the_INTEREST_structure_is_refused()
    {
        Refusal(Request(w => w.Steps[0].Structure = Seq("A", "VA")))
            .Should().Contain("ZA").And.Contain("not in the INTEREST structure");
    }

    [Fact]
    public void ACCRUAL_in_the_order_of_a_deal_with_no_Z_is_refused()
    {
        Refusal(Request(w => w.Steps.RemoveAll(s => s.Type == "ACCRUAL")))
            .Should().Contain("runs ACCRUAL, but no class of the deal accrues");
    }

    [Theory]
    [InlineData(null, true, "names no tranche")]
    [InlineData("ZZ", true, "not a tranche of this deal")]
    [InlineData("ZA", false, "states no structure")]
    public void An_ACCRUAL_step_that_cannot_say_where_the_accrual_goes_is_refused(string? tranche,
        bool structure, string why)
    {
        Refusal(Request(w =>
        {
            var step = w.Steps.Single(s => s.Type == "ACCRUAL");
            step.Tranche = tranche;
            step.Structure = structure ? Seq("VA", "ZA") : null;
        })).Should().Contain(why);
    }

    [Fact]
    public void The_default_order_runs_ACCRUAL_for_a_deal_with_a_Z()
    {
        var cf = Ok(Request(w => w.ExecutionOrder = null)).TrancheCashflows;
        cf["ZA"][0].Balance.Should().BeApproximately(ZA * (1 + Coupon / 1200), 0.01);
    }
}
