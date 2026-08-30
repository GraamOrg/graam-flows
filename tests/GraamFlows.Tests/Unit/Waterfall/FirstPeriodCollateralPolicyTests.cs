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
/// Align is the default. Drop pays the excluded stub to nobody and writes it down
/// nowhere, so the pool balance falls while the bond balance does not; Fold conserves but
/// lets a distribution receive more than one collateral month.
///
/// A third mechanism used to pre-empt both: AlignStubPeriodsToPaySchedule re-dated the
/// i-th collateral period onto the i-th pay date, unconditionally and BEFORE the fold,
/// so nothing was ever left before the boundary and Fold and Drop were indistinguishable.
/// It was added for harmony #2748, and it is the DEFAULT. Removing it was tried and
/// reverted: Fold conserves principal but fails #2748's own test (two collateral months
/// reach distribution 0), and Drop holds that ceiling but pays the excluded stub to
/// nobody. Align does both WHERE ITS RE-DATING APPLIES; where it does not — a
/// non-monthly deal, or a boundary later than first pay — it folds the remainder
/// and the output is byte-identical to Fold, ceiling and all. Stating Fold or Drop is what turns
/// the re-dating off — which STACR 2025-DNA1 does, because its first Reporting Period
/// genuinely spans two months and Appendix G prints the resulting payment.
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
            "Align is what the engine has done since #2748; it conserves principal on every "
            + "shape, and holds the one-collateral-month ceiling wherever its re-dating "
            + "applies");
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

    [Theory]
    [InlineData(4)]   // quarterly — the CLO work
    [InlineData(2)]   // semi-annual
    public void ANonMonthlyDeal_KeepsEveryDollarUnderTheDefault(int payFrequency)
    {
        // This RUNS the waterfall. The version that shipped asserted only that the enum
        // default was not Drop and never touched a pool — so it stayed green while the
        // default discarded 4,474,457.84 of a 100M quarterly pool, because Align is
        // monthly-only and the fold was gated on `== Fold`, which Align is not.
        var collateral = CollateralStartingBeforeFirstPay(400);
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithPayFrequency(payFrequency, new DateTime(2024, 4, 25))
            .BuildAndRun(collateral);

        var toBonds = TotalPrincipal(cf, "A") + TotalPrincipal(cf, "B");
        toBonds.Should().BeApproximately(Math.Min(PoolPrincipal(collateral), 100_000_000), 1.0,
            "a deal the re-timing cannot touch must still be paid every dollar the pool "
            + "produced — Align means re-date where possible and FOLD the rest, never drop");
    }

    [Fact]
    public void AStatedAccrualBoundary_DoesNotCostTheDealPrincipal()
    {
        // The same hole on MONTHLY deals, through the PR's own new field. The re-timing
        // lands periods on the first pay date, but the boundary can be stated LATER, so
        // periods survive re-dating and then fall before the boundary. A caller who states
        // only the boundary — and correctly omits the policy, which every doc comment says
        // is the safe thing to do — was losing that principal silently.
        var collateral = CollateralStartingBeforeFirstPay(400);
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithFirstPeriodCollateral(null, new DateTime(2024, 5, 1))
            .BuildAndRun(collateral);

        var toBonds = TotalPrincipal(cf, "A") + TotalPrincipal(cf, "B");
        toBonds.Should().BeApproximately(Math.Min(PoolPrincipal(collateral), 100_000_000), 1.0,
            "stating a boundary asks WHEN distributions begin, not that principal before it "
            + "should vanish");
    }

    [Theory]
    [InlineData("Fold")]
    [InlineData("Drop")]
    public void TheAnswerDoesNotDependOnTheOrderTheTapeArrivesIn(string policy)
    {
        // Align sorts as a side effect of re-dating, so on the default path order never
        // mattered and an explicit sort looks redundant. Under Fold and Drop nothing
        // re-dates, and the waterfall walks the caller's order — so the sort is load-bearing
        // exactly where it is least visible.
        var ascending = CollateralStartingBeforeFirstPay(24);
        var descending = CollateralStartingBeforeFirstPay(24);
        var reversed = descending.PeriodCashflows.OrderByDescending(p => p.CashflowDate).ToList();
        descending.PeriodCashflows.Clear();
        foreach (var pc in reversed)
            descending.PeriodCashflows.Add(pc);

        (double Prin, double Int) Run(CollateralCashflows c)
        {
            var cf = new TestDealBuilder()
                .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
                .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
                .WithSequentialWaterfall("A", "B")
                .WithFirstPeriodCollateral(policy)
                .BuildAndRun(c).Cashflows;
            return (TotalPrincipal(cf, "A") + TotalPrincipal(cf, "B"),
                Rows(cf, "A").Sum(r => r.Interest) + Rows(cf, "B").Sum(r => r.Interest));
        }

        var (ascPrin, ascInt) = Run(ascending);
        var (descPrin, descInt) = Run(descending);

        descPrin.Should().BeApproximately(ascPrin, 0.01,
            "a tape is a set of periods, not a sequence the caller gets to reorder");
        // Interest too: a reordered tape with NO stub moves interest while principal
        // stays put, so a principal-only assertion is blind to a whole class of it.
        descInt.Should().BeApproximately(ascInt, 0.01,
            "reordering must not move interest either");
    }

    [Theory]
    [InlineData(null)]     // the default
    [InlineData("Align")]
    [InlineData("Fold")]
    [InlineData("Drop")]
    public void CollateralThatNeverReachesADistribution_FailsLoudUnderEveryPolicy(string? policy)
    {
        // The first version of this guard read the FOLD ACCUMULATOR, which `Drop` never
        // populates — so the one policy that discards by design was the one policy the
        // guard could not see, and a Drop deal still returned zero rows for a funded pool
        // with no error. "Discard the stub" is not "deliver nothing at all".
        var act = () => new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithFirstPeriodCollateral(policy, new DateTime(2030, 1, 1))
            .BuildAndRun(CollateralStartingBeforeFirstPay(24));

        act.Should().Throw<InvalidOperationException>().WithMessage("*never reached one*");
    }

    [Fact]
    public void TheStrandedFigureCountsRecoveriesAndInterest()
    {
        // The message reported scheduled+unscheduled principal only, so a period carrying
        // recoveries (which fund PRINCIPAL_RECOVERY and ARE principal) and interest was
        // announced as "0.00 principal" — a diagnostic that misleads on exactly the deals
        // it exists for.
        var act = () => new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithFirstPeriodCollateral(null, new DateTime(2030, 1, 1))
            .BuildAndRun(CollateralStartingBeforeFirstPay(24));

        act.Should().Throw<InvalidOperationException>()
            .Where(e => !e.Message.Contains("0.00 of principal and interest"),
                "a stranded period with interest is not a stranded period worth nothing");
    }

    [Fact]
    public void CollateralThatNeverReachesADistribution_FailsLoud()
    {
        // The fold accumulator drains onto the first period at/after the boundary for the
        // SAME group. State a boundary past the end of the tape and no period ever reaches
        // it — every accumulated period was then discarded when the dictionary went out of
        // scope: zero tranche rows, zero interest, zero writedown, no error, and 100% of a
        // funded pool gone. Under the DEFAULT policy, through this PR's own new field.
        //
        // `AStatedAccrualBoundary_DoesNotCostTheDealPrincipal` picks a boundary well inside
        // the tape, so it cannot see this.
        var act = () => new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithFirstPeriodCollateral(null, new DateTime(2030, 1, 1))
            .BuildAndRun(CollateralStartingBeforeFirstPay(24));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*never reached one*",
                "an empty cashflow set for a funded pool is the one answer that must not "
                + "ship silently");
    }
}