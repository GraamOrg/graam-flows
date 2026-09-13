using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// #92, the second overload. <c>DynamicPseudoClass.CreditSupport(DateTime)</c> sums
/// <c>SubordinateClasses(int)</c>, which — unlike the tranche overload — has no PayFrom filter,
/// so before the fix EVERY pool-notional IO strip counted as subordination beneath a pseudo
/// class, not just one.
///
/// This lives at the builder level because the API does not map <c>DealStructurePseudo</c>: no
/// request can construct a pseudo class, and the API-level test beside this one
/// (<see cref="IoNotionalIsNotSubordinationTests" />) could not see this line. A mutation that
/// reverted it left that whole file green.
/// </summary>
public class PseudoClassCreditSupportExcludesIoNotionalTests
{
    private const double A1 = 60_000_000;
    private const double M1 = 25_000_000;
    private const double B1 = 15_000_000;
    private const double Pool = A1 + M1 + B1;
    private const int Periods = 6;

    [Fact]
    public void A_pseudo_class_credit_support_is_unchanged_by_adding_pool_notional_io_strips()
    {
        var without = PseudoCreditSupport(withIoStrips: false);
        var with = PseudoCreditSupport(withIoStrips: true);

        // Anti-vacuity, pinned to the funded arithmetic rather than a bound. After period 1 pays
        // A1 down by 16.67M, 40M (M1 + B1) sits beneath it out of 83.33M funded: 0.48. Later rows
        // legitimately rise to 1.0 once A1 retires, so a (0, 1) bound was the wrong guard.
        without.Should().HaveCount(Periods, "the pseudo class must record a row every period");
        without[0].Should().BeApproximately((M1 + B1) / (Pool - Pool / Periods), 1e-9,
            "period 1 credit support beneath A1 is the funded subordinate over the funded notes");

        with.Should().HaveCount(without.Count);
        for (var i = 0; i < without.Count; i++)
            with[i].Should().BeApproximately(without[i], 1e-9,
                $"row {i + 1}: a pool-sized IO notional is not subordination beneath a pseudo class");
    }

    private static List<double> PseudoCreditSupport(bool withIoStrips)
    {
        var builder = new TestDealBuilder()
            .WithTranche("A1", A1, 5.0, subOrder: 0)
            .WithTranche("M1", M1, 6.0, subOrder: 1)
            .WithTranche("B1", B1, 7.0, subOrder: 2);
        if (withIoStrips)
        {
            builder.WithExcessServicingStrip("AIOS", Pool, subOrder: 3)
                .WithTranche("XS", Pool, 0.0, subOrder: 4, cashflowType: "IO", couponType: "ExcessInterest");
        }

        var (_, cf) = builder
            .WithPseudoClass("SENIOR", "A1")
            .WithSequentialWaterfall("A1", "M1", "B1")
            .BuildAndRun(AmortizingPool());

        var pseudo = cf.ClassCashflows.FirstOrDefault(c => c.Key.TrancheName == "SENIOR");
        pseudo.Key.Should().NotBeNull("the pseudo class must be materialized");
        return pseudo.Value.Cashflows.OrderBy(c => c.Key).Select(c => c.Value.CreditSupport).ToList();
    }

    private static CollateralCashflows AmortizingPool()
    {
        var builder = new TestCollateralBuilder().WithGroupNum("1");
        var per = Pool / Periods;
        for (var i = 0; i < Periods; i++)
            builder.WithPeriod(date: new DateTime(2024, 2, 25).AddMonths(i),
                beginBalance: Pool - per * i,
                scheduledPrincipal: per, unscheduledPrincipal: 0, interest: 600_000);
        return builder.Build();
    }
}
