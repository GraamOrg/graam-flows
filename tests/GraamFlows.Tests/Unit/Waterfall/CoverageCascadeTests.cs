using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// CLO per-level OC/IC coverage cascade (graam-flows#65): per-LEVEL coverage tests
/// inside the INTEREST step with an interest→principal diversion cure — pay a
/// level's interest, test OC/IC, and on failure divert available interest to
/// sequential senior-first principal paydown BEFORE any junior level's interest.
/// Semantics mirror the validated reference model
/// (graam-harmony src/graam/tools/clo/reference_model.py::forward_sim).
/// </summary>
public class CoverageCascadeTests
{
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    // Common stack: A 700,000 @ 6% (due ~3,500/mo), B 200,000 @ 8% (due ~1,333/mo).
    private static TestDealBuilder BuildDeal() => new TestDealBuilder()
        .WithTranche("A", 700_000, 6.0, subOrder: 0)
        .WithTranche("B", 200_000, 8.0, subOrder: 1)
        .WithSequentialWaterfall("A", "B");

    private static CollateralCashflows SinglePeriod(double interest, double defaulted) =>
        new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithPeriod(
                date: FirstPayDate,
                beginBalance: 1_000_000,
                scheduledPrincipal: 0,
                unscheduledPrincipal: 0,
                interest: interest,
                defaultedPrincipal: defaulted,
                recoveryPrincipal: 0)
            .Build();

    [Fact]
    public void OcFailure_DivertsExactCureAmount_SeniorFirst()
    {
        // Hand-computed fixture. Pool 1,000,000 loses 20,000 (no recovery) →
        // period-end collateral balance 980,000. Notes: A 700,000, B 200,000.
        //
        //   Level "A"   (Tranches [A],   OC trigger 125%): 980,000/700,000 = 140.00% → PASS
        //   Level "A/B" (Tranches [A,B], OC trigger 110%): 980,000/900,000 = 108.89% → FAIL
        //
        //   cure = Σ BALANCE(A,B) − numerator/(110/100)
        //        = 900,000 − 980,000/1.10 = 9,090.909090…
        //
        // Period interest 15,000 covers A (~3,500) + B (~1,333) with ~10,167 to
        // spare, so the diversion is the EXACT cure amount (not interest-capped),
        // and it pays A — the most senior note — as principal.
        var (_, cf) = BuildDeal()
            .WithCoverageCascade(
                new CoverageLevelConfig { Level = "A", Tranches = new[] { "A" }, OcTriggerPct = 125 },
                new CoverageLevelConfig { Level = "A/B", Tranches = new[] { "A", "B" }, OcTriggerPct = 110 })
            .BuildAndRun(SinglePeriod(interest: 15_000, defaulted: 20_000));

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");

        const double expectedCure = 900_000 - 980_000 / 1.10; // 9,090.9090…

        // Coupons are paid before the test at each level.
        aCf.Interest.Should().BeApproximately(700_000 * 0.06 / 12, 50,
            "A's coupon is paid at its level before the coverage test");
        bCf.Interest.Should().BeApproximately(200_000 * 0.08 / 12, 50,
            "B's coupon is paid at the A/B level before that level's test");

        // The diversion is the EXACT cure amount, and A receives it senior-first.
        (aCf.ScheduledPrincipal + aCf.UnscheduledPrincipal).Should().BeApproximately(
            expectedCure, 0.01, "diverted principal must equal the OC cure amount exactly");
        (bCf.ScheduledPrincipal + bCf.UnscheduledPrincipal).Should().Be(0,
            "the cure pays the note stack senior-first; A absorbs it all");
        aCf.Balance.Should().BeApproximately(700_000 - expectedCure, 0.01);

        // Per-level test results surface as trigger results.
        var ocA = cf.TriggerResults.Single(tr => tr.TriggerName == "OC_A");
        ocA.Passed.Should().BeTrue();
        ocA.ActualValue.Should().BeApproximately(140.0, 0.01);
        ocA.RequiredValue.Should().Be(125);

        var ocAb = cf.TriggerResults.Single(tr => tr.TriggerName == "OC_A/B");
        ocAb.Passed.Should().BeFalse();
        ocAb.ActualValue.Should().BeApproximately(980_000.0 / 900_000.0 * 100, 0.01);
        ocAb.RequiredValue.Should().Be(110);
    }

