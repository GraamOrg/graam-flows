using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     An interest-only EXCHANGE class whose notional the document states apart from the
///     notionals exchanged for it. The shape is a Ginnie Mae REMIC's MX interest-only class: Class
///     IB is received for all of IA (an IO on A) and half of AI (another IO on A), so it collects
///     IA's interest plus half of AI's — but its Class Notional Balance is "100% of A", A's
///     balance, not the 1.5x the two notionals exchanged add up to.
///     <para>
///         Before, an exchange IO was reset to the POOL every period (DynamicGroup.NotionalClasses),
///         so it was priced and its WAL measured on a notional the document never states: on GNMA
///         2026-059 that put $187.1M under Class IB for a stated $77.1M, and its four yields came
///         out at -6.6% to -12.9% for a printed 5.6% to 50.4%.
///     </para>
/// </summary>
public class ExchangedIoNotionalTests
{
    private static readonly DateTime FirstPayDate = new(2026, 4, 16);
    private const double ABalance = 60_000_000, BBalance = 40_000_000, Pool = ABalance + BBalance;

    private static TrancheDto Note(string name, double balance, int order) => new()
    {
        TrancheName = name, OriginalBalance = balance, TrancheType = "Offered", CashflowType = "PI",
        CouponType = "Fixed", FixedCoupon = 4.5, SubordinationOrder = order, FirstPayDate = FirstPayDate,
        PayFrequency = 12, PayDay = FirstPayDate.Day
    };

    private static TrancheDto Io(string name, double coupon, int order) => new()
    {
        TrancheName = name, OriginalBalance = ABalance, TrancheType = "Offered", CashflowType = "IO",
        CouponType = "Fixed", FixedCoupon = coupon, NotionalReference = "A", PayFrom = "Notional",
        SubordinationOrder = order, FirstPayDate = FirstPayDate, PayFrequency = 12, PayDay = FirstPayDate.Day
    };

    private static TrancheDto Mx(string? notional, string cashflowType = "IO") => new()
    {
        TrancheName = "IB", OriginalBalance = ABalance, TrancheType = "Exchanged", CashflowType = cashflowType,
        CouponType = "None", NotionalReference = notional, SubordinationOrder = 10, FirstPayDate = FirstPayDate,
        PayFrequency = 12, PayDay = FirstPayDate.Day
    };

    private static PayableStructureDto Seq(params string[] tranches) => new() { Type = "SEQ", Tranches = tranches.ToList() };

