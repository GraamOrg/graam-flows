using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// A deal's cut-off date normally precedes its closing, so the FIRST Payment Date
/// distributes more than one period of collections. Whether the waterfall may spend
/// those pre-first-pay periods is a modelling choice, not a fact, and the two
/// defensible answers move money in opposite directions:
///
///   Fold — accumulate them into the first distribution. A senior class whose first
///          scheduled payment assumes the whole cut-off-to-closing window gets it, but
///          a residual/excess tranche can be flooded by pool interest its bonds never
///          accrued (harmony #1714).
///   Drop — exclude them. No flood, but a long first period starves the senior
///          (harmony #2454).
///
/// Fold is the default. It is what the engine did on every path where the two differ —
/// the re-timing described below bailed out for any deal whose PayFrequency was not 12,
/// so on quarterly and semi-annual deals the fold was live and was the shipped behaviour
/// — and it is the only one of the two that CONSERVES principal. Drop pays the excluded
/// stub to nobody and writes it down nowhere, so the pool balance falls while the bond
/// balance does not.
///
/// A third mechanism used to pre-empt both: AlignStubPeriodsToPaySchedule re-dated the
/// i-th collateral period onto the i-th pay date, unconditionally and BEFORE the fold,
/// so nothing was ever left before the boundary and Fold and Drop were indistinguishable.
/// It was added for harmony #2748 and validated against Intex on the FIRST distribution
/// only — which Drop satisfies equally, since both give a one-month first distribution.
/// What it additionally did was push the whole schedule out by the stub length, which is
/// what broke the STACR 2025-DNA1 tie-out. It has been removed; the engine no longer
/// rewrites collateral dates.
///
/// Before this knob existed the boundary was derived from whichever tranche happened
/// to be FIRST in the deal's list, snapped to the 1st of its month — incidental, not
/// stated — and callers expressed a collateral intent by moving the PROJECTION DATE,
/// which is a different variable.
///
/// These tests pin all three behaviours and, most importantly, that omitting the knob
/// changes nothing.
/// </summary>
public class FirstPeriodCollateralPolicyTests
{
    private static readonly DateTime ProjectionDate = TestConstants.DefaultProjectionDate; // 2024-01-25
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;     // 2024-02-25

    /// <summary>
    /// Collateral whose period 0 lands BEFORE the first distribution — the pool was
    /// projected from the cut-off, so there is a stub period ahead of the first payment.
    /// </summary>
    private static CollateralCashflows CollateralStartingBeforeFirstPay(int numPeriods = 4) =>
        new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(ProjectionDate, numPeriods, 100_000_000,
                cpr: TestConstants.DefaultCpr, cdr: 0.0, wac: 8.0)
            .Build();

    private static (IDeal Deal, DealCashflows Cf) Run(string? policy, DateTime? accrualStart = null) =>
        new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithFirstPeriodCollateral(policy, accrualStart)
            .BuildAndRun(CollateralStartingBeforeFirstPay());

    private static IEnumerable<TrancheCashflow> Rows(DealCashflows cf, string tranche)
    {
        var match = cf.TrancheCashflows.FirstOrDefault(t => t.Key.TrancheName == tranche);
        return (match.Value?.Cashflows ?? new Dictionary<DateTime, TrancheCashflow>())
            .OrderBy(c => c.Key).Select(c => c.Value);
    }

    private static double FirstPrincipal(DealCashflows cf, string tranche) =>
        Rows(cf, tranche)
            .Where(r => r.ScheduledPrincipal + r.UnscheduledPrincipal > 0)
            .Select(r => r.ScheduledPrincipal + r.UnscheduledPrincipal)
            .First();

    private static double TotalPrincipal(DealCashflows cf, string tranche) =>
        Rows(cf, tranche).Sum(r => r.ScheduledPrincipal + r.UnscheduledPrincipal);

    // --- the default must not move ------------------------------------------------

    [Fact]
    public void OmittingThePolicy_IsAlign()
    {
        var (deal, _) = Run(null);
        deal.FirstPeriodCollateralPolicyEnum.Should().Be(FirstPeriodCollateralPolicyEnum.Align,
            "Align is what the engine has done since #2748, and the only policy that both "
            + "conserves principal and holds the one-collateral-month ceiling");
    }

