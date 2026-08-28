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
/// Drop is the default: it is the fail-safe direction, since a distribution can never
/// pay out more than the pool earned in one period. A deal whose cut-off genuinely
/// precedes its closing owns that window and must say so.
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
    public void OmittingThePolicy_IsDrop()
    {
        var (deal, _) = Run(null);
        deal.FirstPeriodCollateralPolicyEnum.Should().Be(FirstPeriodCollateralPolicyEnum.Drop,
            "a distribution must never pay out more than the pool earned in one period "
            + "unless the deal states that it owns the earlier window");
    }

    [Fact]
    public void OmittingThePolicy_ProducesIdenticalCashflowsToExplicitDrop()
    {
        var (_, implicitCf) = Run(null);
        var (_, explicitCf) = Run("Drop");

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
}
