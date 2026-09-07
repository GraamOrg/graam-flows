using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// graam-harmony#4883 — a FUNDED first-loss class must absorb the writedown, even when it
/// carries a residual/excess coupon because it sweeps what is left on the interest side.
///
/// `WritedownCapacity` returned 0 for any class whose COUPON is ExcessInterest or Residual.
/// Its justification is about the BALANCE, not the coupon: such a class "has no principal to
/// write down — its notional balance is reset to the pool each period". That reset is
/// `InitNotionalBalances` / `SettleNotionalBalances`, and those key on
/// `CashflowType.InterestOnly` — NOT on the coupon type. So the rule fired on classes the
/// reset never touches.
///
/// A CLO's Subordinated Notes are exactly that class: `cashflowType: PI`, a real fixed
/// `originalBalance`, never reset, first-loss — with a residual coupon only because it sweeps
/// residual interest. Zeroing its capacity sent the whole pool loss cascading PAST the equity
/// onto the most junior rated bond. Measured on AMMC CLO 33 at 2 CDR / 20 CPR / 40 severity:
/// Class E (BB-, 15,000,000) took a 69.5% writedown while 38,420,000 of equity below it took
/// nothing and was paid 199.7% of its face.
///
/// The fix requires BOTH a notional-strip coupon AND a balance that is actually a notional.
/// The last test is the guardrail: a conventionally-declared XS strip is InterestOnly and
/// keeps capacity 0, so the writedown still cascades past it to the funded bonds.
/// </summary>
public class FundedResidualWritedownTests
{
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    /// <summary>A pool that takes a real loss, so there is a writedown to allocate.</summary>
    private static CollateralCashflows LossyPool()
    {
        var builder = new TestCollateralBuilder().WithGroupNum("1");
        builder.WithPeriod(date: FirstPayDate, beginBalance: 100_000_000,
            scheduledPrincipal: 0, unscheduledPrincipal: 0, interest: 600_000,
            defaultedPrincipal: 10_000_000, recoveryPrincipal: 4_000_000);
        for (var i = 1; i <= 5; i++)
            builder.WithPeriod(date: FirstPayDate.AddMonths(i), beginBalance: 90_000_000,
                scheduledPrincipal: 0, unscheduledPrincipal: 0, interest: 600_000);
        return builder.Build();
    }

    private static Dictionary<string, double> CumWritedownByTranche(string juniorCouponType,
        string juniorCashflowType)
    {
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 60_000_000, 6.0, subOrder: 0)
            // The funded first-loss piece: a real balance, PI (or IO for the guardrail),
            // carrying the residual/excess coupon it sweeps interest with.
            .WithTranche("EQ", 40_000_000, 0.0, subOrder: 1,
                cashflowType: juniorCashflowType, couponType: juniorCouponType)
            .WithSequentialWaterfall("A", "EQ")
            .BuildAndRun(LossyPool());

        var result = new Dictionary<string, double>();
        foreach (var (key, streams) in cf.TrancheCashflows)
            result[key.TrancheName] = streams.Cashflows.Values.Sum(c => c.Writedown);
        return result;
    }

    [Theory]
    [InlineData("Residual")]
    [InlineData("ExcessInterest")]
    public void AFundedFirstLossClassAbsorbsTheWritedown(string couponType)
    {
        var wd = CumWritedownByTranche(couponType, juniorCashflowType: "PI");

        // The equity is funded (PI, a real balance), so it takes the loss...
        wd["EQ"].Should().BeGreaterThan(0,
            "a PI class with a real original balance is funded, whatever its coupon sweeps");
        // ...and the senior bond above it is untouched.
        wd["A"].Should().Be(0,
            "the loss must not cascade past a funded first-loss class onto a senior bond");
    }

    [Theory]
    [InlineData("Residual")]
    [InlineData("ExcessInterest")]
    public void ANotionalStripStillCascadesPastItself(string couponType)
    {
        // The guardrail. A conventionally-declared strip is InterestOnly: its balance IS a
        // pool notional, reset every period, so a writedown against it is a no-op and must
        // flow past it to the funded bonds. Unchanged by the fix — without this, keying on
        // the balance instead of the coupon would silently start consuming allocations
        // against a notional.
        var wd = CumWritedownByTranche(couponType, juniorCashflowType: "IO");

        wd["EQ"].Should().Be(0, "an InterestOnly strip's balance is a notional, not principal");
        wd["A"].Should().BeGreaterThan(0, "the allocation must cascade to the funded bond");
    }
}
