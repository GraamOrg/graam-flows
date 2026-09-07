using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// #4860 — repaying an interest shortfall must REDUCE the accumulator.
///
/// `DynamicClass.PayInterest` sizes a class's ask as `this period's accrual +
/// AccumInterestShortfall`, so anything paid above the accrual IS a repayment of the
/// carried shortfall. `DynamicTranche.PayInterest(..., interest)` booked the new
/// shortfall but never booked the repayment, so the accumulator only ever grew.
///
/// Two consequences, both measured on the real AMMC CLO 33 deal:
///   * a class written down to its FULL face keeps drawing its frozen shortfall for the
///     rest of the deal — class C took 9,445,414 over 51 periods on a ZERO balance and
///     D1 931,220, together 13% of the deal's interest going to classes that no longer
///     existed;
///   * any class that recovers from a shortfall is paid that shortfall again every
///     period the cash allows.
/// </summary>
public class ShortfallRepaymentTests
{
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    /// <summary>One lean period that starves B, then fat periods that can repay it.</summary>
    private static CollateralCashflows LeanThenFat()
    {
        var builder = new TestCollateralBuilder().WithGroupNum("1");
        // Period 1: interest covers A's coupon only — B is starved and books a shortfall.
        builder.WithPeriod(date: FirstPayDate, beginBalance: 100_000_000, scheduledPrincipal: 0,
            unscheduledPrincipal: 0, interest: 300_000);
        // Periods 2-6: plenty for both coupons AND the carried shortfall.
        for (var i = 1; i <= 5; i++)
            builder.WithPeriod(date: FirstPayDate.AddMonths(i), beginBalance: 100_000_000,
                scheduledPrincipal: 0, unscheduledPrincipal: 0, interest: 1_500_000);
        return builder.Build();
    }

    private static Dictionary<DateTime, TrancheCashflow> Run(string tranche)
    {
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 60_000_000, 6.0, subOrder: 0)   // ~300,000/mo — eats period 1
            .WithTranche("B", 20_000_000, 9.0, subOrder: 1)   // ~150,000/mo — starved in period 1
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(LeanThenFat());
        var match = cf.TrancheCashflows.FirstOrDefault(t => t.Key.TrancheName == tranche);
        return match.Value?.Cashflows ?? new Dictionary<DateTime, TrancheCashflow>();
    }

    [Fact]
    public void TheAccumulatorFallsWhenTheShortfallIsRepaid()
    {
        var rows = Run("B").OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

        rows[0].AccumInterestShortfall.Should().BeGreaterThan(0,
            "B is starved in the lean period, so it must book a shortfall");

        var opening = rows[0].AccumInterestShortfall;
        var closing = rows[^1].AccumInterestShortfall;
        closing.Should().BeLessThan(opening,
            "the fat periods pay B above its coupon, which repays the carried shortfall — "
            + "an accumulator that never falls is paid out again every period");
    }

    [Fact]
    public void AShortfallIsNotPaidTwice()
    {
        // The honest ceiling: over the whole run B cannot receive more than the coupon it
        // accrued across those periods. Repaying a shortfall re-times interest; it does not
        // create any.
        var rows = Run("B").OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

        var paid = rows.Sum(r => r.Interest);
        var accrued = rows.Sum(r => r.BeginBalance * 9.0 * .01 * (r.AccrualDays / 360.0));

        paid.Should().BeLessThanOrEqualTo(accrued + 1.0,
            $"B was paid {paid:N2} against {accrued:N2} of accrued coupon");
    }
}
