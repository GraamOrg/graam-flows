using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// End-to-end tests for ComposableStructure waterfall execution.
/// Each test builds a minimal deal, generates collateral, runs the waterfall,
/// and verifies the output cashflows.
/// </summary>
public class ComposableStructureTests
{
    private static readonly DateTime ProjectionDate = TestConstants.DefaultProjectionDate;
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    #region Interest Distribution

    [Fact]
    public void Interest_SequentialDistribution_SeniorPaidFirst()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");

        aCf.Interest.Should().BeGreaterThan(0, "A should receive interest");
        bCf.Interest.Should().BeGreaterThan(0, "B should receive interest");

        // A's interest ≈ 80M * 5% / 12
        aCf.Interest.Should().BeApproximately(80_000_000 * 0.05 / 12, 50000);
    }

    [Fact]
    public void Interest_InsufficientFunds_SeniorPaidBeforeJunior()
    {
        // Low WAC collateral so there isn't enough interest for everyone
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 15.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(1, 100_000_000, wacPct: 3.0));

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");

        // A should be fully paid, B may have shortfall
        aCf.Interest.Should().BeGreaterThan(0);
        // Total interest paid should not exceed collateral interest
        var totalTranchInterest = aCf.Interest + bCf.Interest;
        var collateralInterest = 100_000_000 * 0.03 / 12 * 0.97; // net of servicing
        totalTranchInterest.Should().BeLessOrEqualTo(collateralInterest + 1);
    }

    [Fact]
    public void Interest_GuaranteedTreatment_PaysFullCouponRegardless()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithInterestTreatment("Guaranteed")
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(1, 100_000_000, wacPct: 2.0)); // Very low WAC

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");

        // With Guaranteed treatment, full coupon paid even with low collateral WAC
        aCf.Interest.Should().BeApproximately(80_000_000 * 0.05 / 12, 50000);
        bCf.Interest.Should().BeApproximately(20_000_000 * 0.06 / 12, 50000);
    }

    [Fact]
    public void Interest_ResidualInterestTranche_SweepsExcessSpread()
    {
        // Collateral WAC (8%) exceeds the bond coupons (5% / 6%), so there is
        // excess spread every period. An XS tranche with
        // CouponType=ResidualInterest, placed last in the interest SEQ, should
        // sweep whatever interest remains after the coupon classes are paid —
        // Payscen TrancheAllocator parity. Before this fix the composable path
        // paid it balance × coupon = 0 and dropped the excess on the floor.
        var collateral = CreateCollateral(3, 100_000_000, wacPct: 8.0);
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithTranche("XS", 0, 0.0, subOrder: 2,
                cashflowType: "IO", couponType: "ResidualInterest")
            .WithSequentialWaterfall("A", "B", "XS")
            .BuildAndRun(collateral);

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");
        var xsCf = GetFirstCashflow(cf, "XS");

        var netInterest = collateral.PeriodCashflows
            .OrderBy(p => p.CashflowDate).First().NetInterest;

        // Coupon classes are unchanged by the sweep.
        aCf.Interest.Should().BeApproximately(80_000_000 * 0.05 / 12, 50000);
        bCf.Interest.Should().BeApproximately(20_000_000 * 0.06 / 12, 50000);

        // XS sweeps the remainder: net interest minus the coupon classes.
        xsCf.Interest.Should().BeGreaterThan(0, "XS should receive the excess spread");
        xsCf.Interest.Should().BeApproximately(
            netInterest - aCf.Interest - bCf.Interest, 1.0);

        // Conservation: every dollar of net interest is distributed (nothing
        // dropped) once a residual sweeper is present.
        (aCf.Interest + bCf.Interest + xsCf.Interest)
            .Should().BeApproximately(netInterest, 1.0);
    }

    [Fact]
    public void Interest_TwoResidualInterestTranches_ThrowsAtBuild()
    {
        // Two ResidualInterest tranches in one interest group is a config error:
        // the sweep (DynamicClass.PayInterest) gives the FIRST residual all the
        // remaining interest, silently zeroing the second (first-wins). Cash
        // still conserves and nothing throws at runtime, so the misconfig is
        // invisible — validation must fail loudly at deal build instead.
        var collateral = CreateCollateral(3, 100_000_000, wacPct: 8.0);
        var build = () => new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("XS1", 0, 0.0, subOrder: 1,
                cashflowType: "IO", couponType: "ResidualInterest")
            .WithTranche("XS2", 0, 0.0, subOrder: 2,
                cashflowType: "IO", couponType: "ResidualInterest")
            .WithSequentialWaterfall("A", "XS1", "XS2")
            .BuildAndRun(collateral);

        build.Should().Throw<DealModelingException>()
            .WithMessage("*ResidualInterest*at most one*")
            .WithMessage("*XS1*XS2*");
    }

    [Fact]
    public void Notional_IoTranche_TracksPoolBalance_FiniteCoupon()
    {
        // An IO / excess-spread tranche (XS) carries a notional that tracks the
        // pool balance instead of amortizing via the principal waterfall.
        // Without it the balance is 0, so coupon % / price / yield divide by a
        // zero face. XS is in the INTEREST SEQ (sweeps the spread) but NOT in
        // PRINCIPAL / WRITEDOWN — it is interest-only.
        const double startBal = 100_000_000.0;
        var collateral = CreateCollateral(12, startBal, wacPct: 8.0, cdrPct: 2.0); // losses too
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithTranche("XS", startBal, 0.0, subOrder: 2,
                cashflowType: "IO", couponType: "ResidualInterest")
            .WithPayRule("InterestStruct",
                "SET_INTEREST_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('XS')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A'), SINGLE('B')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A'), SINGLE('B')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A'), SINGLE('B')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('B'), SINGLE('A')))")
            .BuildAndRun(collateral);

        var poolByDate = collateral.PeriodCashflows.ToDictionary(p => p.CashflowDate, p => p);
        var xsCfs = GetCashflows(cf, "XS").OrderBy(c => c.Key).ToList();
        xsCfs.Should().NotBeEmpty();

        var matched = 0;
        foreach (var kv in xsCfs)
        {
            if (!poolByDate.TryGetValue(kv.Key, out var pool))
                continue;
            matched++;
            var xs = kv.Value;
            // Notional tracks the pool balance to the dollar (incl. losses).
            xs.BeginBalance.Should().BeApproximately(pool.BeginBalance, 1.0,
                $"XS notional must equal the pool balance at {kv.Key:yyyy-MM}");
            // Effective coupon is finite — never Infinity/NaN from a zero face.
            double.IsNaN(xs.EffectiveCoupon).Should().BeFalse();
            double.IsInfinity(xs.EffectiveCoupon).Should().BeFalse();
            // Interest-only: the holder receives NO principal. The notional
            // tracks the pool via the balance schedule, not principal cashflows;
            // WAL is derived from the balance change (IsIo) at pricing time.
            (xs.ScheduledPrincipal + xs.UnscheduledPrincipal).Should().Be(0,
                "an IO tranche receives no principal cash");
        }

        matched.Should().BeGreaterThan(5, "most XS periods should map to a pool period");
        // The notional amortizes with the pool — it is NOT frozen at original face.
        xsCfs.Last().Value.BeginBalance.Should().BeLessThan(startBal);
        // And it receives the swept excess spread.
        xsCfs.Sum(c => c.Value.Interest).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Interest_NoResidualTranche_ExcessNotOverDistributed()
    {
        // Backwards-compat guard: with no ResidualInterest tranche, coupon
        // classes still take only their due and the excess is NOT force-fed to
        // anyone (it flows back as undistributed). A + B must stay at coupon.
        var collateral = CreateCollateral(1, 100_000_000, wacPct: 8.0);
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(collateral);

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");
        var netInterest = collateral.PeriodCashflows
            .OrderBy(p => p.CashflowDate).First().NetInterest;

        aCf.Interest.Should().BeApproximately(80_000_000 * 0.05 / 12, 50000);
        bCf.Interest.Should().BeApproximately(20_000_000 * 0.06 / 12, 50000);
        // Excess spread exists but is not distributed to the coupon classes.
        (aCf.Interest + bCf.Interest).Should().BeLessThan(netInterest);
    }

    #endregion

    #region Principal Distribution

    [Fact]
    public void Principal_Sequential_SeniorPaidFirst()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 50_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 30_000_000, 6.0, subOrder: 1)
            .WithTranche("C", 20_000_000, 7.0, subOrder: 2)
            .WithSequentialWaterfall("A", "B", "C")
            .BuildAndRun(CreateCollateral(6, 100_000_000));

        var aCfs = GetCashflows(cf, "A");
        var bCfs = GetCashflows(cf, "B");

        var aPrincipal = aCfs.Sum(c => c.Value.TotalPrincipal());
        var bPrincipal = bCfs.Sum(c => c.Value.TotalPrincipal());

        aPrincipal.Should().BeGreaterThan(0, "A should receive principal");

        // B should not receive principal while A still has balance
        var aStillHasBalance = aCfs.All(c => c.Value.Balance > 1000);
        if (aStillHasBalance)
            bPrincipal.Should().BeLessThan(1000,
                "B should not receive principal while A has remaining balance");
    }

    [Fact]
    public void Principal_BalanceDecreasesCorrectly()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(6, 100_000_000));

        foreach (var trancheName in new[] { "A", "B" })
        {
            var cashflows = GetCashflows(cf, trancheName);
            foreach (var tcf in cashflows.Values)
            {
                var expectedBalance = tcf.BeginBalance - tcf.ScheduledPrincipal
                                      - tcf.UnscheduledPrincipal - tcf.Writedown;
                tcf.Balance.Should().BeApproximately(expectedBalance, 1,
                    $"{trancheName}: Balance should equal BeginBalance - Principal - Writedown");
            }
        }
    }

    [Fact]
    public void Principal_ConsecutivePeriodsLink()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(6, 100_000_000));

        var aCashflows = GetCashflows(cf, "A").OrderBy(c => c.Key).Select(c => c.Value).ToList();

        for (var i = 1; i < aCashflows.Count; i++)
        {
            aCashflows[i].BeginBalance.Should().BeApproximately(aCashflows[i - 1].Balance, 1,
                $"Period {i}: BeginBalance should equal previous period's ending Balance");
        }
    }

    #endregion

    #region Writedown Distribution

    [Fact]
    public void Writedown_ReverseSeniority_JuniorAbsorbsFirst()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 70_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithTranche("C", 10_000_000, 7.0, subOrder: 2)
            .WithSequentialWaterfall("A", "B", "C")
            .BuildAndRun(CreateCollateral(6, 100_000_000, cdrPct: 10.0));

        var aCumWd = GetCashflows(cf, "A").Max(c => c.Value.CumWritedown);
        var bCumWd = GetCashflows(cf, "B").Max(c => c.Value.CumWritedown);
        var cCumWd = GetCashflows(cf, "C").Max(c => c.Value.CumWritedown);

        // C (most junior in writedown order) should absorb losses first
        if (cCumWd < 10_000_000 * 0.99)
        {
            bCumWd.Should().BeLessThan(cCumWd * 0.1,
                "B should have minimal writedowns while C has remaining balance");
        }

        aCumWd.Should().BeLessThanOrEqualTo(bCumWd + 1,
            "A should not have more writedowns than B");
    }

    [Fact]
    public void Writedown_ExceedsNoteBalance_ClampsWithoutCrashing()
    {
        // Regression for the synthetic_loan_passthrough hard crash: a period's
        // collateral loss (defaulted principal net of recovery) exceeds the single
        // note's remaining balance. This previously tripped Debug.Assert in
        // DynamicClass.Writedown, fail-fasting the whole process. The class must
        // instead clamp the writedown to its balance and continue.
        var collateral = new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithPeriod(
                date: FirstPayDate,
                beginBalance: 10_000_000,
                scheduledPrincipal: 0,
                unscheduledPrincipal: 0,
                interest: 50_000,
                defaultedPrincipal: 12_000_000, // exceeds the 10M note
                recoveryPrincipal: 0)           // 100% severity, nothing to offset
            .Build();

        var run = () => new TestDealBuilder()
            .WithTranche("A", 10_000_000, 5.0, subOrder: 0)
            .WithSequentialWaterfall("A")
            .BuildAndRun(collateral);

        run.Should().NotThrow("writedowns exceeding the note balance must clamp, not crash the process");

        var (_, cf) = run();
        var aCumWd = GetCashflows(cf, "A").Max(c => c.Value.CumWritedown);
        aCumWd.Should().BeLessOrEqualTo(10_000_000 + 1,
            "a note can absorb at most its own balance in writedowns");
    }

    #endregion

    #region Cashflow Conservation

    [Fact]
    public void CashflowConservation_PrincipalDistributed()
    {
        var collateral = CreateCollateral(6, 100_000_000);
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(collateral);

        var totalCollateralPrincipal = collateral.PeriodCashflows
            .Sum(p => p.ScheduledPrincipal + p.UnscheduledPrincipal);

        var totalTranchePrincipal = new[] { "A", "B" }
            .SelectMany(name => GetCashflows(cf, name).Values)
            .Sum(c => c.TotalPrincipal());

        totalTranchePrincipal.Should().BeGreaterThan(0, "Tranches should receive principal");
        totalTranchePrincipal.Should().BeLessOrEqualTo(totalCollateralPrincipal + 1,
            "Tranche principal should not exceed collateral principal");
    }

    #endregion

    #region Execution Order

    [Fact]
    public void ExecutionOrder_DefaultOrder_Works()
    {
        // No explicit execution order - uses defaults
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        var aCf = GetFirstCashflow(cf, "A");
        aCf.Interest.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ExecutionOrder_Custom_Respected()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithExecutionOrder("EXPENSE", "INTEREST", "PRINCIPAL_SCHEDULED",
                "PRINCIPAL_UNSCHEDULED", "WRITEDOWN")
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        GetFirstCashflow(cf, "A").Interest.Should().BeGreaterThan(0);
    }

    #endregion

    #region Prorata Interest + Sequential Principal

    [Fact]
    public void ProrataInterest_SequentialPrincipal_Works()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 50_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 30_000_000, 5.0, subOrder: 1)
            .WithTranche("C", 20_000_000, 5.0, subOrder: 2)
            .WithProrataInterestSequentialPrincipal(
                new[] { "A", "B", "C" },
                new[] { "A", "B", "C" })
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");
        var cCf = GetFirstCashflow(cf, "C");

        // All tranches should receive interest (prorata)
        aCf.Interest.Should().BeGreaterThan(0);
        bCf.Interest.Should().BeGreaterThan(0);
        cCf.Interest.Should().BeGreaterThan(0);

        // Interest should be proportional to balance * coupon
        var aShare = aCf.Interest / (aCf.Interest + bCf.Interest + cCf.Interest);
        aShare.Should().BeApproximately(0.5, 0.05, "A (50% balance) should get ~50% of interest");
    }

    #endregion

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

    private static Dictionary<DateTime, TrancheCashflow> GetCashflows(
        DealCashflows dealCashflows, string trancheName)
    {
        var match = dealCashflows.TrancheCashflows.FirstOrDefault(t => t.Key.TrancheName == trancheName);
        return match.Value?.Cashflows ?? new Dictionary<DateTime, TrancheCashflow>();
    }

    #endregion
}