    [Fact]
    public void OmittingThePolicy_ProducesIdenticalCashflowsToExplicitAlign()
    {
        var (_, implicitCf) = Run(null);
        var (_, explicitCf) = Run("Align");

        foreach (var tranche in new[] { "A", "B" })
            TotalPrincipal(implicitCf, tranche).Should()
                .BeApproximately(TotalPrincipal(explicitCf, tranche), 0.01);

        // Only A amortises over this fixture's four periods — B is still full, so it has
        // no first paying period to compare.
        FirstPrincipal(implicitCf, "A").Should()
            .BeApproximately(FirstPrincipal(explicitCf, "A"), 0.01);
    }

    [Fact]
    public void OmittingTheAccrualStart_KeepsTheDerivedBoundary()
    {
        // The derived boundary is first-of-month of the first tranche's FirstPayDate.
        var (deal, _) = Run(null);
        deal.CollateralAccrualStartDate.Should().BeNull();
        new DateTime(FirstPayDate.Year, FirstPayDate.Month, 1).Should().Be(new DateTime(2024, 2, 1));
    }

    // --- Fold vs Drop -------------------------------------------------------------

    [Fact]
    public void Fold_SpendsThePreFirstPayPeriodInTheFirstDistribution()
    {
        var (_, fold) = Run("Fold");
        var (_, drop) = Run("Drop");

        FirstPrincipal(fold, "A").Should().BeGreaterThan(FirstPrincipal(drop, "A"),
            "folding adds the stub period's principal to the first distribution");

        // On STACR 2025-DNA1 this is the entire Class A-1 divergence from Payscen:
        // Drop/Align give 17,789,097.37, Fold and Payscen both give 20,662,500.00.
    }

    [Fact]
    public void Drop_ExcludesThePreFirstPayPeriodEntirely()
    {
        var (_, fold) = Run("Fold");
        var (_, drop) = Run("Drop");

        var foldTotal = TotalPrincipal(fold, "A") + TotalPrincipal(fold, "B");
        var dropTotal = TotalPrincipal(drop, "A") + TotalPrincipal(drop, "B");

        dropTotal.Should().BeLessThan(foldTotal,
            "the dropped period's principal never reaches a distribution");
    }

    [Fact]
    public void Drop_LosesExactlyTheStubPeriod()
    {
        var collateral = CollateralStartingBeforeFirstPay();
        var stub = collateral.PeriodCashflows
            .Where(p => p.CashflowDate < new DateTime(2024, 2, 1))
            .Sum(p => p.ScheduledPrincipal + p.UnscheduledPrincipal);
        stub.Should().BeGreaterThan(0, "the fixture must actually have a pre-first-pay period");

        var (_, fold) = Run("Fold");
        var (_, drop) = Run("Drop");
        var delta = (TotalPrincipal(fold, "A") + TotalPrincipal(fold, "B"))
                    - (TotalPrincipal(drop, "A") + TotalPrincipal(drop, "B"));

        delta.Should().BeApproximately(stub, 1.0,
            "the difference between the two policies is exactly the stub, not a re-timing");
    }

    // --- the explicit boundary ----------------------------------------------------

    [Fact]
    public void AccrualStartDate_OverridesTheDerivedBoundary()
    {
        // Push the boundary past every collateral period, so under Fold they ALL
        // accumulate and none is distributed on its own period.
        var (_, derived) = Run("Fold");
        var (_, moved) = Run("Fold", new DateTime(2024, 3, 1));

        FirstPrincipal(moved, "A").Should().BeGreaterThan(FirstPrincipal(derived, "A"),
            "a later boundary folds MORE periods into the first distribution");
    }

    [Fact]
    public void AccrualStartDate_IsIndependentOfThePolicy()
    {
        var (deal, _) = Run("Drop", new DateTime(2024, 3, 1));
        deal.CollateralAccrualStartDate.Should().Be(new DateTime(2024, 3, 1));
        deal.FirstPeriodCollateralPolicyEnum.Should().Be(FirstPeriodCollateralPolicyEnum.Drop);
    }

    // --- an unknown policy must not be guessed ------------------------------------

    [Theory]
    [InlineData("Stub")]     // proposed, deliberately NOT implemented yet
    [InlineData("nonsense")]
    public void UnrecognisedPolicy_Throws(string policy)
    {
        var deal = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithSequentialWaterfall("A")
            .WithFirstPeriodCollateral(policy)
            .Build();

        var act = () => deal.FirstPeriodCollateralPolicyEnum;

        act.Should().Throw<ArgumentException>()
            .WithMessage("*not recognised*",
                "the two policies move money in opposite directions, so silently picking "
                + "one would mis-state a deal without failing");
    }