    [Fact]
    public void NoFailure_WaterfallIdenticalToDealWithoutCascade()
    {
        // Additive pin: a cascade whose tests all pass must leave the waterfall
        // byte-identical to the same deal with no CoverageCascade at all.
        CollateralCashflows Collateral() => new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(FirstPayDate, 3, 1_000_000, cpr: 6.0, cdr: 0.0, wac: 8.0)
            .Build();

        var (_, baseline) = BuildDeal().BuildAndRun(Collateral());

        // OC = pool/notes ≈ 111% > 105; IC ≈ 133% > 100 — every test passes.
        var (_, withCascade) = BuildDeal()
            .WithCoverageCascade(
                new CoverageLevelConfig
                {
                    Level = "A/B", Tranches = new[] { "A", "B" },
                    OcTriggerPct = 105, IcTriggerPct = 100
                })
            .BuildAndRun(Collateral());

        foreach (var name in new[] { "A", "B" })
        {
            var expected = GetCashflows(baseline, name);
            var actual = GetCashflows(withCascade, name);
            actual.Keys.Should().BeEquivalentTo(expected.Keys, $"{name}: same pay dates");
            foreach (var date in expected.Keys)
            {
                actual[date].Interest.Should().BeApproximately(expected[date].Interest, 1e-6,
                    $"{name} interest on {date:yyyy-MM-dd}");
                actual[date].ScheduledPrincipal.Should().BeApproximately(
                    expected[date].ScheduledPrincipal, 1e-6, $"{name} sched principal on {date:yyyy-MM-dd}");
                actual[date].UnscheduledPrincipal.Should().BeApproximately(
                    expected[date].UnscheduledPrincipal, 1e-6, $"{name} unsched principal on {date:yyyy-MM-dd}");
                actual[date].Writedown.Should().BeApproximately(expected[date].Writedown, 1e-6,
                    $"{name} writedown on {date:yyyy-MM-dd}");
                actual[date].BeginBalance.Should().BeApproximately(expected[date].BeginBalance, 1e-6,
                    $"{name} begin balance on {date:yyyy-MM-dd}");
                actual[date].Balance.Should().BeApproximately(expected[date].Balance, 1e-6,
                    $"{name} balance on {date:yyyy-MM-dd}");
            }
        }
    }

    [Fact]
    public void IcFailure_DivertsRemainingInterest()
    {
        // No losses (OC passes trivially — no OC test configured). Period interest
        // 6,000 vs A/B due ~4,833 → IC ≈ 124% < the 130% trigger → FAIL. An
        // IC-only failure sweeps the REMAINING interest (post-coupon) into
        // sequential senior-first principal: ~1,167 to A.
        var (_, cf) = BuildDeal()
            .WithCoverageCascade(
                new CoverageLevelConfig { Level = "A/B", Tranches = new[] { "A", "B" }, IcTriggerPct = 130 })
            .BuildAndRun(SinglePeriod(interest: 6_000, defaulted: 0));

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");

        var expectedDiversion = 6_000 - aCf.Interest - bCf.Interest;
        expectedDiversion.Should().BeGreaterThan(1_000, "the fixture leaves real interest to sweep");

        (aCf.ScheduledPrincipal + aCf.UnscheduledPrincipal).Should().BeApproximately(
            expectedDiversion, 0.01, "an IC-only failure sweeps all remaining interest to principal");
        (bCf.ScheduledPrincipal + bCf.UnscheduledPrincipal).Should().Be(0, "senior-first");

        var ic = cf.TriggerResults.Single(tr => tr.TriggerName == "IC_A/B");
        ic.Passed.Should().BeFalse();
        ic.RequiredValue.Should().Be(130);
        ic.ActualValue.Should().BeApproximately(
            6_000 / (aCf.Interest + bCf.Interest) * 100, 0.01,
            "IC = period collateral interest collected / level interest due, in percent");
    }

