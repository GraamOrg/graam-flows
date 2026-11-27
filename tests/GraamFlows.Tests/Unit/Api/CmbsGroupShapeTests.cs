using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     The two CMBS group shapes, stated through <see cref="UnifiedWaterfallDto.Groups" />, give the
///     cashflows the same deal gives stated by hand as explicit <c>dealStructures</c> and
///     <c>payRules</c> (what a caller wrote before the groups could be stated), period by period
///     and class by class:
///     <list type="bullet">
///         <item>
///             a LOAN-SPECIFIC group: a note held outside the pool pays only its own classes (a
///             senior, a junior, a vertical interest pro rata beside them and an interest-only
///             class on the senior), at its own net rate;
///         </item>
///         <item>
///             an ASSET group: a loan's principal pays its own class first and then the pooled
///             cascade, the classes shared, and its interest is the pool's
///             (<see cref="GroupWaterfallDto.SharesClasses" />, <see cref="GroupWaterfallDto.Interest" />).
///         </item>
///     </list>
///     The collateral defaults and recovers, so every principal and loss step runs. Synthetic deals.
/// </summary>
public class CmbsGroupShapeTests
{
    private static readonly DateTime FirstPayDate = new(2026, 10, 15);

    private static PayableStructureDto Single(string t) => new() { Type = "SINGLE", Tranche = t };
    private static PayableStructureDto Seq(params PayableStructureDto[] c) => new() { Type = "SEQ", Children = c.ToList() };
    private static PayableStructureDto Seq(params string[] t) => Seq(t.Select(Single).ToArray());
    private static PayableStructureDto ProRata(params PayableStructureDto[] c) => new() { Type = "PRORATA", Children = c.ToList() };

    private static TrancheDto Class(string name, double balance, double coupon, string? notional = null) => new()
    {
        TrancheName = name, OriginalBalance = balance, TrancheType = "Offered",
        CashflowType = notional == null ? "PI" : "IO", CouponType = "Fixed", FixedCoupon = coupon,
        NotionalReference = notional, FirstPayDate = FirstPayDate, PayFrequency = 12,
        PayDay = FirstPayDate.Day, DayCount = "30/360"
    };

    private static List<WaterfallStepDto> Steps(PayableStructureDto interest, PayableStructureDto principal,
        PayableStructureDto writedown) => new()
    {
        new() { Type = "INTEREST", Structure = interest },
        new() { Type = "PRINCIPAL", Source = "scheduled", Default = principal },
        new() { Type = "PRINCIPAL", Source = "unscheduled", Default = principal },
        new() { Type = "PRINCIPAL", Source = "recovery", Default = principal },
        new() { Type = "WRITEDOWN", Structure = writedown }
    };

    private static readonly List<string> Order = new()
        { "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN" };

