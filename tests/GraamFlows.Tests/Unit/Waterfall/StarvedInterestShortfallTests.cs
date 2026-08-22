using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
///     A class that receives NO interest in a period must still book that period's
///     coupon as a shortfall (#58).
///
///     The interest sweep only recorded a shortfall from inside
///     <c>DynamicTranche.PayInterest</c>, and skipped that call when no funds reached
///     the class — so a PARTIALLY paid class booked its shortfall while a class paid
///     nothing booked none. Its row kept the <see cref="TrancheCashflow" /> defaults:
///     Coupon 0, InterestShortfall 0, and AccumInterestShortfall frozen while the
///     class was still outstanding and unpaid.
///
///     Booking the accrual moves no cash, so every one of these tests also asserts
///     that what the classes actually received is unchanged.
/// </summary>
public class StarvedInterestShortfallTests
{
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    private const double SeniorBal = 95_000_000;
    private const double JuniorBal = 5_000_000;
    private const double JuniorCoupon = 6.0;

    /// <summary>
    ///     Pool WAC (3%) is far below the senior's coupon (5%), so the senior absorbs
    ///     every dollar of interest and the junior receives exactly nothing.
    /// </summary>
    private static CollateralCashflows StarvingCollateral(int periods = 6) =>
        new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(FirstPayDate, periods, 100_000_000, cpr: 0, cdr: 0, wac: 3.0)
            .Build();

    [Fact]
    public void JuniorReceivesNothing_BooksFullCouponAsShortfall()
    {
        var collateral = StarvingCollateral();
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", SeniorBal, 5.0, subOrder: 0)
            .WithTranche("B", JuniorBal, JuniorCoupon, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(collateral);

        var junior = Ordered(cf, "B");

        junior.Should().NotBeEmpty();
        junior.Should().OnlyContain(c => c.Interest == 0,
            "the senior absorbs all interest — this test is only meaningful while B is starved");

        foreach (var c in junior)
            c.InterestShortfall.Should().BeApproximately(c.BeginBalance * JuniorCoupon / 100 / 12, 0.01,
                "a starved class accrues its full coupon");
    }

    [Fact]
    public void StarvedClass_AccumulatesShortfallEveryPeriod()
    {
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", SeniorBal, 5.0, subOrder: 0)
            .WithTranche("B", JuniorBal, JuniorCoupon, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(StarvingCollateral());

        var accum = Ordered(cf, "B").Select(c => c.AccumInterestShortfall).ToList();

        accum.Should().HaveCountGreaterThan(1);
        accum.Should().BeInAscendingOrder("an unpaid class's accumulated shortfall must keep growing, not freeze");
        accum.Zip(accum.Skip(1)).Should().OnlyContain(p => p.Second > p.First);
    }

    [Fact]
    public void StarvedClass_ReportsItsStatedCoupon_NotZero()
    {
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", SeniorBal, 5.0, subOrder: 0)
            .WithTranche("B", JuniorBal, JuniorCoupon, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(StarvingCollateral());

        var junior = Ordered(cf, "B");

        junior.Should().OnlyContain(c => c.Coupon == JuniorCoupon,
            "the tape must show the coupon the class is owed; a starved 6% bond is not a 0% bond");
        junior.Should().OnlyContain(c => c.EffectiveCoupon == 0,
            "the EFFECTIVE coupon is what was actually paid — zero");
    }

    [Fact]
    public void NoInterestAtAll_EveryLevelBooksItsShortfall()
    {
        // Not one dollar of interest reaches the waterfall, so the sweep runs out of
        // funds at the very first payable. Every class below it — the senior included
        // — must still accrue. This is the case the sequential walk used to break on
        // before visiting anyone.
        var collateral = new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithPeriod(FirstPayDate, 100_000_000, scheduledPrincipal: 0, unscheduledPrincipal: 0, interest: 0)
            .WithPeriod(FirstPayDate.AddMonths(1), 100_000_000, scheduledPrincipal: 0, unscheduledPrincipal: 0, interest: 0)
            .Build();

        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", SeniorBal, 5.0, subOrder: 0)
            .WithTranche("B", JuniorBal, JuniorCoupon, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(collateral);

        foreach (var (name, coupon) in new[] { ("A", 5.0), ("B", JuniorCoupon) })
        {
            var rows = Ordered(cf, name);
            rows.Should().NotBeEmpty();
            rows.Should().OnlyContain(c => c.Interest == 0);
            foreach (var c in rows)
                c.InterestShortfall.Should().BeApproximately(c.BeginBalance * coupon / 100 / 12, 0.01,
                    $"{name} accrued its coupon even though the waterfall was dry");
        }
    }

    [Fact]
    public void StarvedExcessInterestStrip_BooksNoShortfall()
    {
        // XS sweeps whatever is left; it carries no stated coupon, so a period with
        // nothing left to sweep is not a shortfall.
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", SeniorBal, 5.0, subOrder: 0)
            .WithTranche("B", JuniorBal, JuniorCoupon, subOrder: 1)
            .WithTranche("XS", 0, 0.0, subOrder: 2, cashflowType: "IO", couponType: "ResidualInterest")
            .WithSequentialWaterfall("A", "B", "XS")
            .BuildAndRun(StarvingCollateral());

        var xs = Ordered(cf, "XS");

        xs.Should().NotBeEmpty();
        xs.Should().OnlyContain(c => c.InterestShortfall == 0, "an excess-spread strip has no coupon to fall short of");
        xs.Should().OnlyContain(c => c.AccumInterestShortfall == 0);
    }

    [Fact]
    public void BookingUnpaidAccrual_DistributesNoExtraCash()
    {
        // The accrual is bookkeeping. What the classes actually RECEIVE must still be
        // bounded by — and here equal to — the interest the collateral produced.
        var collateral = StarvingCollateral();
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", SeniorBal, 5.0, subOrder: 0)
            .WithTranche("B", JuniorBal, JuniorCoupon, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(collateral);

        var distributed = Ordered(cf, "A").Sum(c => c.Interest) + Ordered(cf, "B").Sum(c => c.Interest);
        var available = collateral.PeriodCashflows.Sum(p => p.NetInterest);

        distributed.Should().BeApproximately(available, 0.01,
            "every available dollar is paid out and not one more — the shortfall booking is cash-neutral");
    }

    [Fact]
    public void RetiredClass_BooksNoShortfall()
    {
        // A class with no face left accrues nothing, so its row must stay untouched —
        // no phantom shortfall on a bond that has already been paid off.
        var collateral = new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(FirstPayDate, 8, 100_000_000, cpr: 99, cdr: 0, wac: 3.0)
            .Build();

        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 1_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 99_000_000, JuniorCoupon, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(collateral);

        var retired = Ordered(cf, "A").Where(c => c.BeginBalance == 0).ToList();

        retired.Should().NotBeEmpty("A pays off early under 99 CPR — otherwise this test proves nothing");
        retired.Should().OnlyContain(c => c.InterestShortfall == 0);
    }

    private static List<TrancheCashflow> Ordered(DealCashflows dealCashflows, string trancheName)
    {
        var match = dealCashflows.TrancheCashflows.FirstOrDefault(t => t.Key.TrancheName == trancheName);
        return match.Value?.Cashflows.OrderBy(c => c.Key).Select(c => c.Value).ToList()
               ?? new List<TrancheCashflow>();
    }
}
