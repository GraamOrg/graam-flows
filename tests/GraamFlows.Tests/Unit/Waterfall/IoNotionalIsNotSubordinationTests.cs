using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// #92: an interest-only strip's NOTIONAL is not subordination.
///
/// <c>SubordinateClasses</c> / <c>SubordinateBalance</c> date from the initial commit, and so does
/// the <c>CashflowType != InterestOnly</c> exclusion on <c>DealClasses</c> — the funded set that
/// <c>DynamicGroup.Balance()</c> sums. <c>SubordinateClasses</c> never had that exclusion, but while
/// an IO class carried no balance it did not matter. #22 made pool-referenced IO strips carry the
/// POOL balance as their balance (<c>InitNotionalBalances</c> / <c>SettleNotionalBalances</c>) so
/// their interest and WAL compute, and from then on the missing exclusion counted.
///
/// So a pool-sized notional was counted as loss-absorbing subordination beneath every senior
/// class, and <c>DelinquencySubordinateTrigger</c> divided by the funded subordinate balance PLUS
/// one strip's pool-sized notional. Only one strip counted because the <c>ITranche</c> overload also filters on PayFrom: the
/// excess-servicing strip pays from <c>ExcessServicing</c> and was excluded, the excess-spread
/// strip pays <c>Sequential</c> and was not.
///
/// <c>CreditSupport()</c> divides the same inflated numerator by <c>DynamicGroup.Balance()</c>,
/// which sums <c>DealClasses</c> and so already excludes IO — an overstated ratio, not a
/// cancelling one.
///
/// The property asserted here is the one that cannot drift with the fixture: adding a
/// pool-notional IO strip to a deal must not change any subordination measure on it.
/// </summary>
public class IoNotionalIsNotSubordinationTests
{
    private static readonly DateTime FirstPayDate = new(2026, 2, 25);
    private const double A1Balance = 60_000_000;
    private const double M1Balance = 25_000_000;
    private const double B1Balance = 15_000_000;
    private const double PoolBalance = A1Balance + M1Balance + B1Balance;
    private const double DelinquentPct = 0.06;

    [Fact]
    public void The_delinquency_trigger_is_unchanged_by_adding_pool_notional_io_strips()
    {
        var without = TriggerValues(Run(withIoStrips: false), "DelinquencyTest");
        var with = TriggerValues(Run(withIoStrips: true), "DelinquencyTest");

        with.Should().HaveCount(without.Count);
        for (var i = 0; i < without.Count; i++)
            with[i].Should().BeApproximately(without[i], 1e-9,
                $"period {i + 1}: an IO strip's notional absorbs no loss and must not enter the " +
                "delinquency trigger's subordinate-balance divisor");
    }

    [Fact]
    public void Credit_support_is_unchanged_by_adding_pool_notional_io_strips()
    {
        var without = TriggerValues(Run(withIoStrips: false), "CeTest");
        var with = TriggerValues(Run(withIoStrips: true), "CeTest");

        with.Should().HaveCount(without.Count);
        for (var i = 0; i < without.Count; i++)
            with[i].Should().BeApproximately(without[i], 1e-9,
                $"period {i + 1}: CreditSupport's numerator counted a pool-sized notional over a " +
                "denominator that already excludes it");
    }

    [Fact]
    public void The_first_period_ratios_are_the_funded_subordination_exactly()
    {
        // Anti-vacuity for the two rows above: equal-with-and-without would also hold if both
        // triggers silently reported zero. Pin the actual first-period values against the
        // funded stack, where the notional is 100M and cannot be mistaken for rounding.
        var response = Run(withIoStrips: true);
        var funded = M1Balance + B1Balance;

        var dq = TriggerValues(response, "DelinquencyTest")[0];
        dq.Should().BeApproximately(DelinquentPct * PoolBalance / funded, 0.01,
            "6% of a 100M pool over the 40M funded beneath A1 — with the notional in the divisor " +
            "this reads about a third of that");

        // Exact, not a bound. A `< 1.0` check here let a CreditSupport() hard-wired to return 0
        // pass the whole suite — and with it the credit-enhancement trigger and every rule that
        // reads credit support. Credit support is sampled before any principal is paid in the
        // trigger, so period 1 is 40M funded beneath A1 over 100M funded: 0.40.
        var ce = TriggerValues(response, "CeTest")[0];
        ce.Should().BeApproximately((M1Balance + B1Balance) / PoolBalance, 1e-9,
            "credit support beneath A1 is the funded subordinate over the funded notes; a " +
            "pool-sized notional in the numerator reads 1.40");
    }