    private static WaterfallRequest Request(TrancheDto mx, string ioReferenced = "A") => new()
    {
        ProjectionDate = FirstPayDate.AddMonths(-1),
        CollateralCashflows = Collateral(),
        Deal = new DealDto
        {
            DealName = "EXCHANGED_IO_NOTIONAL", WaterfallType = "ComposableStructure", InterestTreatment = "Guaranteed",
            Tranches = new List<TrancheDto> { Note("A", ABalance, 0), Note("B", BBalance, 1), Io("IA", 1.0, 2), Io("AI", 0.5, 3), mx },
            ExchangeShares = new List<ExchangeShareDto>
            {
                new()
                {
                    ExchangeTranche = "IB",
                    Shares = new List<ExShareDto>
                    {
                        new() { TrancheName = "IA", ShareAmount = ABalance },
                        new() { TrancheName = "AI", ShareAmount = ABalance / 2 }
                    }
                }
            },
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = new List<string>
                    { "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN" },
                Steps = new List<WaterfallStepDto>
                {
                    new() { Type = "INTEREST", Structure = Seq("A", "B", "IA", "AI") },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A", "B") },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A", "B") },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A", "B") },
                    new() { Type = "WRITEDOWN", Structure = Seq("B", "A") }
                }
            }
        }
    };

    /// <summary>3%/month scheduled + 4%/month prepaid; A pays first, so A runs off well ahead of the pool.</summary>
    private static List<PeriodCashflowDto> Collateral()
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = Pool;
        for (var i = 0; i < 120 && balance > 1.0; i++)
        {
            var sched = i == 119 ? balance : balance * 0.03;
            var unsched = i == 119 ? 0.0 : balance * 0.04;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = "1", BeginBalance = balance,
                Balance = balance - sched - unsched, ScheduledPrincipal = sched, UnscheduledPrincipal = unsched,
                Interest = balance * 6.0 / 1200, NetInterest = balance * 6.0 / 1200, Wac = 6.0
            });
            balance -= sched + unsched;
        }

        return periods;
    }

    private static ActionResult<WaterfallResponse> Run(WaterfallRequest r) =>
        new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(r);

    private static Dictionary<string, List<TrancheCashflowDto>> Ok(WaterfallRequest r)
    {
        var ok = Run(r).Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<WaterfallResponse>().Subject.TrancheCashflows;
    }

    [Fact]
    public void Its_notional_follows_the_stated_classes_and_its_interest_is_its_components()
    {
        var cf = Ok(Request(Mx("A")));
        var a = cf["A"];
        var ib = cf["IB"];
        var aGone = Enumerable.Range(0, a.Count).First(i => a[i].Balance < 1.0);
        for (var i = 0; i < a.Count; i++)
        {
            ib[i].Balance.Should().BeApproximately(a[i].Balance, 1.0, $"period {i + 1}: IB's notional is 100% of A");
            ib[i].Interest.Should().BeApproximately(cf["IA"][i].Interest + cf["AI"][i].Interest / 2, 0.01,
                $"period {i + 1}: IB collects IA's interest and half of AI's");
        }

        // A retires while B is still outstanding: the pool would still be well above zero.
        aGone.Should().BeLessThan(a.Count - 1);
        ib[aGone].Balance.Should().BeApproximately(0, 1.0);
        ib.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal).Should().BeApproximately(0, 1.0,
            "an interest-only class receives no principal: its notional runs off as its balance");
    }

    [Fact]
    public void Its_stated_notional_can_follow_each_class_at_its_own_share()
    {
        // "100% of A and 50% of B": one notional the one proportion cannot carry (notionalWeights).
        var mx = Mx("A,B");
        mx.OriginalBalance = ABalance + 0.5 * BBalance;
        mx.NotionalWeights = new Dictionary<string, double> { ["A"] = 1.0, ["B"] = 0.5 };
        var cf = Ok(Request(mx));
        for (var i = 0; i < cf["A"].Count; i++)
            cf["IB"][i].Balance.Should().BeApproximately(cf["A"][i].Balance + 0.5 * cf["B"][i].Balance, 1.0,
                $"period {i + 1}");
        // Unweighted, the same reference is one share of A + B: not the same notional.
        var plain = Mx("A,B");
        plain.OriginalBalance = ABalance + 0.5 * BBalance;
        var one = Ok(Request(plain))["IB"];
        one[1].Balance.Should().NotBeApproximately(cf["IB"][1].Balance, 1000.0);
    }

    [Fact]
    public void Without_a_stated_notional_it_is_on_the_pool_as_before()
    {
        var cf = Ok(Request(Mx(null)));
        cf["IB"][0].BeginBalance.Should().BeApproximately(Pool, 1.0);
    }

    [Fact]
    public void A_notional_on_an_exchange_class_with_principal_is_ignored_as_before()
    {
        // Agency CRT strips are sent this way (Exchanged, principal, a notionalReference): the
        // reference has never been read for them, and it is not now.
        var withRef = Ok(Request(Mx("A", cashflowType: "PI")));
        var without = Ok(Request(Mx(null, cashflowType: "PI")));
        for (var i = 0; i < withRef["IB"].Count; i++)
            withRef["IB"][i].Balance.Should().Be(without["IB"][i].Balance, $"period {i + 1}");
    }

    [Fact]
    public void A_notional_on_an_interest_only_class_is_refused()
    {
        var bad = Run(Request(Mx("IA"))).Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("pays no principal");
    }

    [Fact]
    public void A_notional_on_a_class_the_deal_does_not_carry_is_refused()
    {
        var bad = Run(Request(Mx("ZZ"))).Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("not a tranche of this deal");
    }
}
