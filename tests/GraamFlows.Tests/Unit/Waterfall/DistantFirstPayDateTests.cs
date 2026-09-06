using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// #4848 — a deal whose first Payment Date is MONTHS after settlement.
///
/// Routine in CLOs, which commonly skip a quarter for the ramp: AMMC CLO 33 closes
/// 2025-12-23 and first pays 2026-07-20. The API maps FirstSettleDate from the deal's
/// ClosingDate, so the tranche accrues ~7 months to its first payment while the
/// waterfall distributes ONE collateral period's cash at that date. The resulting
/// shortfall is then repaid out of every later period, and the senior class ends up
/// taking everything.
///
/// Measured live on AMMC (projection 2026-01-01, 405.42M pool, 95,082,307 of collateral
/// interest), sweeping only the first pay date:
///
///   first pay    class A       juniors     A paid after payoff
///   2026-01-20   24,038,599    71,043,707            0     correct
///   2026-02-20   26,651,822    68,430,485      482,942     leaking
///   2026-03-20   94,563,791       518,516   17,628,797     collapsing
///   2026-04-20   95,082,307             0   17,628,797     total
///
/// No fixture could express this before: TestDealBuilder pinned FirstSettleDate to
/// exactly one month before the first pay date, so the gap was always one period.
/// </summary>
public class DistantFirstPayDateTests
{
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    /// <summary>Settlement six months before the first payment — the CLO ramp shape.</summary>
    private static (IDeal, DealCashflows) RunWithDistantFirstPay()
    {
        // The real shape: the pool is projected from the projection date, and the notes
        // first pay SIX MONTHS later. Both halves matter — moving settlement back while
        // the first pay date stays one period out does NOT reproduce this.
        var projection = TestConstants.DefaultProjectionDate;
        var distantFirstPay = projection.AddMonths(6);

        var collateral = new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(projection, 60, 100_000_000, cpr: 6.0, cdr: 0.0, wac: 8.0)
            .Build();

        return new TestDealBuilder(projectionDate: projection)
            .WithTranche("A", 60_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 7.0, subOrder: 1)
            .WithTranche("C", 20_000_000, 9.0, subOrder: 2)
            .WithPayFrequency(12, firstPayDate: distantFirstPay)
            .WithFirstSettleDate(projection.AddDays(-9))   // the deal's closing date
            .WithSequentialWaterfall("A", "B", "C")
            .BuildAndRun(collateral);
    }

    [Fact]
    public void ASeniorClassIsNotPaidInterestAfterItsBalanceReachesZero()
    {
        var (_, cf) = RunWithDistantFirstPay();
        var rows = GetCashflows(cf, "A").OrderBy(kv => kv.Key).ToList();

        var paidAfterPayoff = rows
            .SkipWhile(kv => kv.Value.BeginBalance > 1.0)
            .Sum(kv => kv.Value.Interest);

        paidAfterPayoff.Should().BeApproximately(0, 0.01,
            "a retired PI class has no balance to accrue on — interest after payoff is "
            + "cash the junior classes were entitled to");
    }

    [Fact]
    public void TheJuniorClassesAreNotStarvedByTheLongFirstAccrual()
    {
        var (_, cf) = RunWithDistantFirstPay();

        var junior = GetCashflows(cf, "B").Sum(kv => kv.Value.Interest)
                     + GetCashflows(cf, "C").Sum(kv => kv.Value.Interest);

        junior.Should().BeGreaterThan(0,
            "the pool earns far more than the senior's coupon, so B and C must receive "
            + "interest — a long first accrual is a timing difference, not a claim on "
            + "every later period's cash");
    }

    [Fact]
    public void TheSeniorIsNotPaidMoreThanItEverAccrued()
    {
        // The honest ceiling: cumulative interest to a class cannot exceed what it
        // accrued (coupon on its outstanding balance) over the same periods.
        var (_, cf) = RunWithDistantFirstPay();
        var rows = GetCashflows(cf, "A").OrderBy(kv => kv.Key).ToList();

        var paid = rows.Sum(kv => kv.Value.Interest);
        var accrued = rows.Sum(kv => kv.Value.BeginBalance * 5.0 * .01 * (kv.Value.AccrualDays / 360.0));

        paid.Should().BeLessThanOrEqualTo(accrued + 1.0,
            $"paid {paid:N0} against {accrued:N0} accrued");
    }

    private static Dictionary<DateTime, TrancheCashflow> GetCashflows(DealCashflows cf, string tranche)
    {
        var match = cf.TrancheCashflows.FirstOrDefault(t => t.Key.TrancheName == tranche);
        return match.Value?.Cashflows ?? new Dictionary<DateTime, TrancheCashflow>();
    }
}
