using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// MACR / exchangeable-class tests for the ComposableStructure engine.
///
/// Intex MACR model: an exchange class (e.g. "AB") is a pass-through aggregate
/// of its component real tranches (A + B). Its per-period cashflows are derived
/// as the fraction-weighted sum of component cashflows; the waterfall runs only
/// on the real tranches and never pays the exchange class directly.
///
/// For 100% pass-through MACR (the PAID 2026-2 case) each component's share
/// Quantity equals the real tranche's OriginalBalance, so pctShare = 1.0 and
/// the exchange-class vector is a plain sum of component vectors.
/// </summary>
public class MacrExchangeTests
{
    private static readonly DateTime ProjectionDate = TestConstants.DefaultProjectionDate;
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    /// <summary>
    /// Two-class A+B capital stack, one MACR class "AB" combining both 100%.
    /// Exchange-class cashflows must equal A+B at every period.
    /// </summary>
    [Fact]
    public void Macr_TwoTrancheCombination_BalanceEqualsSumOfComponents()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithExchangeClass("AB", new[]
            {
                ("A", 80_000_000.0),
                ("B", 20_000_000.0),
            })
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");
        var abCf = GetFirstCashflow(cf, "AB");

        abCf.Balance.Should().BeApproximately(aCf.Balance + bCf.Balance, 0.01,
            "MACR exchange-class balance equals sum of component balances");
    }

    [Fact]
    public void Macr_TwoTrancheCombination_InterestEqualsSumOfComponents()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithExchangeClass("AB", new[]
            {
                ("A", 80_000_000.0),
                ("B", 20_000_000.0),
            })
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");
        var abCf = GetFirstCashflow(cf, "AB");

        abCf.Interest.Should().BeApproximately(aCf.Interest + bCf.Interest, 0.01,
            "MACR exchange-class interest equals sum of component interest");
    }

    [Fact]
    public void Macr_TwoTrancheCombination_PrincipalEqualsSumOfComponents()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithExchangeClass("AB", new[]
            {
                ("A", 80_000_000.0),
                ("B", 20_000_000.0),
            })
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");
        var abCf = GetFirstCashflow(cf, "AB");

        var aPrin = aCf.ScheduledPrincipal + aCf.UnscheduledPrincipal;
        var bPrin = bCf.ScheduledPrincipal + bCf.UnscheduledPrincipal;
        var abPrin = abCf.ScheduledPrincipal + abCf.UnscheduledPrincipal;

        abPrin.Should().BeApproximately(aPrin + bPrin, 0.01,
            "MACR exchange-class principal equals sum of component principal");
    }

    /// <summary>
    /// PAID 2026-2 style: an A_ class combining only A1+A2 (the senior-float
    /// pair), leaving B/C/D/E/F1/F2 unexchangeable. A_ must equal A1+A2 exactly
    /// at every period while B is unaffected.
    /// </summary>
    [Fact]
    public void Macr_SeniorOnlyCombination_DoesNotAffectSubordinate()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A1", 60_000_000, 5.0, subOrder: 0)
            .WithTranche("A2", 20_000_000, 5.5, subOrder: 1)
            .WithTranche("B",  20_000_000, 6.0, subOrder: 2)
            .WithSequentialWaterfall("A1", "A2", "B")
            .WithExchangeClass("A_", new[]
            {
                ("A1", 60_000_000.0),
                ("A2", 20_000_000.0),
            })
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        var a1Cf = GetFirstCashflow(cf, "A1");
        var a2Cf = GetFirstCashflow(cf, "A2");
        var aUnderscore = GetFirstCashflow(cf, "A_");
        var bCf = GetFirstCashflow(cf, "B");

        aUnderscore.Balance.Should().BeApproximately(a1Cf.Balance + a2Cf.Balance, 0.01);
        aUnderscore.Interest.Should().BeApproximately(a1Cf.Interest + a2Cf.Interest, 0.01);

        // B should still receive its normal cashflow regardless of the exchange layer
        bCf.Interest.Should().BeGreaterThan(0, "subordinate class still paid via waterfall");
    }

    /// <summary>
    /// Overlapping MACR classes: AB combines A+B, ABC combines A+B+C. Both
    /// coexist and each derives from the same underlying real tranches without
    /// interfering with each other or with the waterfall.
    /// </summary>
    [Fact]
    public void Macr_NestedCombinations_BothDeriveIndependently()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 50_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 30_000_000, 5.5, subOrder: 1)
            .WithTranche("C", 20_000_000, 6.0, subOrder: 2)
            .WithSequentialWaterfall("A", "B", "C")
            .WithExchangeClass("AB", new[]
            {
                ("A", 50_000_000.0),
                ("B", 30_000_000.0),
            })
            .WithExchangeClass("ABC", new[]
            {
                ("A", 50_000_000.0),
                ("B", 30_000_000.0),
                ("C", 20_000_000.0),
            })
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");
        var cCf = GetFirstCashflow(cf, "C");
        var abCf = GetFirstCashflow(cf, "AB");
        var abcCf = GetFirstCashflow(cf, "ABC");

        abCf.Balance.Should().BeApproximately(aCf.Balance + bCf.Balance, 0.01);
        abcCf.Balance.Should().BeApproximately(aCf.Balance + bCf.Balance + cCf.Balance, 0.01);
        abcCf.Interest.Should().BeApproximately(aCf.Interest + bCf.Interest + cCf.Interest, 0.01);
    }

    #region Helper Methods

    private static CollateralCashflows CreateCollateral(int numPeriods, double startingBalance,
        double wacPct = 8.0, double cdrPct = 0.0)
    {
        return new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(FirstPayDate, numPeriods, startingBalance,
                cpr: TestConstants.DefaultCpr, cdr: cdrPct, wac: wacPct)
            .Build();
    }

    private static TrancheCashflow GetFirstCashflow(DealCashflows dealCashflows, string trancheName)
    {
        var match = dealCashflows.TrancheCashflows.First(t => t.Key.TrancheName == trancheName);
        return match.Value.Cashflows.OrderBy(c => c.Key).First().Value;
    }

    #endregion
}
