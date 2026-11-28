using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     A SUBGROUP_SPLIT: each leg is paid its stated share of each subgroup's own principal this
///     period ("60% of the Subgroup X Principal Distribution Amount and 20% of the Subgroup Y
///     Principal Distribution Amount to A; 40% of X and 80% of Y to B"). The collateral is stated
///     by subgroup, one row per subgroup and date, and the two subgroups prepay at different
///     speeds, so no fixed share of the group's principal pays either leg what it is due. These
///     tests drive the DTO -> controller -> engine path.
/// </summary>
public class SubgroupSplitStructureTests
{
    private static readonly DateTime FirstPayDate = new(2026, 10, 20);

    private const double X = 40_000_000, Y = 60_000_000;
    private const double A = 0.6 * X + 0.2 * Y, B = 0.4 * X + 0.8 * Y;

    private static TrancheDto Class(string name, double balance, int order) => new()
    {
        TrancheName = name, OriginalBalance = balance, TrancheType = "Offered", CashflowType = "PI",
        CouponType = "Fixed", FixedCoupon = 4.0, SubordinationOrder = order, FirstPayDate = FirstPayDate,
        PayFrequency = 12, PayDay = FirstPayDate.Day, DayCount = "30/360"
    };

    private static PayableStructureDto Single(string t) => new() { Type = "SINGLE", Tranche = t };

    private static PayableStructureDto SubgroupSplit(List<Dictionary<string, double>>? shares = null) => new()
    {
        Type = "SUBGROUP_SPLIT",
        SubgroupShares = shares ?? new List<Dictionary<string, double>>
        {
            new() { ["X"] = 60, ["Y"] = 20 },
            new() { ["X"] = 40, ["Y"] = 80 }
        },
        Children = new List<PayableStructureDto> { Single("A"), Single("B") }
    };