    [Theory]
    [InlineData("fold")]
    [InlineData("FOLD")]
    [InlineData("drop")]
    [InlineData(" Fold ")]  // whitespace is formatting, not a different policy
    public void PolicyParsing_IsCaseInsensitive(string policy)
    {
        var deal = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithSequentialWaterfall("A")
            .WithFirstPeriodCollateral(policy)
            .Build();

        var act = () => deal.FirstPeriodCollateralPolicyEnum;
        act.Should().NotThrow();
    }

    // --- principal conservation, measured against the POOL -----------------------
    //
    // The gap that let a principal-destroying default ship. Every other assertion here
    // compares the two policies to EACH OTHER, or checks a first-period ceiling; none
    // asked whether the money that left the pool reached a bond. It did not: Drop
    // excludes the stub's principal, pays it to nobody and writes it down nowhere, so
    // the stack ends permanently under-collateralized by exactly that amount — and the
    // classes that never amortized keep accruing on principal the pool no longer holds,
    // so over the deal's life Drop pays MORE interest, not less.

    private static (IDeal Deal, DealCashflows Cf) RunToExhaustion(
        string? policy, int numPeriods = 400)
    {
        var collateral = CollateralStartingBeforeFirstPay(numPeriods);
        return new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithFirstPeriodCollateral(policy)
            .BuildAndRun(collateral);
    }

    private static double PoolPrincipal(CollateralCashflows c) =>
        c.PeriodCashflows.Sum(p => p.ScheduledPrincipal + p.UnscheduledPrincipal);

    [Fact]
    public void TheDefault_DeliversEveryDollarThePoolPaid()
    {
        var collateral = CollateralStartingBeforeFirstPay(400);
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(collateral);

        var toBonds = TotalPrincipal(cf, "A") + TotalPrincipal(cf, "B");
        var fromPool = PoolPrincipal(collateral);

        // Two-sided. `BeLessOrEqualTo` is what the existing conservation test uses, and
        // it is exactly what cannot see money going missing.
        toBonds.Should().BeApproximately(Math.Min(fromPool, 100_000_000), 1.0,
            "principal that leaves the pool must reach a bond or be written down — under "
            + "the default it may not simply disappear");
    }

    [Fact]
    public void Drop_IsTheOneThatLosesPrincipal_AndSaysSo()
    {
        var collateral = CollateralStartingBeforeFirstPay(400);
        var stub = collateral.PeriodCashflows
            .Where(p => p.CashflowDate < new DateTime(2024, 2, 1))
            .Sum(p => p.ScheduledPrincipal + p.UnscheduledPrincipal);
        stub.Should().BeGreaterThan(0);

        var (_, fold) = RunToExhaustion("Fold");
        var (_, drop) = RunToExhaustion("Drop");

        var foldTotal = TotalPrincipal(fold, "A") + TotalPrincipal(fold, "B");
        var dropTotal = TotalPrincipal(drop, "A") + TotalPrincipal(drop, "B");

        (foldTotal - dropTotal).Should().BeApproximately(stub, 1.0,
            "the shortfall is exactly the excluded stub — pinned so that anyone making "
            + "Drop the default has to delete this line first");
    }

    [Fact]
    public void ANonMonthlyDeal_KeepsItsPrincipalUnderTheDefault()
    {
        // The re-timing that used to precede the fold bailed out on PayFrequency != 12,
        // so quarterly and semi-annual deals took the FOLD branch and it was never dead
        // code for them. A default that excluded the stub would take ~7.5% of a quarterly
        // pool without the caller asking. The CLO work is quarterly-pay.
        var deal = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithPayFrequency(4, new DateTime(2024, 4, 25))
            .Build();

        deal.Tranches.First().PayFrequency.Should().Be(4);
        // The default is Align, and Align is monthly-only — so a quarterly deal falls
        // through to the fold, which is exactly what the engine did for it before the
        // policy existed. The bug this pins is a default that EXCLUDES the stub: that
        // would take ~7.5% of a quarterly pool without the caller asking, and the CLO
        // work is quarterly-pay.
        deal.FirstPeriodCollateralPolicyEnum.Should().NotBe(FirstPeriodCollateralPolicyEnum.Drop,
            "no deal may lose principal by default, least of all one the re-timing never "
            + "touched");
    }
}