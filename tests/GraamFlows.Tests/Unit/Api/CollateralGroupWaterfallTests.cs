using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     A unified waterfall that states its collateral groups (<see cref="UnifiedWaterfallDto.Groups" />):
///     an agency REMIC's Security Groups, each "based solely on payments on the Trust Asset Group
///     with the same numerical designation". Before, a unified waterfall built every class in group
///     "1": a second group's collateral opened a group with no classes and was never distributed,
///     and the classes it backs were paid from the first group's. These tests drive the DTO ->
///     controller -> engine path and check each class against its own group's collateral, period by
///     period.
/// </summary>
public class CollateralGroupWaterfallTests
{
    private static readonly DateTime FirstPayDate = new(2026, 10, 20);
    private const double A = 60_000_000, B = 40_000_000, C = 25_000_000, D = 15_000_000;
    private const double Wac1 = 5.5, Wac2 = 6.75, Coupon = 4.5;

    private static PayableStructureDto Seq(params string[] tranches) => new()
        { Type = "SEQ", Tranches = tranches.ToList() };

    private static TrancheDto Class(string name, double balance, int order, string? formula = null) => new()
    {
        TrancheName = name, OriginalBalance = balance, TrancheType = "Offered", CashflowType = "PI",
        CouponType = formula == null ? "Fixed" : "Formula", FixedCoupon = formula == null ? Coupon : null,
        CouponFormula = formula, SubordinationOrder = order,
        FirstPayDate = FirstPayDate, PayFrequency = 12, PayDay = FirstPayDate.Day, DayCount = "30/360"
    };

    private static readonly List<string> Order = new()
        { "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN" };

