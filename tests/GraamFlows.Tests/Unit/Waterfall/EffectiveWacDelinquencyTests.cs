using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// `eff_wac` must not depend on the delinquency assumption (graam-harmony #4481 §1.1).
///
/// Both structures used to compute
///
///     wac = 1200 * (Interest + UnAdvancedInterest - ServiceFee - expenses) / BeginBalance
///
/// and that `+ UnAdvancedInterest` was the exact algebraic inverse of the docking
/// the amortizer applied: interest arrived short by `interest * dq * (1 - adv)`
/// and this added it straight back, reconstructing the contractual net WAC. Once
/// §1.1 stopped docking, the add-back became uncompensated and inflated eff_wac
/// by `1 + dq * (1 - adv)` — measured at +113bp for dq=20/adv=0.
///
/// This is a CASH path, not disclosure: `EffectiveWac` is exposed to the rules
/// engine as `eff_wac` (RulesHost.cs) and is the net-WAC cap on tranche coupons —
/// `"MIN(4.006, eff_wac)"` in the COLT sample. An inflated cap silently un-caps
/// every tranche whose fixed rate sits inside the inflated band.
///
/// The collateral here is synthetic (built directly, not amortized), so
/// `UnAdvancedInterest` can be dialled independently of everything else — which
/// is exactly the property under test: no value of it may move eff_wac.
/// </summary>
public class EffectiveWacDelinquencyTests
{
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    private static CollateralCashflows Collateral(double unAdvancedInterest)
    {
        var collateral = new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(FirstPayDate, 3, 100_000_000,
                cpr: TestConstants.DefaultCpr, cdr: 0.0, wac: 8.0)
            .Build();

        foreach (var period in collateral.PeriodCashflows)
            period.UnAdvancedInterest = unAdvancedInterest;

        return collateral;
    }

    private static List<double> RunAndReadEffectiveWac(double unAdvancedInterest)
    {
        var collateral = Collateral(unAdvancedInterest);
        new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(collateral);

        return collateral.PeriodCashflows.Select(p => p.EffectiveWac).ToList();
    }

    [Theory]
    [InlineData(50_000.0)]
    [InlineData(250_000.0)]
    [InlineData(1_000_000.0)]
    public void EffectiveWac_IgnoresUnAdvancedInterest(double unAdvancedInterest)
    {
        var baseline = RunAndReadEffectiveWac(0.0);
        var withUnadvanced = RunAndReadEffectiveWac(unAdvancedInterest);

        baseline.Should().NotBeEmpty();
        baseline.Should().Contain(w => w > 0, "the fixture must actually produce a net WAC");

        withUnadvanced.Should().HaveCount(baseline.Count);
        for (var t = 0; t < baseline.Count; t++)
        {
            withUnadvanced[t].Should().BeApproximately(baseline[t], 1e-9,
                $"period {t}: eff_wac is the net-WAC CAP on tranche coupons, and unadvanced " +
                "interest is a servicer-advance disclosure item. Adding it back inflated the " +
                "cap by 1 + dq*(1-adv) once the amortizer stopped docking interest");
        }
    }
}