    private static WaterfallRequest Request(PayableStructureDto? principal = null, bool bySubgroup = true) => new()
    {
        ProjectionDate = FirstPayDate.AddMonths(-1),
        CollateralCashflows = Collateral(bySubgroup),
        Deal = new DealDto
        {
            DealName = "SUBGROUP_SPLIT_TEST",
            WaterfallType = "ComposableStructure",
            InterestTreatment = "Guaranteed",
            Tranches = new List<TrancheDto> { Class("A", A, 0), Class("B", B, 1) },
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = new List<string>
                    { "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN" },
                Steps = new List<WaterfallStepDto>
                {
                    new() { Type = "INTEREST", Structure = new PayableStructureDto { Type = "SEQ", Tranches = new List<string> { "A", "B" } } },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = principal ?? SubgroupSplit() },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = principal ?? SubgroupSplit() },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = principal ?? SubgroupSplit() },
                    new() { Type = "WRITEDOWN", Structure = new PayableStructureDto { Type = "SEQ", Tranches = new List<string> { "B", "A" } } }
                }
            }
        }
    };

    /// <summary>Subgroup X: 1%/month scheduled + 6%/month prepaid; Y: 1% + 1%. Both to zero.</summary>
    private static List<(double X, double Y, double XSched, double YSched)> Paydown()
    {
        var rows = new List<(double, double, double, double)>();
        double x = X, y = Y;
        for (var i = 0; i < 360 && x + y > 1.0; i++)
        {
            var last = i == 359;
            var xs = last ? x : x * 0.01;
            var ys = last ? y : y * 0.01;
            var xu = last ? 0 : x * 0.06;
            var yu = last ? 0 : y * 0.01;
            rows.Add((xs + xu, ys + yu, xs, ys));
            x -= xs + xu;
            y -= ys + yu;
        }

        return rows;
    }

    private static List<PeriodCashflowDto> Collateral(bool bySubgroup)
    {
        var periods = new List<PeriodCashflowDto>();
        double x = X, y = Y;
        var i = 0;
        foreach (var (xp, yp, xs, ys) in Paydown())
        {
            var date = FirstPayDate.AddMonths(i);
            PeriodCashflowDto Row(string? subgroup, double begin, double sched, double prin) => new()
            {
                Period = i + 1, CashflowDate = date, GroupNum = "1", Subgroup = subgroup, BeginBalance = begin,
                Balance = begin - prin, ScheduledPrincipal = sched, UnscheduledPrincipal = prin - sched,
                Interest = begin * 5.0 / 1200, NetInterest = begin * 5.0 / 1200, Wac = 5.0
            };

            if (bySubgroup)
            {
                periods.Add(Row("X", x, xs, xp));
                periods.Add(Row("Y", y, ys, yp));
            }
            else
            {
                periods.Add(Row(null, x + y, xs + ys, xp + yp));
            }

            x -= xp;
            y -= yp;
            i++;
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
        var result = Run(request).Result;
        var bad = result.Should().BeAssignableTo<ObjectResult>().Subject;
        bad.StatusCode.Should().BeGreaterThanOrEqualTo(400);
        return bad.Value!.ToString()!;
    }

    private static double Principal(TrancheCashflowDto r) => r.ScheduledPrincipal + r.UnscheduledPrincipal;

    [Fact]
    public void Each_leg_is_paid_its_share_of_each_subgroups_principal()
    {
        var cf = Ok(Request()).TrancheCashflows;
        var paydown = Paydown();
        for (var i = 0; i < paydown.Count; i++)
        {
            var (xp, yp, xs, ys) = paydown[i];
            (Principal(cf["A"][i]) + Principal(cf["B"][i])).Should()
                .BeApproximately(xp + yp, 0.01, $"period {i + 1}: every dollar is paid");
            // The shares are sized to retire both legs together, so each is paid its own share
            // every period: scheduled principal by the subgroups' scheduled principal, prepaid by
            // their prepaid.
            cf["A"][i].ScheduledPrincipal.Should().BeApproximately(0.6 * xs + 0.2 * ys, 0.01, $"period {i + 1}");
            Principal(cf["A"][i]).Should().BeApproximately(0.6 * xp + 0.2 * yp, 0.01, $"period {i + 1}");
        }

        cf["A"][^1].Balance.Should().BeApproximately(0, 0.01);
        cf["B"][^1].Balance.Should().BeApproximately(0, 0.01);
    }

    [Fact]
    public void No_fixed_share_of_the_group_pays_the_legs_the_same()
    {
        var subgroups = Ok(Request()).TrancheCashflows;
        // The fixed share of the legs' original balances: right on day one, wrong once X prepays
        // faster than Y.
        var fixedShare = Ok(Request(new PayableStructureDto
        {
            Type = "SPLIT",
            Shares = new List<double> { 100 * A / (A + B), 100 * B / (A + B) },
            Children = new List<PayableStructureDto> { Single("A"), Single("B") }
        })).TrancheCashflows;
        Principal(subgroups["A"][0]).Should().BeGreaterThan(Principal(fixedShare["A"][0]) + 100_000);
    }

    [Fact]
    public void A_rows_subgroups_are_merged_into_one_period_of_the_group()
    {
        var response = Ok(Request());
        var bySubgroup = response.TrancheCashflows;
        // The same collateral stated as one row per date pays the two legs the same total.
        var total = Ok(Request(new PayableStructureDto
            { Type = "PRORATA", Children = new List<PayableStructureDto> { Single("A"), Single("B") } }, bySubgroup: false));
        var merged = Ok(Request(new PayableStructureDto
            { Type = "PRORATA", Children = new List<PayableStructureDto> { Single("A"), Single("B") } }));
        for (var i = 0; i < 12; i++)
            (Principal(merged.TrancheCashflows["A"][i]) + Principal(merged.TrancheCashflows["B"][i])).Should()
                .BeApproximately(Principal(total.TrancheCashflows["A"][i]) + Principal(total.TrancheCashflows["B"][i]), 0.01);
        bySubgroup.Should().ContainKeys("A", "B");
    }

    [Fact]
    public void Shares_of_a_subgroup_that_do_not_total_100_are_refused()
    {
        Refusal(Request(SubgroupSplit(new List<Dictionary<string, double>>
        {
            new() { ["X"] = 60, ["Y"] = 20 },
            new() { ["X"] = 30, ["Y"] = 80 }
        }))).Should().Contain("shares of subgroup X total 90%");
    }

    [Fact]
    public void A_subgroup_no_row_states_has_no_collateral_and_is_refused()
    {
        Refusal(Request(SubgroupSplit(new List<Dictionary<string, double>>
        {
            new() { ["X"] = 60, ["Z"] = 20 },
            new() { ["X"] = 40, ["Z"] = 80 }
        }))).Should().Contain("subgroup Z, which the collateral does not state");
    }

    [Fact]
    public void A_paid_off_subgroup_pays_its_legs_nothing_from_then_on()
    {
        // Y pays off in full in period 12 and states no row after it; X pays off in period 150.
        var request = Request();
        var rows = request.CollateralCashflows;
        foreach (var (sub, last) in new[] { ("Y", 12), ("X", 150) })
        {
            var row = rows.Single(r => r.Subgroup == sub && r.Period == last);
            row.UnscheduledPrincipal = row.BeginBalance - row.ScheduledPrincipal;
            row.Balance = 0;
            rows.RemoveAll(r => r.Subgroup == sub && r.Period > last);
        }

        var cf = Ok(request).TrancheCashflows;
        var collateral = rows.GroupBy(r => r.Period).OrderBy(g => g.Key).ToList();
        for (var i = 0; i < collateral.Count; i++)
        {
            var x = collateral[i].Single(r => r.Subgroup == "X");
            var yp = collateral[i].Where(r => r.Subgroup == "Y").Sum(r => r.ScheduledPrincipal + r.UnscheduledPrincipal);
            var xp = x.ScheduledPrincipal + x.UnscheduledPrincipal;
            (Principal(cf["A"][i]) + Principal(cf["B"][i])).Should()
                .BeApproximately(xp + yp, 0.01, $"period {i + 1}: every dollar is paid");
            // While A holds more than its share, it is paid its share: from period 13 on, of X alone.
            if (cf["A"][i].BeginBalance > 0.6 * xp + 0.2 * yp + 0.01)
                Principal(cf["A"][i]).Should().BeApproximately(0.6 * xp + 0.2 * yp, 0.01, $"period {i + 1}");
        }

        (cf["A"].Sum(Principal) + cf["B"].Sum(Principal)).Should().BeApproximately(A + B, 0.01);
    }

    [Fact]
    public void Each_source_is_conserved_every_period_and_over_the_run()
    {
        var cf = Ok(Request()).TrancheCashflows;
        var paydown = Paydown();
        var collected = 0.0;
        var paid = 0.0;
        for (var i = 0; i < paydown.Count; i++)
        {
            var (xp, yp, xs, ys) = paydown[i];
            (cf["A"][i].ScheduledPrincipal + cf["B"][i].ScheduledPrincipal).Should()
                .BeApproximately(xs + ys, 0.01, $"period {i + 1}: scheduled principal");
            (cf["A"][i].UnscheduledPrincipal + cf["B"][i].UnscheduledPrincipal).Should()
                .BeApproximately(xp - xs + (yp - ys), 0.01, $"period {i + 1}: unscheduled principal");
            collected += xp + yp;
            paid += Principal(cf["A"][i]) + Principal(cf["B"][i]);
        }

        paid.Should().BeApproximately(collected, 0.01);
        collected.Should().BeApproximately(X + Y, 0.01);
    }

    [Fact]
    public void A_group_with_no_subgroup_step_is_paid_as_before()
    {
        // The same collateral, stated by subgroup or as the group's rows, pays a waterfall with
        // no SUBGROUP_SPLIT the same in every field of every period (to the rounding of summing
        // the subgroups' rows in the engine rather than before it).
        var prorata = new PayableStructureDto
            { Type = "PRORATA", Children = new List<PayableStructureDto> { Single("A"), Single("B") } };
        var bySubgroup = Ok(Request(prorata)).TrancheCashflows;
        var asGroup = Ok(Request(prorata, bySubgroup: false)).TrancheCashflows;
        foreach (var name in new[] { "A", "B" })
        {
            bySubgroup[name].Count.Should().Be(asGroup[name].Count);
            for (var i = 0; i < asGroup[name].Count; i++)
                foreach (var field in typeof(TrancheCashflowDto).GetProperties()
                             .Where(p => p.PropertyType == typeof(double)))
                    ((double)field.GetValue(bySubgroup[name][i])!).Should().BeApproximately(
                        (double)field.GetValue(asGroup[name][i])!, 1e-6, $"{name} period {i + 1} {field.Name}");
        }
    }

    [Fact]
    public void Collateral_not_stated_by_subgroup_is_refused()
    {
        Refusal(Request(bySubgroup: false)).Should().Contain("the collateral was not stated by subgroup");
    }

    [Fact]
    public void Shares_that_do_not_match_the_legs_are_refused()
    {
        Refusal(Request(SubgroupSplit(new List<Dictionary<string, double>> { new() { ["X"] = 100, ["Y"] = 100 } })))
            .Should().Contain("one per child");
    }
}