    private static List<WaterfallStepDto> Steps(params string[] classes) => new()
    {
        new() { Type = "INTEREST", Structure = Seq(classes) },
        new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq(classes) },
        new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq(classes) },
        new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq(classes) },
        new() { Type = "WRITEDOWN", Structure = Seq(classes.Reverse().ToArray()) }
    };

    private static WaterfallRequest Request(Action<WaterfallRequest>? edit = null)
    {
        var request = new WaterfallRequest
        {
            ProjectionDate = FirstPayDate.AddMonths(-1),
            CollateralCashflows = Collateral("1", A + B, Wac1, 0.010, 0.010)
                .Concat(Collateral("2", C + D, Wac2, 0.015, 0.025)).ToList(),
            Deal = new DealDto
            {
                DealName = "COLLATERAL_GROUP_TEST",
                WaterfallType = "ComposableStructure",
                InterestTreatment = "Guaranteed",
                Tranches = new List<TrancheDto>
                {
                    Class("A", A, 0), Class("B", B, 1),
                    // D bears its own group's net WAC.
                    Class("C", C, 2), Class("D", D, 3, "collat_net_wac")
                },
                UnifiedWaterfall = new UnifiedWaterfallDto
                {
                    ExecutionOrder = Order,
                    Steps = Steps("A", "B"),
                    Groups = new List<GroupWaterfallDto> { new() { GroupNum = "2", Steps = Steps("C", "D") } }
                }
            }
        };
        edit?.Invoke(request);
        return request;
    }

    /// <summary>A level pool: <paramref name="sched" /> of the balance scheduled and <paramref name="prepay" /> prepaid each month.</summary>
    private static List<PeriodCashflowDto> Collateral(string group, double pool, double wac, double sched, double prepay)
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = pool;
        for (var i = 0; i < 360 && balance > 1.0; i++)
        {
            var interest = balance * wac / 1200;
            var s = i == 359 ? balance : balance * sched;
            var u = i == 359 ? 0.0 : balance * prepay;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = group,
                BeginBalance = balance, Balance = balance - s - u,
                ScheduledPrincipal = s, UnscheduledPrincipal = u,
                Interest = interest, NetInterest = interest, Wac = wac
            });
            balance -= s + u;
        }

        return periods;
    }

    private static ActionResult<WaterfallResponse> Run(WaterfallRequest request) =>
        new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(request);

    private static WaterfallResponse Ok(WaterfallRequest request)
    {
        var result = Run(request).Result;
        if (result is BadRequestObjectResult bad)
            throw new Xunit.Sdk.XunitException(bad.Value?.ToString());
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<WaterfallResponse>().Subject;
    }

    private static string Refusal(WaterfallRequest request)
    {
        var result = Run(request).Result;
        var message = result switch
        {
            BadRequestObjectResult bad => bad.Value?.ToString(),
            ObjectResult other when other.StatusCode >= 400 => other.Value?.ToString(),
            _ => null
        };
        message.Should().NotBeNull("the request should be refused");
        return message!;
    }

    /// <summary>Each class's balance, period by period, as its own group's collateral pays it sequentially.</summary>
    private static List<double[]> Expected(List<PeriodCashflowDto> pool, double senior, double junior)
    {
        var rows = new List<double[]>();
        var (s, j) = (senior, junior);
        foreach (var p in pool)
        {
            var prin = p.ScheduledPrincipal + p.UnscheduledPrincipal;
            var toS = Math.Min(prin, s); s -= toS; prin -= toS;
            j -= Math.Min(prin, j);
            rows.Add(new[] { s, j });
        }

        return rows;
    }

    [Fact]
    public void Each_group_pays_its_own_classes_from_its_own_collateral_every_period()
    {
        var request = Request();
        var cf = Ok(request).TrancheCashflows;
        var one = Expected(request.CollateralCashflows.Where(p => p.GroupNum == "1").ToList(), A, B);
        var two = Expected(request.CollateralCashflows.Where(p => p.GroupNum == "2").ToList(), C, D);

        for (var i = 0; i < 120; i++)
        {
            var label = $"period {i + 1}";
            cf["A"][i].Balance.Should().BeApproximately(one[i][0], 1.0, label);
            cf["B"][i].Balance.Should().BeApproximately(one[i][1], 1.0, label);
            cf["C"][i].Balance.Should().BeApproximately(two[i][0], 1.0, label);
            cf["D"][i].Balance.Should().BeApproximately(two[i][1], 1.0, label);
        }
    }

    [Fact]
    public void A_net_wac_class_bears_its_own_groups_rate_not_the_deals_blend()
    {
        var cf = Ok(Request()).TrancheCashflows;
        // Group 2's collateral pays 6.75%; the two groups blended pay about 5.8%.
        cf["D"][0].Interest.Should().BeApproximately(D * Wac2 / 1200, 1.0);
    }

    [Fact]
    public void Without_groups_the_classes_are_paid_from_the_one_pool_as_before()
    {
        var request = Request(r =>
        {
            r.Deal.UnifiedWaterfall!.Groups = null;
            r.Deal.UnifiedWaterfall.Steps = Steps("A", "B", "C", "D");
            r.CollateralCashflows = Collateral("1", A + B + C + D, Wac1, 0.010, 0.010);
        });
        var cf = Ok(request).TrancheCashflows;
        var all = request.CollateralCashflows;
        // C is paid only once A and B are retired.
        var aB = Expected(all, A + B, C + D);
        for (var i = 0; i < 60; i++)
            (cf["A"][i].Balance + cf["B"][i].Balance).Should().BeApproximately(aB[i][0], 1.0, $"period {i + 1}");
        cf["C"][0].Balance.Should().Be(C);
    }

    /// <summary>Group 2 with a 0.5% IO on its own pool and penalties paid to it; group 1 collects none.</summary>
    private static WaterfallRequest WithGroupTwoPenalties(bool groupOneCollects = false)
    {
        var request = Request(r =>
        {
            r.Deal.Tranches.Add(new TrancheDto
            {
                TrancheName = "IO2", OriginalBalance = C + D, TrancheType = "Offered", CashflowType = "IO",
                CouponType = "Fixed", FixedCoupon = 0.5, SubordinationOrder = 4,
                FirstPayDate = FirstPayDate, PayFrequency = 12, PayDay = FirstPayDate.Day, DayCount = "30/360"
            });
            var group = r.Deal.UnifiedWaterfall!.Groups![0];
            group.Steps[0].Structure = Seq("C", "D", "IO2");
            group.Steps.Add(new WaterfallStepDto
                { Type = "PREPAYMENT_PENALTY", Shares = new() { new() { Tranche = "IO2", Pct = 100 } } });
            r.Deal.UnifiedWaterfall.ExecutionOrder = Order.Take(1).Append("PREPAYMENT_PENALTY")
                .Concat(Order.Skip(1)).ToList();
            foreach (var p in r.CollateralCashflows.Where(p => p.Period <= 12))
                if (p.GroupNum == "2" || groupOneCollects)
                    p.PrepaymentPenalty = 1_000.0 * p.Period;
        });
        return request;
    }

    [Fact]
    public void A_groups_penalties_pay_its_own_class_and_a_group_that_collects_none_states_no_step()
    {
        var with = Ok(WithGroupTwoPenalties()).TrancheCashflows;
        var plain = Ok(Request()).TrancheCashflows;
        for (var i = 0; i < 12; i++)
        {
            var label = $"period {i + 1}";
            with["IO2"][i].PrepaymentPenalty.Should().BeApproximately(1_000.0 * (i + 1), 1e-6, label);
            // The IO's notional is its own group's pool, not the deal's.
            var pool = i == 0 ? C + D : plain["C"][i - 1].Balance + plain["D"][i - 1].Balance;
            (with["IO2"][i].Interest - with["IO2"][i].PrepaymentPenalty)
                .Should().BeApproximately(pool * 0.5 / 1200, 1.0, label);
            foreach (var c in new[] { "A", "B", "C", "D" })
                with[c][i].Balance.Should().BeApproximately(plain[c][i].Balance, 1e-6, $"{c} {label}");
        }
    }

    [Fact]
    public void Penalties_a_group_collects_with_no_step_of_its_own_are_refused()
    {
        Refusal(WithGroupTwoPenalties(groupOneCollects: true))
            .Should().Contain("prepayment penalties").And.Contain("pays them to no class");
    }

    [Fact]
    public void A_groups_accrual_class_accretes_and_pays_the_class_its_steps_direct_it_to()
    {
        var request = Request(r =>
        {
            r.Deal.Tranches[3] = Class("D", D, 3);
            var group = r.Deal.UnifiedWaterfall!.Groups![0];
            group.Steps.Add(new WaterfallStepDto { Type = "ACCRUAL", Tranche = "D", Structure = Seq("C", "D") });
            r.Deal.UnifiedWaterfall.ExecutionOrder = Order.Take(4).Append("ACCRUAL").Append("WRITEDOWN").ToList();
        });
        var cf = Ok(request).TrancheCashflows;
        var two = request.CollateralCashflows.Where(p => p.GroupNum == "2").ToList();
        var (c, d) = (C, D);
        for (var i = 0; i < 24; i++)
        {
            var accrual = d * Coupon / 1200;
            var prin = two[i].ScheduledPrincipal + two[i].UnscheduledPrincipal;
            var toC = Math.Min(prin, c); c -= toC; prin -= toC;
            d -= Math.Min(prin, d);
            var accToC = Math.Min(accrual, c); c -= accToC;
            // D accretes its accrual; the cash it stands for pays C, and D itself once C is retired.
            d += accToC;
            cf["C"][i].Balance.Should().BeApproximately(c, 1.0, $"C period {i + 1}");
            cf["D"][i].Balance.Should().BeApproximately(d, 1.0, $"D period {i + 1}");
        }

        // Group 1 is as it was.
        var plain = Ok(Request()).TrancheCashflows;
        cf["A"][23].Balance.Should().BeApproximately(plain["A"][23].Balance, 1e-6);
    }

    /// <summary>An exchange (MX) class received for all of C and D.</summary>
    private static void AddExchange(WaterfallRequest r, params string[] components)
    {
        var byName = r.Deal.Tranches.ToDictionary(t => t.TrancheName);
        var face = components.Sum(c => byName[c].OriginalBalance);
        r.Deal.Tranches.Add(new TrancheDto
        {
            TrancheName = "MX", OriginalBalance = face, TrancheType = "Exchanged", CashflowType = "PI",
            CouponType = "Fixed", FixedCoupon = Coupon, SubordinationOrder = 9,
            FirstPayDate = FirstPayDate, PayFrequency = 12, PayDay = FirstPayDate.Day, DayCount = "30/360"
        });
        r.Deal.ExchangeShares = new List<ExchangeShareDto>
        {
            new()
            {
                ExchangeTranche = "MX",
                Shares = components.Select(c => new ExShareDto { TrancheName = c, ShareAmount = byName[c].OriginalBalance })
                    .ToList()
            }
        };
    }

    [Fact]
    public void An_exchange_class_is_settled_from_its_components_in_their_group()
    {
        var cf = Ok(Request(r => AddExchange(r, "C", "D"))).TrancheCashflows;
        for (var i = 0; i < 60; i++)
            cf["MX"][i].Balance.Should().BeApproximately(cf["C"][i].Balance + cf["D"][i].Balance, 1.0,
                $"period {i + 1}");
    }

    [Fact]
    public void An_exchange_class_over_two_groups_is_refused()
    {
        Refusal(Request(r => AddExchange(r, "B", "C")))
            .Should().Contain("Exchange class 'MX' is exchanged for classes of groups 1 and 2");
    }

    [Fact]
    public void A_class_two_groups_name_is_refused()
    {
        Refusal(Request(r => r.Deal.UnifiedWaterfall!.Groups![0].Steps = Steps("C", "D", "B")))
            .Should().Contain("'B' is named by the steps of group 1 and of group 2");
    }

    [Fact]
    public void A_stated_group_with_no_collateral_is_refused()
    {
        Refusal(Request(r => r.CollateralCashflows = Collateral("1", A + B, Wac1, 0.01, 0.01)))
            .Should().Contain("group(s) 2 have steps and no collateral");
    }

    [Fact]
    public void Collateral_in_a_group_no_steps_state_is_refused()
    {
        Refusal(Request(r => r.CollateralCashflows.AddRange(Collateral("3", 1_000_000, Wac1, 0.01, 0.01))))
            .Should().Contain("group(s) 3 have collateral and no steps");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("02")]
    [InlineData("B")]
    public void A_group_numbered_other_than_a_whole_number_past_one_is_refused(string groupNum)
    {
        Refusal(Request(r => r.Deal.UnifiedWaterfall!.Groups![0].GroupNum = groupNum))
            .Should().Contain($"a collateral group is numbered '{groupNum}'");
    }

    [Fact]
    public void A_group_stated_twice_is_refused()
    {
        Refusal(Request(r => r.Deal.UnifiedWaterfall!.Groups!.Add(
                new GroupWaterfallDto { GroupNum = "2", Steps = Steps("C", "D") })))
            .Should().Contain("collateral group 2 is stated twice");
    }

    [Fact]
    public void A_step_type_a_group_does_not_build_is_refused()
    {
        Refusal(Request(r => r.Deal.UnifiedWaterfall!.Groups![0].Steps.Add(
                new WaterfallStepDto { Type = "EXCESS", Structure = Seq("D") })))
            .Should().Contain("collateral group 2 states EXCESS, which a group does not build");
    }

    [Fact]
    public void A_group_missing_a_required_step_is_refused()
    {
        Refusal(Request(r => r.Deal.UnifiedWaterfall!.Groups![0].Steps.RemoveAll(s => s.Type == "WRITEDOWN")))
            .Should().Contain("requires WRITEDOWN step");
    }

    [Fact]
    public void Groups_with_explicit_deal_structures_are_refused()
    {
        Refusal(Request(r => r.Deal.DealStructures = new List<DealStructureDto>
            {
                new() { ClassGroupName = "A", PayFrom = "Sequential", GroupNum = "1" }
            }))
            .Should().Contain("states collateral groups and the request states its deal structures itself");
    }

    [Fact]
    public void Groups_outside_the_composable_engine_are_refused()
    {
        Refusal(Request(r =>
            {
                r.Deal.WaterfallType = "Sequential";
                r.Deal.UnifiedWaterfall!.ExecutionOrder = null;
            }))
            .Should().Contain("collateral groups are run by the ComposableStructure engine only");
    }
}