    /// <summary>A pool that pays, prepays, defaults and recovers each month.</summary>
    private static List<PeriodCashflowDto> Collateral(string group, double pool, double wac, double cdr)
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = pool;
        for (var i = 0; i < 120 && balance > 1.0; i++)
        {
            var interest = balance * wac / 1200;
            var defaulted = i == 119 ? 0 : balance * cdr;
            var recovery = defaulted * 0.6;
            var sched = i == 119 ? balance - defaulted : balance * 0.004;
            var unsched = i == 119 ? 0.0 : balance * 0.006;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = group,
                BeginBalance = balance, Balance = balance - sched - unsched - defaulted,
                ScheduledPrincipal = sched, UnscheduledPrincipal = unsched, DefaultedPrincipal = defaulted,
                RecoveryPrincipal = recovery, CollateralLoss = defaulted - recovery,
                Interest = interest, NetInterest = interest * 0.98, Wac = wac
            });
            balance -= sched + unsched + defaulted;
        }

        return periods;
    }

    private static string Dsl(PayableStructureDto s) => s.Type switch
    {
        "SINGLE" => $"SINGLE('{s.Tranche}')",
        _ => $"{s.Type}({string.Join(", ", (s.Children ?? new()).Select(Dsl))})"
    };

    /// <summary>A group's steps as the explicit pay rules a caller wrote before (GROUP_n).</summary>
    private static List<PayRuleDto> Rules(string group, PayableStructureDto interest, PayableStructureDto principal,
        PayableStructureDto writedown)
    {
        var rules = new List<PayRuleDto>();
        foreach (var (fn, node) in new[]
                 {
                     ("SET_INTEREST_STRUCT", interest), ("SET_SCHED_STRUCT", principal), ("SET_PREPAY_STRUCT", principal),
                     ("SET_RECOV_STRUCT", principal), ("SET_WRITEDOWN_STRUCT", writedown)
                 })
            rules.Add(new PayRuleDto
            {
                RuleName = $"G{group}{fn.Split('_')[1]}Struct", ClassGroupName = $"GROUP_{group}",
                Formula = $"{fn}({Dsl(node)})", Priority = 1000 + rules.Count
            });
        return rules;
    }

    private static Dictionary<string, List<TrancheCashflowDto>> Run(WaterfallRequest request)
    {
        var result = new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(request).Result;
        if (result is BadRequestObjectResult bad)
            throw new Xunit.Sdk.XunitException(bad.Value?.ToString());
        return result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<WaterfallResponse>()
            .Subject.TrancheCashflows;
    }

    private static string Refusal(WaterfallRequest request)
    {
        var result = new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(request).Result;
        return result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()!;
    }

    private static void SameCashflows(Dictionary<string, List<TrancheCashflowDto>> a,
        Dictionary<string, List<TrancheCashflowDto>> b)
    {
        a.Keys.Should().BeEquivalentTo(b.Keys);
        foreach (var name in a.Keys)
        {
            a[name].Count.Should().Be(b[name].Count, name);
            for (var i = 0; i < a[name].Count; i++)
            {
                var (x, y) = (a[name][i], b[name][i]);
                var label = $"{name} period {i + 1}";
                x.Balance.Should().Be(y.Balance, label);
                x.Interest.Should().Be(y.Interest, label);
                x.ScheduledPrincipal.Should().Be(y.ScheduledPrincipal, label);
                x.UnscheduledPrincipal.Should().Be(y.UnscheduledPrincipal, label);
                x.Writedown.Should().Be(y.Writedown, label);
                x.Coupon.Should().Be(y.Coupon, label);
            }
        }
    }

    // ------------------------------------------------------------------ loan-specific groups

    private const double A = 60_000_000, B = 25_000_000, R = 0;
    private const double S = 8_000_000, J = 1_500_000, V = 500_000;

    private static WaterfallRequest LoanSpecific(bool explicitly)
    {
        var tranches = new List<TrancheDto>
        {
            Class("A", A, 4.0), Class("B", B, 5.0), Class("R", R, 0.0),
            Class("NS", S, 6.0), Class("NJ", J, 7.0), Class("NV", V, 6.2), Class("NX", S, 0.75, "NS")
        };
        // Group 1: the pooled classes. Group 2: the note's classes, its vertical interest beside them.
        var interest1 = Seq("A", "B", "R");
        var principal1 = Seq("A", "B", "R");
        var losses1 = Seq("R", "B", "A");
        var interest2 = Seq(ProRata(Single("NS"), Single("NX")), Single("NJ"), Single("NV"));
        var principal2 = ProRata(Single("NV"), Seq("NS", "NJ"));
        var losses2 = ProRata(Single("NV"), Seq("NJ", "NS"));
        var waterfall = new UnifiedWaterfallDto { ExecutionOrder = Order, Steps = Steps(interest1, principal1, losses1) };
        var deal = new DealDto
        {
            DealName = "LOAN_SPECIFIC_GROUP", WaterfallType = "ComposableStructure", InterestTreatment = "Collateral",
            Tranches = tranches, UnifiedWaterfall = waterfall
        };
        if (explicitly)
        {
            deal.DealStructures = new List<DealStructureDto>
            {
                new() { ClassGroupName = "A", PayFrom = "Sequential", GroupNum = "1", SubordinationOrder = 1 },
                new() { ClassGroupName = "B", PayFrom = "Sequential", GroupNum = "1", SubordinationOrder = 2 },
                new() { ClassGroupName = "R", PayFrom = "Sequential", GroupNum = "1", SubordinationOrder = 3 },
                new() { ClassGroupName = "NS", PayFrom = "Sequential", GroupNum = "2", SubordinationOrder = 1 },
                new() { ClassGroupName = "NJ", PayFrom = "Sequential", GroupNum = "2", SubordinationOrder = 2 },
                new() { ClassGroupName = "NV", PayFrom = "Sequential", GroupNum = "2", SubordinationOrder = 3 },
                new()
                {
                    ClassGroupName = "NX", PayFrom = "Notional", GroupNum = "2", ExchangableTranche = "NS",
                    SubordinationOrder = 0
                }
            };
            deal.PayRules = Rules("2", interest2, principal2, losses2);
        }
        else
        {
            waterfall.Groups = new List<GroupWaterfallDto>
                { new() { GroupNum = "2", Steps = Steps(interest2, principal2, losses2) } };
        }

        return new WaterfallRequest
        {
            ProjectionDate = FirstPayDate.AddMonths(-1),
            CollateralCashflows = Collateral("1", A + B, 5.1, 0.002).Concat(Collateral("2", S + J + V, 6.8, 0.004)).ToList(),
            Deal = deal
        };
    }

    [Fact]
    public void A_loan_specific_group_stated_as_a_group_pays_what_its_explicit_rules_paid()
    {
        SameCashflows(Run(LoanSpecific(explicitly: false)), Run(LoanSpecific(explicitly: true)));
    }

    // ------------------------------------------------------------------ asset groups

    private const double A1 = 45_000_000, A2 = 30_000_000, ABP = 10_000_000, B2 = 15_000_000;

    private static WaterfallRequest AssetGroup(bool explicitly, Action<GroupWaterfallDto>? edit = null)
    {
        var tranches = new List<TrancheDto>
        {
            Class("A1", A1, 3.9), Class("A2", A2, 4.2), Class("ABP", ABP, 4.4), Class("B", B2, 5.0),
            Class("R", 0, 0.0), Class("X", A1 + A2, 0.6, "A1,A2")
        };
        var interest = Seq("A1", "A2", "ABP", "X", "B", "R");
        var cascade = Seq("A1", "A2", "ABP", "B", "R");
        var losses = Seq("R", "B", "ABP", "A2", "A1");
        // The loan's principal pays ABP first, then the pooled cascade without it.
        var own = Seq(Single("ABP"), Seq("A1", "A2", "B", "R"));
        var waterfall = new UnifiedWaterfallDto { ExecutionOrder = Order, Steps = Steps(interest, cascade, losses) };
        var deal = new DealDto
        {
            DealName = "ASSET_GROUP", WaterfallType = "ComposableStructure", InterestTreatment = "Collateral",
            Tranches = tranches, UnifiedWaterfall = waterfall
        };
        var group1 = Collateral("1", A1 + A2 + B2, 5.2, 0.002);
        var group2 = Collateral("2", ABP, 7.4, 0.003);
        if (explicitly)
        {
            // Every principal class shared; the interest-only class group 1's.
            deal.DealStructures = new[] { ("A1", 5), ("A2", 4), ("ABP", 3), ("B", 2), ("R", 1) }
                .Select(c => new DealStructureDto
                    { ClassGroupName = c.Item1, PayFrom = "Sequential", GroupNum = "0", SubordinationOrder = c.Item2 })
                .Append(new DealStructureDto
                {
                    ClassGroupName = "X", PayFrom = "Notional", GroupNum = "1", ExchangableTranche = "A1,A2",
                    SubordinationOrder = 0
                }).ToList();
            deal.PayRules = Rules("2", Single("R"), own, losses);
            // The loan's interest posted in group 1's rows by the caller.
            foreach (var (g1, g2) in group1.Zip(group2))
            {
                g1.Interest += g2.Interest;
                g1.NetInterest += g2.NetInterest;
                g2.Interest = g2.NetInterest = 0;
            }
        }
        else
        {
            var group = new GroupWaterfallDto
            {
                GroupNum = "2", SharesClasses = true, Interest = "pooled", Steps = Steps(Single("R"), own, losses)
            };
            edit?.Invoke(group);
            waterfall.Groups = new List<GroupWaterfallDto> { group };
        }

        return new WaterfallRequest
        {
            ProjectionDate = FirstPayDate.AddMonths(-1), CollateralCashflows = group1.Concat(group2).ToList(),
            Deal = deal
        };
    }

    [Fact]
    public void An_asset_group_stated_as_a_group_pays_what_its_explicit_rules_paid()
    {
        var stated = Run(AssetGroup(explicitly: false));
        SameCashflows(stated, Run(AssetGroup(explicitly: true)));
        // ABP is paid the loan's principal first, while A1 still stands: a single pooled cascade
        // would pay it nothing until A1 and A2 are retired.
        (stated["ABP"][0].ScheduledPrincipal + stated["ABP"][0].UnscheduledPrincipal).Should().BeGreaterThan(0);
        stated["A1"][0].Balance.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_class_two_groups_name_without_sharing_is_refused_and_says_how_to_share_it()
    {
        Refusal(AssetGroup(explicitly: false, g => g.SharesClasses = false))
            .Should().Contain("state \"sharesClasses\": true on group 2");
    }

    [Fact]
    public void A_group_whose_interest_is_its_own_pays_its_interest_from_its_own_collateral()
    {
        // The same deal with the loan's interest its own: R, the group's INTEREST step, takes it.
        var own = Run(AssetGroup(explicitly: false, g => g.Interest = "own"));
        var pooled = Run(AssetGroup(explicitly: false));
        own["R"].Sum(c => c.Interest).Should().BeGreaterThanOrEqualTo(pooled["R"].Sum(c => c.Interest));
        own["ABP"][0].Balance.Should().Be(pooled["ABP"][0].Balance);
    }

    [Fact]
    public void A_pooled_group_whose_interest_step_pays_a_class_group_1_does_not_is_refused()
    {
        var request = AssetGroup(explicitly: false);
        request.Deal.Tranches.Add(Class("ABPX", ABP, 0.5, "ABP"));
        request.Deal.UnifiedWaterfall!.Groups![0].Steps[0].Structure = Seq("R", "ABPX");
        Refusal(request).Should().Contain("pools its interest with group 1, but its INTEREST step pays ABPX");
    }

    [Fact]
    public void An_interest_basis_the_engine_does_not_know_is_refused()
    {
        Refusal(AssetGroup(explicitly: false, g => g.Interest = "shared"))
            .Should().Contain("states interest 'shared'");
    }

    [Fact]
    public void An_exchange_class_over_a_shared_class_is_a_view_of_it()
    {
        var request = AssetGroup(explicitly: false);
        request.Deal.Tranches.Add(new TrancheDto
        {
            TrancheName = "MX", OriginalBalance = A2, TrancheType = "Exchanged", CashflowType = "PI",
            CouponType = "Fixed", FixedCoupon = 4.2, FirstPayDate = FirstPayDate, PayFrequency = 12,
            PayDay = FirstPayDate.Day, DayCount = "30/360"
        });
        request.Deal.ExchangeShares = new List<ExchangeShareDto>
            { new() { ExchangeTranche = "MX", Shares = new() { new() { TrancheName = "A2", ShareAmount = A2 } } } };
        var cf = Run(request);
        for (var i = 0; i < cf["A2"].Count; i++)
        {
            cf["MX"][i].Balance.Should().BeApproximately(cf["A2"][i].Balance, 1e-6, $"period {i + 1}");
            (cf["MX"][i].ScheduledPrincipal + cf["MX"][i].UnscheduledPrincipal).Should().BeApproximately(
                cf["A2"][i].ScheduledPrincipal + cf["A2"][i].UnscheduledPrincipal, 1e-6, $"period {i + 1}");
        }
    }
}