    [Fact]
    public void The_io_strips_still_carry_their_pool_notional()
    {
        // The fix removes the notional from SUBORDINATION only. #22's behaviour — the strips
        // tracking the pool so their interest and WAL compute — must be untouched.
        var response = Run(withIoStrips: true);
        response.TrancheCashflows["XS"][0].BeginBalance.Should().BeApproximately(PoolBalance, 1.0);
        response.TrancheCashflows["AIOS"][0].BeginBalance.Should().BeApproximately(PoolBalance, 1.0);
    }

    // ------------------------------------------------------------------------------------------

    private static List<double> TriggerValues(WaterfallResponse response, string name) =>
        (response.TriggerResults ?? new List<TriggerResultDto>())
        .Where(t => t.TriggerName == name)
        .OrderBy(t => t.Period)
        .Select(t => t.Value ?? double.NaN)
        .ToList();

    private static WaterfallResponse Run(bool withIoStrips)
    {
        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);
        var ok = controller.Execute(BuildRequest(withIoStrips)).Result as OkObjectResult;
        ok.Should().NotBeNull("the waterfall request should succeed");
        var response = (ok!.Value as WaterfallResponse)!;
        TriggerValues(response, "DelinquencyTest").Should().NotBeEmpty("the trigger must be evaluated");
        return response;
    }

    private static WaterfallRequest BuildRequest(bool withIoStrips)
    {
        var tranches = new List<TrancheDto>
        {
            Note("A1", A1Balance, 0),
            Note("M1", M1Balance, 1),
            Note("B1", B1Balance, 2)
        };
        var interest = new List<string> { "A1", "M1", "B1" };
        if (withIoStrips)
        {
            // The excess-servicing strip: IO, Reference, pays from ExcessServicing.
            tranches.Add(Strip("AIOS", "Reference", "None", 3));
            // The excess-spread strip: IO, ExcessInterest, pays Sequential.
            tranches.Add(Strip("XS", "Offered", "ExcessInterest", 4));
            interest.AddRange(new[] { "AIOS", "XS" });
        }

        return new WaterfallRequest
        {
            ProjectionDate = FirstPayDate.AddMonths(-1),
            CollateralCashflows = BuildCollateral(),
            Deal = new DealDto
            {
                DealName = "IO_NOTIONAL_SUBORDINATION_TEST",
                WaterfallType = "ComposableStructure",
                ClosingDate = FirstPayDate.AddMonths(-1),
                Tranches = tranches,
                Triggers = new List<TriggerDto>
                {
                    new()
                    {
                        TriggerName = "DelinquencyTest", TriggerType = "DELINQ_TRIGGER_SUB_1",
                        TriggerParam = "0.05", TriggerParam2 = "A1"
                    },
                    new()
                    {
                        TriggerName = "CeTest", TriggerType = "CREDIT_ENHANCEMENT",
                        TriggerParam = "0.0", TriggerParam2 = "A1"
                    }
                },
                UnifiedWaterfall = new UnifiedWaterfallDto
                {
                    ExecutionOrder = new List<string>
                    {
                        "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED",
                        "PRINCIPAL_RECOVERY", "WRITEDOWN"
                    },
                    Steps = new List<WaterfallStepDto>
                    {
                        new() { Type = "INTEREST", Structure = Seq(interest.ToArray()) },
                        new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A1", "M1", "B1") },
                        new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A1", "M1", "B1") },
                        new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A1", "M1", "B1") },
                        new() { Type = "WRITEDOWN", Structure = Seq("B1", "M1", "A1") }
                    }
                }
            }
        };
    }

    private static TrancheDto Note(string name, double balance, int subOrder) => new()
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

    private static TrancheDto Strip(string name, string trancheType, string couponType, int subOrder) => new()
    {
        TrancheName = name,
        OriginalBalance = PoolBalance, // the pool-sized notional face, as #22 expects
        TrancheType = trancheType,
        CashflowType = "IO",
        CouponType = couponType,
        FixedCoupon = 0.0,
        ClassReference = name,
        SubordinationOrder = subOrder,
        FirstPayDate = FirstPayDate,
        PayFrequency = 12,
        PayDay = FirstPayDate.Day
    };

    private static PayableStructureDto Seq(params string[] tranches) => new()
    {
        Type = "SEQ",
        Tranches = tranches.ToList()
    };

    private static List<PeriodCashflowDto> BuildCollateral()
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        const double wac = 7.0;
        for (var i = 0; i < 24 && balance > 1.0; i++)
        {
            var interest = balance * wac / 100 / 12;
            var scheduled = i == 23 ? balance : balance * 0.03;
            var endBalance = balance - scheduled;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1,
                CashflowDate = FirstPayDate.AddMonths(i),
                GroupNum = "1",
                BeginBalance = balance,
                Balance = endBalance,
                ScheduledPrincipal = scheduled,
                Interest = interest,
                NetInterest = interest,
                DelinqBalance = balance * DelinquentPct,
                Wac = wac
            });
            balance = endBalance;
        }

        return periods;
    }
}