    [Fact]
    public void AcpaScheduledVariable_OverridesCollateralBalanceNumerator()
    {
        // No losses: collateral balance stays 1,000,000 → OC(A/B) = 111.1% > 110
        // → PASSES on the collateral-balance numerator. The deal's ACPA scheduled
        // variable (950,000 — e.g. after a CCC haircut computed by harmony) takes
        // over as numerator: 950,000/900,000 = 105.6% < 110 → FAIL → diversion.
        var collateralOnly = BuildDeal()
            .WithCoverageCascade(
                new CoverageLevelConfig { Level = "A/B", Tranches = new[] { "A", "B" }, OcTriggerPct = 110 })
            .BuildAndRun(SinglePeriod(interest: 15_000, defaulted: 0));

        var withAcpa = BuildDeal()
            .WithScheduledVariable("ACPA", 950_000,
                FirstPayDate.AddYears(-1), FirstPayDate.AddYears(10))
            .WithCoverageCascade(
                new CoverageLevelConfig { Level = "A/B", Tranches = new[] { "A", "B" }, OcTriggerPct = 110 })
            .BuildAndRun(SinglePeriod(interest: 15_000, defaulted: 0));

        // Baseline (no ACPA): test passes, nothing diverted.
        var aBase = GetFirstCashflow(collateralOnly.Cashflows, "A");
        (aBase.ScheduledPrincipal + aBase.UnscheduledPrincipal).Should().Be(0,
            "on the collateral-balance numerator the OC test passes — no diversion");
        collateralOnly.Cashflows.TriggerResults.Single(tr => tr.TriggerName == "OC_A/B")
            .Passed.Should().BeTrue();

        // With ACPA: the test fails ONLY because of the ACPA numerator. The cure
        // need (900,000 − 950,000/1.10 ≈ 36,364) exceeds the remaining interest,
        // so the diversion is capped at the post-coupon interest.
        var aAcpa = GetFirstCashflow(withAcpa.Cashflows, "A");
        var bAcpa = GetFirstCashflow(withAcpa.Cashflows, "B");
        var expectedDiversion = 15_000 - aAcpa.Interest - bAcpa.Interest;

        (aAcpa.ScheduledPrincipal + aAcpa.UnscheduledPrincipal).Should().BeApproximately(
            expectedDiversion, 0.01, "the ACPA-driven failure diverts the available interest");
        expectedDiversion.Should().BeGreaterThan(9_000);

        var oc = withAcpa.Cashflows.TriggerResults.Single(tr => tr.TriggerName == "OC_A/B");
        oc.Passed.Should().BeFalse();
        oc.ActualValue.Should().BeApproximately(950_000.0 / 900_000.0 * 100, 0.01,
            "the OC numerator is the ACPA scheduled variable, not the collateral balance");
    }

    [Fact]
    public void SeniorLevelFailure_DivertsBeforeJuniorInterest()
    {
        // The defining CLO behavior: a failing SENIOR level diverts interest to
        // principal BEFORE any junior level's interest is paid. Level "A" fails
        // (980,000/700,000 = 140% < 150%) with a cure need (~46,667) larger than
        // the remaining interest, so everything after A's coupon is diverted to
        // A's principal and B receives NO interest this period.
        var (_, cf) = BuildDeal()
            .WithCoverageCascade(
                new CoverageLevelConfig { Level = "A", Tranches = new[] { "A" }, OcTriggerPct = 150 },
                new CoverageLevelConfig { Level = "A/B", Tranches = new[] { "A", "B" }, OcTriggerPct = 50 })
            .BuildAndRun(SinglePeriod(interest: 15_000, defaulted: 20_000));

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");

        (aCf.ScheduledPrincipal + aCf.UnscheduledPrincipal).Should().BeApproximately(
            15_000 - aCf.Interest, 0.01,
            "everything after A's coupon is diverted (cure need exceeds remaining interest)");
        bCf.Interest.Should().Be(0, "the senior level's cure runs before junior interest");
        bCf.AccumInterestShortfall.Should().BeGreaterThan(0,
            "B's unpaid coupon accrues as a shortfall");
    }

    [Fact]
    public void CascadeWithInterleavedWaterfallOrder_ThrowsActionableError()
    {
        var run = () => BuildDeal()
            .WithWaterfallOrder(GraamFlows.Objects.TypeEnum.WaterfallOrderEnum.InterestFirst)
            .WithCoverageCascade(
                new CoverageLevelConfig { Level = "A/B", Tranches = new[] { "A", "B" }, OcTriggerPct = 110 })
            .BuildAndRun(SinglePeriod(interest: 15_000, defaulted: 0));

        run.Should().Throw<GraamFlows.Util.DealModelingException>()
            .WithMessage("*CoverageCascade*")
            .WithMessage("*standard waterfall order*");
    }

    [Fact]
    public void UnknownTrancheInCascade_ThrowsActionableError()
    {
        var run = () => BuildDeal()
            .WithCoverageCascade(
                new CoverageLevelConfig { Level = "A/B", Tranches = new[] { "A", "NOPE" }, OcTriggerPct = 110 })
            .BuildAndRun(SinglePeriod(interest: 15_000, defaulted: 0));

        run.Should().Throw<GraamFlows.Util.DealModelingException>()
            .WithMessage("*A/B*")
            .WithMessage("*NOPE*");
    }

    #region Helpers

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
