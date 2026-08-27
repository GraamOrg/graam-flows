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
    public void Interest_ExcessServicingStrip_PaidFromServicingFeeAndWalNeutral()
    {
        // The Class A-IO-S excess-servicing strip draws its strip from the
        // period servicing fee — which was already removed from collateral
        // interest before the waterfall — so it must NOT reduce interest to the
        // offered classes. Run an identical deal with and without the strip and
        // confirm the offered classes are byte-for-byte unchanged while the
        // strip is paid the servicing fee.
        var baseline = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(3, 100_000_000, wacPct: 8.0));

        var withStrip = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithExcessServicingStrip("AIOS", 100_000_000)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(3, 100_000_000, wacPct: 8.0));

        var collateral = CreateCollateral(3, 100_000_000, wacPct: 8.0);
        var firstServiceFee = collateral.PeriodCashflows
            .OrderBy(p => p.CashflowDate).First().ServiceFee;

        // Strip is paid the servicing fee.
        var aiosCf = GetFirstCashflow(withStrip.Cashflows, "AIOS");
        aiosCf.Interest.Should().BeApproximately(firstServiceFee, 1.0,
            "the excess-servicing strip receives the period servicing fee");
        firstServiceFee.Should().BeGreaterThan(0);

        // Offered classes are unchanged by the strip (WAL-neutral).
        GetFirstCashflow(withStrip.Cashflows, "A").Interest
            .Should().BeApproximately(GetFirstCashflow(baseline.Cashflows, "A").Interest, 0.01);
        GetFirstCashflow(withStrip.Cashflows, "B").Interest
            .Should().BeApproximately(GetFirstCashflow(baseline.Cashflows, "B").Interest, 0.01);
    }

    [Fact]
    public void Interest_PreFirstPayStub_ReTimedToOneMonthPerDistribution()
    {
        // Repro of graam-harmony #2748. The pool is projected from the closing/
        // cutoff date, so its first period lands a full month BEFORE the first pay
        // date. The amortizer emits a full month of interest for every period, so
        // without re-timing the first-pay fold sums that stub month INTO the first
        // paying period and the residual (XS) sweeps ~2 months of collateral
        // interest in period 0 — paying out more than the pool earned. After the
        // fix each collateral month funds exactly one distribution.
        const double bal = 100_000_000;
        var collateral = CreateCollateralBeforeFirstPay(4, bal, wacPct: 8.0);
        var collOrdered = collateral.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();

        // Sanity: the repro really has a pre-first-pay stub period.
        collOrdered[0].CashflowDate.Should().BeBefore(FirstPayDate);

        var (deal, run) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithExcessServicingStrip("AIOS", bal)
            .WithTranche("XS", 0, 0.0, subOrder: 2,
                cashflowType: "IO", couponType: "ResidualInterest")
            .WithSequentialWaterfall("A", "B", "XS")
            .BuildAndRun(collateral);

        var a = GetCashflows(run, "A");
        var b = GetCashflows(run, "B");
        var xs = GetCashflows(run, "XS");
        var aios = GetCashflows(run, "AIOS");

        var payDates = a.Keys.OrderBy(d => d).ToList();
        var firstPay = payDates.First();

        // Each distribution is funded by exactly ONE collateral month (the i-th
        // collateral period, re-timed onto the i-th pay date): the interest paid
        // to all tranches never exceeds that single month's collateral interest.
        for (var i = 0; i < payDates.Count && i < collOrdered.Count; i++)
        {
            var d = payDates[i];
            var distributed =
                (a.TryGetValue(d, out var ac) ? ac.Interest : 0) +
                (b.TryGetValue(d, out var bc) ? bc.Interest : 0) +
                (xs.TryGetValue(d, out var xc) ? xc.Interest : 0) +
                (aios.TryGetValue(d, out var ic) ? ic.Interest : 0);
            distributed.Should().BeLessOrEqualTo(collOrdered[i].Interest + 1.0,
                $"distribution {i} ({d:yyyy-MM-dd}) must not exceed one collateral month");
        }

        // First distribution == ONE collateral month (the stub period), split
        // coll - seniors - AIOS, NOT two months.
        var stub = collOrdered[0];
        aios[firstPay].Interest.Should().BeApproximately(stub.ServiceFee, 1.0,
            "the strip receives one month of servicing fee");
        xs[firstPay].Interest.Should().BeApproximately(
            stub.NetInterest - a[firstPay].Interest - b[firstPay].Interest, 1.0,
            "XS = one month net interest - seniors (no folded stub month)");
        xs[firstPay].Interest.Should().BeLessThan(stub.NetInterest,
            "the residual can never exceed a single month of pool interest");

        // Conservation: over the run, all distributed interest ties to the
        // collateral interest of the periods that were distributed (nothing
        // dropped, nothing double-counted).
        var distributedTotal = new[] { a, b, xs, aios }
            .Sum(t => t.Where(kv => kv.Key <= payDates.Last()).Sum(kv => kv.Value.Interest));
        var collateralTotal = collOrdered.Take(payDates.Count).Sum(p => p.Interest);
        distributedTotal.Should().BeApproximately(collateralTotal, 5.0,
            "total distributed interest ties to collateral interest — conserved");
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
            .WithMessage("*ExcessInterest*at most one*")
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
    public void ExcessRelease_HonorsStructureOrder_XsBeforeResidual()
    {
        // EXCESS_RELEASE SEQ(XS, R): the excess goes to the XS strip FIRST, then
        // R. Earlier the step ignored the structure and dumped the excess onto
        // every Certificate class, so an XS listed first got nothing while the
        // residual Certificate scooped it all (#1714).
        var collateral = CreateCollateral(6, 100_000_000, wacPct: 10.0); // high WAC → excess
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 100_000_000, 4.0, subOrder: 0) // low coupon → big excess
            .WithTranche("XS", 100_000_000, 0.0, subOrder: 1,
                cashflowType: "IO", couponType: "ResidualInterest")
            .WithCertificateTranche("R", subOrder: 2)
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("ReleaseStruct", "SET_RELEASE_STRUCT(SEQ(SINGLE('XS'), SINGLE('R')))")
            .BuildAndRun(collateral);

        var xsInt = GetCashflows(cf, "XS").Sum(c => c.Value.Interest);
        var rInt = GetCashflows(cf, "R").Sum(c => c.Value.Interest);

        xsInt.Should().BeGreaterThan(0, "XS is first in EXCESS_RELEASE → it sweeps the excess");
        rInt.Should().Be(0, "R is after XS → it gets nothing once XS sweeps the excess");
    }

    [Fact]
    public void ExcessStruct_NoCertificate_PaysDeclaredRecipient_NotDestroyed()
    {
        // graam-flows#68: an EXCESS step's structure lands in ExcessPayable
        // (SET_EXCESS_STRUCT), but the shared EXCESS/EXCESS_RELEASE executor read
        // only ReleasePayable. On a deal with NO Certificate class (the CLO-native
        // shape — equity is a plain subordinated note), recipients resolved empty
        // and the residual interest was silently DESTROYED every period. The
        // declared recipient must receive it.
        var collateral = CreateCollateral(6, 100_000_000, wacPct: 10.0); // high WAC → excess
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 100_000_000, 4.0, subOrder: 0) // low coupon → big excess
            .WithTranche("Subordinated", 10_000_000, 0.0, subOrder: 1)
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("ExcessStruct", "SET_EXCESS_STRUCT(SINGLE('Subordinated'))")
            .BuildAndRun(collateral);

        var subInt = GetCashflows(cf, "Subordinated").Sum(c => c.Value.Interest);
        var aInt = GetCashflows(cf, "A").Sum(c => c.Value.Interest);

        // Pool at 10% WAC vs A due at 4% → substantial residual interest, and it
        // must land on the declared EXCESS recipient rather than vanish.
        subInt.Should().BeGreaterThan(0, "the EXCESS structure names Subordinated as the sweep recipient");
        (aInt + subInt).Should().BeGreaterThan(aInt, "cash conservation: residual interest is distributed, not destroyed");
    }

    [Fact]
    public void ExcessRelease_NestedStructure_StillYieldsXs()
    {
        // A nested release — SEQ(SEQ(XS), R) — must still resolve XS as the first
        // recipient. Earlier a flat GetChildren().OfType<DynamicClass>() missed
        // the inner SequentialStructure and silently dropped XS, reopening #1714.
        var collateral = CreateCollateral(6, 100_000_000, wacPct: 10.0);
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 100_000_000, 4.0, subOrder: 0)
            .WithTranche("XS", 100_000_000, 0.0, subOrder: 1,
                cashflowType: "IO", couponType: "ResidualInterest")
            .WithCertificateTranche("R", subOrder: 2)
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("ReleaseStruct",
                "SET_RELEASE_STRUCT(SEQ(SEQ(SINGLE('XS')), SINGLE('R')))")
            .BuildAndRun(collateral);

        var xsInt = GetCashflows(cf, "XS").Sum(c => c.Value.Interest);
        var rInt = GetCashflows(cf, "R").Sum(c => c.Value.Interest);

        xsInt.Should().BeGreaterThan(0,
            "nested SEQ(SEQ(XS), R) still resolves XS as the first recipient");
        rInt.Should().Be(0, "R is after XS → it gets nothing once XS sweeps the excess");
    }

    [Fact]
    public void ExcessRelease_MultiTrancheRecipient_DoesNotMintInterest()
    {
        // A Certificate recipient's excess release lands on its CLASS cashflow, because
        // ConvertToResponse serializes Certificate classes from ClassCashflows (their per-tranche
        // cashflows are skipped) and UpdateCertificateBalance writes the class cashflow. Crediting
        // the per-tranche cashflow dropped the excess from the output (graam-flows#32). A class is
        // ONE cashflow, so a multi-tranche (combined / exchangeable) Certificate cannot mint the
        // excess. Conservation: total interest distributed equals collateral net interest.
        var collateral = CreateCollateral(6, 100_000_000, wacPct: 10.0);
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 100_000_000, 4.0, subOrder: 0)
            .WithCertificateTranche("R", subOrder: 1)
            .WithTrancheInClass("R", "R2", 0.0, 0.0,
                cashflowType: "PI", couponType: "None", trancheType: "Certificate")
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("ReleaseStruct", "SET_RELEASE_STRUCT(SEQ(SINGLE('R')))")
            .BuildAndRun(collateral);

        var aInt = GetCashflows(cf, "A").Sum(c => c.Value.Interest);
        // Certificate excess is on the CLASS cashflow (what ConvertToResponse serializes).
        var rInt = GetClassCashflows(cf, "R").Sum(c => c.Value.Interest);
        var netInterest = collateral.PeriodCashflows.Sum(p => p.NetInterest);

        rInt.Should().BeGreaterThan(0, "the combined R class sweeps the excess onto its class cashflow");
        // The class receives the excess exactly ONCE — no per-tranche double-credit.
        (aInt + rInt).Should().BeApproximately(netInterest, 1.0,
            "the R class sweeps the excess exactly once — no interest is minted");
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

    #region Deal Model Validation

    [Fact]
    public void ExchangedClass_WithoutComponentReference_ThrowsActionableError()
    {
        // A class typed Exchanged but with no ExchangableTranche (e.g. a plain
        // debt note mis-typed as Exchanged by an upstream extractor) previously
        // NRE'd deep in the subordination walk. It must now fail with an
        // actionable DealModelingException at validation instead.
        Action run = () => new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithExchangeClass("AB", subOrder: 2, wellFormed: false, "A", "B")
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        run.Should().Throw<DealModelingException>()
            .WithMessage("*AB*")
            .WithMessage("*Exchanged*");
    }

    [Fact]
    public void ExchangedClass_WellFormed_DoesNotThrow()
    {
        // Sanity: a properly configured Exchanged class still validates.
        Action run = () => new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithExchangeClass("AB", subOrder: 2, "A", "B")
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        run.Should().NotThrow<DealModelingException>();
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

    /// <summary>
    ///     Collateral projected ONE month BEFORE the first pay date — the pool was
    ///     projected from the closing/cutoff date, so period 0 lands ahead of the
    ///     first distribution (a full-month "stub").
    /// </summary>
    private static CollateralCashflows CreateCollateralBeforeFirstPay(int numPeriods,
        double startingBalance, double wacPct = 8.0)
    {
        return new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(ProjectionDate, numPeriods, startingBalance,
                cpr: TestConstants.DefaultCpr, cdr: 0.0, wac: wacPct)
            .Build();
    }

    #region Exchangeable / MACR classes

    [Fact]
    public void ExchangeClass_MirrorsSumOfComponents_PrincipalAndInterest()
    {
        // A MACR / combined class "AB" holds 100% of A and 100% of B. Its cashflow
        // must equal the sum of A's and B's cashflows — principal AND interest —
        // every period. The exchange overlay (PayExchangeables + PayExchangeInterest)
        // derives it from the components; before that overlay was invoked from
        // ComposableStructure the class produced zero.
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithExchangeClass("AB", subOrder: 50, "A", "B")
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(6, 100_000_000, wacPct: 8.0));

        var a = GetCashflows(cf, "A");
        var b = GetCashflows(cf, "B");
        var ab = GetCashflows(cf, "AB");

        ab.Should().NotBeEmpty("the exchange class must produce cashflows");

        double Prin(TrancheCashflow c) => c.ScheduledPrincipal + c.UnscheduledPrincipal;
        foreach (var date in a.Keys)
        {
            var expectedPrin = Prin(a[date]) + Prin(b[date]);
            var expectedInt = a[date].Interest + b[date].Interest;
            Prin(ab[date]).Should().BeApproximately(expectedPrin, 0.01,
                $"AB principal must equal A+B on {date:yyyy-MM-dd}");
            ab[date].Interest.Should().BeApproximately(expectedInt, 0.01,
                $"AB interest must equal A+B on {date:yyyy-MM-dd}");
        }

        // Lifetime totals tie as well.
        (ab.Values.Sum(Prin)).Should().BeApproximately(
            a.Values.Sum(Prin) + b.Values.Sum(Prin), 0.01);
        (ab.Values.Sum(c => c.Interest)).Should().BeApproximately(
            a.Values.Sum(c => c.Interest) + b.Values.Sum(c => c.Interest), 0.01);
    }

    [Fact]
    public void ExchangeClass_DoesNotDoubleCount_ComponentsUnchanged()
    {
        // Adding the exchange overlay class must not divert cash from the primaries:
        // the components' cashflows are identical with and without the MACR class.
        var collateral = CreateCollateral(6, 100_000_000, wacPct: 8.0);
        var baseRun = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(collateral);
        var withMacr = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithExchangeClass("AB", subOrder: 50, "A", "B")
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(collateral);

        double Prin(TrancheCashflow c) => c.ScheduledPrincipal + c.UnscheduledPrincipal;
        foreach (var name in new[] { "A", "B" })
        {
            var baseline = GetCashflows(baseRun.Cashflows, name);
            var withEx = GetCashflows(withMacr.Cashflows, name);
            baseline.Values.Sum(Prin).Should().BeApproximately(withEx.Values.Sum(Prin), 0.01,
                $"{name} principal must be unchanged by the exchange overlay");
            baseline.Values.Sum(c => c.Interest).Should().BeApproximately(
                withEx.Values.Sum(c => c.Interest), 0.01,
                $"{name} interest must be unchanged by the exchange overlay");
        }
    }

    #endregion

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

    // CLASS-level cashflows — what ConvertToResponse serializes for a Certificate/OC class (whose
    // per-tranche cashflows it skips). Certificate excess-release lands here (graam-flows#32).
    private static Dictionary<DateTime, TrancheCashflow> GetClassCashflows(
        DealCashflows dealCashflows, string trancheName)
    {
        var match = dealCashflows.ClassCashflows.FirstOrDefault(t => t.Key.TrancheName == trancheName);
        return match.Value?.Cashflows ?? new Dictionary<DateTime, TrancheCashflow>();
    }

    #endregion
}
