using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Util;
using GraamFlows.Waterfall;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
///     The MODIFICATION_LOSS step: a Modification Loss Priority whose rungs interleave two
///     effects, which the single-amount WRITEDOWN leg cannot express (graam-harmony #4794).
///
///     The ladder under test is the shape agency CRT states — STACR 2025-DNA1, "Allocation of
///     Modification Loss Amount" — reduced to three classes so the arithmetic is checkable by
///     hand:
///
///         first   C -> Preliminary Class Notional Amount   (NOTIONAL)
///         second  B -> Interest Accrual Amount             (INTEREST)
///         third   B -> Preliminary Class Notional Amount   (NOTIONAL)
///
///     with A standing in for the senior reference tranche the notional bites transfer to.
/// </summary>
public class ModificationLossTests
{
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    // 12% on 10,000,000 is 100,000 a month — the interest rung's whole capacity, chosen so the
    // amounts below sit either side of it by an unmistakable margin rather than a rounding one.
    private const double BBalance = 10_000_000;
    private const double BCouponPct = 12.0;
    private const double BMonthlyAccrual = BBalance * BCouponPct / 100 / 12;
    private const double CBalance = 1_000_000;

    private static TestDealBuilder Ladder(string writeUpTranche = "A")
    {
        var builder = new TestDealBuilder()
            .WithTranche("A", 89_000_000, 5.0, subOrder: 0)
            .WithTranche("B", BBalance, BCouponPct, subOrder: 1)
            .WithTranche("C", CBalance, 6.0, subOrder: 2)
            .WithSequentialWaterfall("A", "B", "C")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED",
                "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN")
            .WithPayRule("ModLossStruct",
                "SET_MODLOSS_STRUCT(ML_NOTIONAL(SINGLE('C')), ML_INTEREST(SINGLE('B')), " +
                "ML_NOTIONAL(SINGLE('B')))");

        if (writeUpTranche != null)
            builder.WithPayRule("ModLossWriteup", $"SET_MODLOSS_WRITEUP('{writeUpTranche}')");

        return builder;
    }

    /// <summary>
    ///     One period, no credit events, so the ladder is the only thing moving anything.
    /// </summary>
    /// <summary>One period whose collateral INTEREST is stated, for the interest-cascade probes.</summary>
    private static CollateralCashflows OnePeriodWithInterest(double modificationLoss, double interest)
    {
        return new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithPeriod(
                date: FirstPayDate,
                beginBalance: 100_000_000,
                scheduledPrincipal: 0,
                unscheduledPrincipal: 0,
                interest: interest,
                modificationLoss: modificationLoss)
            .Build();
    }

    private static CollateralCashflows OnePeriod(double modificationLoss, double defaultedPrincipal = 0)
    {
        return new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithPeriod(
                date: FirstPayDate,
                beginBalance: 100_000_000,
                scheduledPrincipal: 0,
                unscheduledPrincipal: 0,
                interest: 100_000_000 * 0.08 / 12,
                defaultedPrincipal: defaultedPrincipal,
                modificationLoss: modificationLoss)
            .Build();
    }

    [Fact]
    public void FirstRung_TakesNotional_AndTheSeniorTakesItOn()
    {
        // 400,000 — inside C's 1,000,000 notional, so the ladder stops at the first priority.
        var (_, cf) = Ladder().BuildAndRun(OnePeriod(modificationLoss: 400_000));

        var c = First(cf, "C");
        var a = First(cf, "A");

        c.Writedown.Should().BeApproximately(400_000, 1, "the first priority is a notional bite on C");
        c.ModificationWritedown.Should().BeApproximately(400_000, 1,
            "and it is reported as a MODIFICATION writedown, not a credit-event one");
        c.Balance.Should().BeApproximately(CBalance - 400_000, 1);

        // The document makes a notional bite a TRANSFER: "the Class Notional Amount for the
        // Class A-H Reference Tranche will be increased by the sum of amounts included in the
        // first, third, fifth ... priorities above".
        a.Balance.Should().BeApproximately(89_000_000 + 400_000, 1,
            "the senior reference tranche absorbs the notional the junior lost");
        a.ModificationWritedown.Should().BeApproximately(-400_000, 1,
            "signed the other way on the receiving class, so the two sum to zero");

        (c.ModificationWritedown + a.ModificationWritedown).Should().BeApproximately(0, 1,
            "a modification notional bite creates no notional and destroys none");
    }

    [Fact]
    public void SecondRung_CutsInterest_AndLeavesNotionalAlone()
    {
        // 1,000,000 exhausts C's notional; the remaining 50,000 reaches B's interest rung, which
        // has 100,000 of capacity, so nothing gets as far as the third (notional) priority.
        var (_, cf) = Ladder().BuildAndRun(OnePeriod(modificationLoss: 1_050_000));

        var b = First(cf, "B");

        b.ModificationLoss.Should().BeApproximately(50_000, 1,
            "the second priority allocates against B's Interest Accrual Amount");
        b.Interest.Should().BeApproximately(BMonthlyAccrual - 50_000, 1,
            "which reduces the interest B is actually paid");

        // The whole reason the two rung kinds are modelled separately: an interest bite does not
        // touch the notional, so it does not erode credit enhancement.
        b.Writedown.Should().Be(0, "an interest bite writes nothing down");
        b.ModificationWritedown.Should().Be(0);
        b.Balance.Should().BeApproximately(BBalance, 1, "B's Class Notional Amount is untouched");
    }

    [Fact]
    public void SecondRung_IsNotAnInterestShortfall()
    {
        // A shortfall is unpaid interest the deal still owes and later repays. A Modification
        // Loss Amount allocated against an Interest Accrual Amount is a permanent reduction of
        // what is DUE — reversible only by a Modification Gain Amount, down its own priority
        // list. Booking it as a shortfall would repay it out of ordinary interest and silently
        // turn the loss into a timing difference.
        var (_, cf) = Ladder().BuildAndRun(OnePeriod(modificationLoss: 1_050_000));

        var b = First(cf, "B");

        b.ModificationLoss.Should().BeApproximately(50_000, 1);
        b.InterestShortfall.Should().BeApproximately(0, 1,
            "the reduced coupon was paid in full, so nothing is owed");
        b.AccumInterestShortfall.Should().BeApproximately(0, 1);
    }

    [Fact]
    public void InterestRung_AbsorbsBeforeTheNotionalRungBelowItTakesAnything()
    {
        // THE POINT OF THE ISSUE. With a single-amount write-down leg, everything past C's
        // notional falls straight onto B's notional. The document puts an interest-accrual-sized
        // bite in between, and that bite is a sink: it consumes allocation without eroding credit
        // enhancement. Here 1,000,000 (C) + 100,000 (B's accrual) = 1,100,000 is absorbed before
        // B's notional is touched at all.
        var justInside = Ladder().BuildAndRun(OnePeriod(modificationLoss: 1_100_000));
        var justOutside = Ladder().BuildAndRun(OnePeriod(modificationLoss: 1_150_000));

        var bInside = First(justInside.Cashflows, "B");
        bInside.ModificationLoss.Should().BeApproximately(BMonthlyAccrual, 1,
            "the interest rung is filled to its Interest Accrual Amount");
        bInside.Writedown.Should().BeApproximately(0, 1,
            "and the notional rung below it is not reached");
        bInside.Balance.Should().BeApproximately(BBalance, 1);

        var bOutside = First(justOutside.Cashflows, "B");
        bOutside.ModificationLoss.Should().BeApproximately(BMonthlyAccrual, 1,
            "the interest rung is still capped at the accrual");
        bOutside.ModificationWritedown.Should().BeApproximately(50_000, 1,
            "only the excess over it reaches the third priority");
        bOutside.Balance.Should().BeApproximately(BBalance - 50_000, 1);
    }

    [Fact]
    public void CreditEventsConsumeNotionalCapacityFirst()
    {
        // "the Preliminary Principal Loss Amount, the Preliminary Tranche Write-down Amount ...
        // will be computed prior to the allocation of the Modification Loss Amount", and the
        // Preliminary Class Notional Amount each notional rung caps at is net of it. This step
        // runs BEFORE the WRITEDOWN step, so it has to anticipate that write-down rather than
        // observe it.
        //
        // C has 1,000,000; a 600,000 credit event claims most of it, leaving 400,000 for the
        // modification's first priority. The remaining 200,000 of a 600,000 modification then
        // fills B's 100,000 interest rung and the last 100,000 reaches B's notional rung —
        // the full three-rung cascade in one period.
        var (_, cf) = Ladder().BuildAndRun(OnePeriod(modificationLoss: 600_000, defaultedPrincipal: 600_000));

        var c = First(cf, "C");
        var b = First(cf, "B");
        var a = First(cf, "A");

        c.Writedown.Should().BeApproximately(1_000_000, 1,
            "C absorbs the credit event and the modification up to its whole notional");
        c.ModificationWritedown.Should().BeApproximately(400_000, 1,
            "of which only what the credit event left is the modification's");

        b.ModificationLoss.Should().BeApproximately(BMonthlyAccrual, 1,
            "the second priority fills to B's Interest Accrual Amount");
        b.ModificationWritedown.Should().BeApproximately(100_000, 1,
            "and only what that rung could not hold reaches the third priority");

        // Only the NOTIONAL bites transfer. The interest bite is not notional and must not be
        // included, which is what makes this assertion worth making: 400,000 + 100,000, not
        // 400,000 + 100,000 + 100,000.
        a.ModificationWritedown.Should().BeApproximately(-500_000, 1,
            "the senior takes on the notional bites only");
    }

    [Fact]
    public void ProRataInterestRung_CapsOnTheNamedMemberGrossedUpByItsWeight()
    {
        // "to the Class M-2B and Class M-2BH Reference Tranches, pro rata based on their Class
        // Notional Amounts ... until the amount allocated to the Class M-2B Reference Tranche is
        // equal to the Class M-2B Notes Interest Accrual Amount". The cap is stated on ONE
        // member, so the rung's total capacity is that member's accrual grossed up by its
        // pro-rata weight — the retained sibling takes its share on the way there.
        //
        // The two classes must carry DIFFERENT coupons or this test cannot tell the rule apart
        // from "cap at the pair's combined accrual" — with one coupon each accrual is exactly
        // proportional to balance and the two formulas coincide. (They did, and the first version
        // of this test passed against both.)
        //
        // The real configuration is starker than differing coupons, and it is what forecloses
        // the rival reading outright: the retained H sibling has NO Interest Accrual Amount at
        // all. The glossary defines the term "with respect to each outstanding Class of NOTES
        // (and, for purposes of calculating allocations of any Modification Gain Amounts or
        // Modification Loss Amounts, the Class B-1H and the Class B-2H Reference Tranches)" —
        // M-2BH is a Reference Tranche with no corresponding notes and is not in that carve-out,
        // so there is nothing to aggregate and "the pair's combined accrual" names no quantity.
        //
        // M (8,000,000 at 12%) accrues 80,000 and carries 0.8 of the pair's notional, so the rung
        // holds 80,000 / 0.8 = 100,000 and MH takes 20,000 of it. Capping on the pair's combined
        // accrual would hold 90,000 and split it 72,000 / 18,000.
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 90_000_000, 5.0, subOrder: 0)
            .WithTranche("M", 8_000_000, 12.0, subOrder: 1)
            .WithTranche("MH", 2_000_000, 6.0, subOrder: 2)
            .WithSequentialWaterfall("A", "M", "MH")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED",
                "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN")
            .WithPayRule("ModLossStruct",
                "SET_MODLOSS_STRUCT(ML_INTEREST(PRORATA('M','MH'), 'M'))")
            .BuildAndRun(OnePeriod(modificationLoss: 500_000));

        var m = First(cf, "M");
        var mh = First(cf, "MH");

        m.ModificationLoss.Should().BeApproximately(80_000, 1,
            "M's share stops at its own Interest Accrual Amount");
        mh.ModificationLoss.Should().BeApproximately(20_000, 1,
            "MH takes its pro-rata share of the NOTIONAL, which here exceeds its own 10,000 " +
            "accrual — the retained class's share is a calculation, not a cash effect");
        (m.ModificationLoss + mh.ModificationLoss).Should().BeApproximately(100_000, 1,
            "so the rung holds the capped member's accrual grossed up by its weight (100,000), " +
            "not the pair's combined accrual (90,000)");

        // A class allocated more than it accrues is paid nothing, never negative interest.
        mh.Interest.Should().BeApproximately(0, 1);
        m.Interest.Should().BeApproximately(0, 1, "M's whole 80,000 accrual was extinguished");
    }

    [Fact]
    public void WithoutAWriteUpClass_TheNotionalBiteIsAPureReduction()
    {
        // The opt-out, and the behaviour every deal that states no such transfer keeps. Asserted
        // rather than assumed, because the difference is invisible in the junior's own row: only
        // the senior's balance tells the two apart.
        var (_, cf) = Ladder(writeUpTranche: null).BuildAndRun(OnePeriod(modificationLoss: 400_000));

        First(cf, "C").ModificationWritedown.Should().BeApproximately(400_000, 1);
        First(cf, "A").Balance.Should().BeApproximately(89_000_000, 1,
            "with no declared write-up class the notional is simply gone");
    }

    [Fact]
    public void AmountWithNoLadder_IsIgnored_NotMisrouted()
    {
        // A posted Modification Loss Amount on a deal that states no Modification Loss Priority
        // must not fall through to the write-down leg — that is exactly the conflation this step
        // exists to end.
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 90_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 10_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(OnePeriod(modificationLoss: 500_000));

        First(cf, "B").Writedown.Should().Be(0, "no ladder, no allocation");
        First(cf, "B").Balance.Should().BeApproximately(10_000_000, 1);
    }

    [Fact]
    public void StepWithoutALadder_Throws()
    {
        // Both halves of the silent-no-model. The amount is posted, nothing books it, and the run
        // comes back a plausible number short — worse than not starting, because it looks fine.
        var act = () => new TestDealBuilder()
            .WithTranche("A", 90_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 10_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .BuildAndRun(OnePeriod(modificationLoss: 500_000));

        act.Should().Throw<Exception>().WithMessage("*MODIFICATION_LOSS step but no Modification Loss Priority*");
    }

    [Fact]
    public void LadderWithoutAStep_Throws()
    {
        var act = () => new TestDealBuilder()
            .WithTranche("A", 90_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 10_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithExecutionOrder("INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct", "SET_MODLOSS_STRUCT(ML_NOTIONAL(SINGLE('B')))")
            .BuildAndRun(OnePeriod(modificationLoss: 500_000));

        act.Should().Throw<Exception>().WithMessage("*no MODIFICATION_LOSS step*");
    }

    [Fact]
    public void ZeroAmount_ChangesNothing()
    {
        // Anti-vacuity for every test above: the ladder is wired and the step runs, so a run that
        // moves nothing has to be the amount's doing and not a ladder that never fired.
        var withLadder = Ladder().BuildAndRun(OnePeriod(modificationLoss: 0));
        var withoutLadder = new TestDealBuilder()
            .WithTranche("A", 89_000_000, 5.0, subOrder: 0)
            .WithTranche("B", BBalance, BCouponPct, subOrder: 1)
            .WithTranche("C", CBalance, 6.0, subOrder: 2)
            .WithSequentialWaterfall("A", "B", "C")
            .BuildAndRun(OnePeriod(modificationLoss: 0));

        foreach (var name in new[] { "A", "B", "C" })
        {
            First(withLadder.Cashflows, name).Balance.Should().BeApproximately(
                First(withoutLadder.Cashflows, name).Balance, 0.01,
                $"{name}'s balance must not move on a zero Modification Loss Amount");
            First(withLadder.Cashflows, name).Interest.Should().BeApproximately(
                First(withoutLadder.Cashflows, name).Interest, 0.01,
                $"{name}'s interest must not move either");
        }
    }

    [Fact]
    public void TheDslTheBuilderEmits_CompilesAndRuns()
    {
        // THE SEAM. Every other test here hand-writes the rule formula, and the builder tests
        // assert the emitted string — so the two meet only by my reading of both. This runs the
        // engine on rules the BUILDER produced from a step DTO, which is what a posted deal
        // actually travels through: a verb renamed on one side of that boundary and not the
        // other would pass both neighbours and fail here.
        var waterfall = new UnifiedWaterfallDto
        {
            Steps = new List<WaterfallStepDto>
            {
                new()
                {
                    Type = "MODIFICATION_LOSS",
                    WriteUpTranche = "A",
                    Rungs = new List<ModificationLossRungDto>
                    {
                        new() { Effect = "NOTIONAL", Tranche = "C" },
                        new() { Effect = "INTEREST", Tranche = "B" },
                        new() { Effect = "NOTIONAL", Tranche = "B" }
                    }
                }
            }
        };

        var builder = new TestDealBuilder()
            .WithTranche("A", 89_000_000, 5.0, subOrder: 0)
            .WithTranche("B", BBalance, BCouponPct, subOrder: 1)
            .WithTranche("C", CBalance, 6.0, subOrder: 2)
            .WithSequentialWaterfall("A", "B", "C")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED",
                "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN");

        foreach (var rule in UnifiedWaterfallBuilder.BuildPayRules(waterfall))
            builder.WithPayRule(rule.RuleName, rule.Formula);

        var (_, cf) = builder.BuildAndRun(OnePeriod(modificationLoss: 1_150_000));

        First(cf, "C").ModificationWritedown.Should().BeApproximately(1_000_000, 1);
        First(cf, "B").ModificationLoss.Should().BeApproximately(BMonthlyAccrual, 1);
        First(cf, "B").ModificationWritedown.Should().BeApproximately(50_000, 1);
        First(cf, "A").ModificationWritedown.Should().BeApproximately(-1_050_000, 1);
    }

    // ---- guards: every one of these was a SILENT wrong answer before ----------------------

    [Fact]
    public void AStepPlacedAfterInterest_Throws()
    {
        // The interest priorities reduce the SAME Payment Date's Interest Payment Amount, which
        // PayInterest has already paid by then — so every one of them consumes the amount and
        // reduces nothing, and on an agency-CRT ladder that is most of the allocation. The
        // validator used to check only that the step was PRESENT while its own error text
        // asserted the ordering it never checked.
        var act = () => Ladder()
            .WithExecutionOrder("INTEREST", "MODIFICATION_LOSS", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .BuildAndRun(OnePeriod(modificationLoss: 400_000));

        act.Should().Throw<Exception>().WithMessage("*after INTEREST*");
    }

    [Fact]
    public void AStepPlacedAfterWritedown_Throws()
    {
        // It reserves each notional priority's capacity for a write-down that has ALREADY been
        // applied, so every priority is docked twice and the amount cascades to classes senior
        // to where the document puts it.
        var act = () => Ladder()
            .WithExecutionOrder("WRITEDOWN", "MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED")
            .BuildAndRun(OnePeriod(modificationLoss: 400_000));

        act.Should().Throw<Exception>().WithMessage("*after WRITEDOWN*");
    }

    [Fact]
    public void AWriteUpClassTheRosterDoesNotCarry_Throws()
    {
        // "A-H" against a roster spelling it "AH" — assembly emits both. The transfer was
        // skipped by a null guard and the run looked complete while giving credit support
        // (S-X)/(T-X) instead of (S-X)/T, so a Minimum Credit Enhancement Test trips LATE.
        var act = () => Ladder(writeUpTranche: "A-H")
            .BuildAndRun(OnePeriod(modificationLoss: 400_000));

        // Raised inside a compiled pay rule, so it arrives wrapped in a
        // TargetInvocationException whose own message is "Exception has been thrown by the
        // target of an invocation." — assert on the chain, not the outer message.
        act.Should().Throw<Exception>().Which.ToString().Should().Contain("no such class");
    }

    [Fact]
    public void TheWriteUpSurvivesRulesDeclaredInEitherOrder()
    {
        // SET_MODLOSS_STRUCT replaced the whole ladder object, so a write-up declared FIRST was
        // silently dropped — the exact loss the comment on SET_MODLOSS_WRITEUP claimed to
        // prevent. The builder emits them in the safe order; a hand-authored rule set need not.
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 89_000_000, 5.0, subOrder: 0)
            .WithTranche("B", BBalance, BCouponPct, subOrder: 1)
            .WithTranche("C", CBalance, 6.0, subOrder: 2)
            .WithSequentialWaterfall("A", "B", "C")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossWriteup", "SET_MODLOSS_WRITEUP('A')")
            .WithPayRule("ModLossStruct", "SET_MODLOSS_STRUCT(ML_NOTIONAL(SINGLE('C')))")
            .BuildAndRun(OnePeriod(modificationLoss: 400_000));

        First(cf, "A").Balance.Should().BeApproximately(89_400_000, 1,
            "the write-up class declared before the ladder must survive the ladder being set");
    }

    [Fact]
    public void AWriteUpWithNoRungs_Throws()
    {
        // SET_MODLOSS_WRITEUP mints a rung-less ladder on its own, so a rule set with the
        // write-up verb and a missing SET_MODLOSS_STRUCT used to validate clean, take the posted
        // amount, allocate none of it, and return a plausible grid.
        var act = () => new TestDealBuilder()
            .WithTranche("A", 90_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 10_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossWriteup", "SET_MODLOSS_WRITEUP('A')")
            .BuildAndRun(OnePeriod(modificationLoss: 400_000));

        act.Should().Throw<Exception>().WithMessage("*no Modification Loss Priority*");
    }

    [Fact]
    public void ARungNamingAClassTheRosterDoesNotCarry_Throws()
    {
        // A one-character typo used to convert a NOTIONAL bite into an INTEREST one — the two
        // rung kinds this whole step exists to distinguish. SINGLE returns null for an unknown
        // name, the rung's capacity came out zero, and the step moved on: the priority was not
        // lost quietly, it shifted the whole remaining allocation up the ladder.
        //
        // Measured on the STACR sample before the fix: mistyping the first rung moved 18,634,842
        // out of B-3H's notional and into B-2H's interest, with no error.
        var act = () => new TestDealBuilder()
            .WithTranche("A", 89_000_000, 5.0, subOrder: 0)
            .WithTranche("B", BBalance, BCouponPct, subOrder: 1)
            .WithTranche("C", CBalance, 6.0, subOrder: 2)
            .WithSequentialWaterfall("A", "B", "C")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct",
                "SET_MODLOSS_STRUCT(ML_NOTIONAL(SINGLE('CX')), ML_INTEREST(SINGLE('B')))")
            .BuildAndRun(OnePeriod(modificationLoss: 400_000));

        act.Should().Throw<Exception>().Which.ToString()
            .Should().Contain("names no class in group");
    }

    [Fact]
    public void ACapClassTheRungDoesNotAllocateTo_Throws()
    {
        // It used to make the rung take NOTHING and cascade to a more senior class, with no log
        // and no exception — while a comment claimed the step "reports the skew".
        var act = () => new TestDealBuilder()
            .WithTranche("A", 90_000_000, 5.0, subOrder: 0)
            .WithTranche("M", 8_000_000, 12.0, subOrder: 1)
            .WithTranche("MH", 2_000_000, 6.0, subOrder: 2)
            .WithSequentialWaterfall("A", "M", "MH")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct", "SET_MODLOSS_STRUCT(ML_INTEREST(PRORATA('M','MH'), 'A'))")
            .BuildAndRun(OnePeriod(modificationLoss: 400_000));

        act.Should().Throw<Exception>().Which.ToString()
            .Should().Contain("not one of the classes it allocates to");
    }

    [Fact]
    public void ANegativeAmount_IsRefused_NotNettedAway()
    {
        // A negative net is a Modification GAIN Amount, which runs its own seven-priority ladder
        // in the reverse direction. Discarding it as zero turns a reimbursement into a loss the
        // deal keeps, and the field is a plain double named for one side of the netting — so
        // posting the signed net is the natural mistake to make.
        var act = () => Ladder().BuildAndRun(OnePeriod(modificationLoss: -50_000));

        act.Should().Throw<Exception>().WithMessage("*Modification Gain Amount*");
    }

    [Fact]
    public void AnExcessSpreadStripBesideTheLadder_IsRefused()
    {
        // Excess spread absorbs a credit-event loss before the funded classes see it, and
        // WritedownCapacity returns 0 for the strip — so the reservation walk cannot see it,
        // over-reserves junior notional, and allocates the modification too far up the stack.
        var act = () => new TestDealBuilder()
            .WithTranche("A", 89_000_000, 5.0, subOrder: 0)
            .WithTranche("C", CBalance, 6.0, subOrder: 1)
            .WithTranche("XS", 100_000_000, 0.0, subOrder: 2,
                cashflowType: "IO", couponType: "ResidualInterest")
            .WithSequentialWaterfall("A", "C")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct", "SET_MODLOSS_STRUCT(ML_NOTIONAL(SINGLE('C')))")
            .BuildAndRun(OnePeriod(modificationLoss: 100_000));

        act.Should().Throw<Exception>().WithMessage("*ExcessInterest*");
    }

    [Fact]
    public void AnInterestRungSplitsWithinAClassByAccrual_NotByBalance()
    {
        // "any Modification Loss Amount that is allocable in the sixth or seventh priority ...
        // will be allocated to reduce the Interest Payment Amounts ... pro rata, based on their
        // Interest Accrual Amounts". A class holding several tranches is usually holding them
        // BECAUSE their coupons differ, so balance and accrual are different bases.
        //
        // MACR (6,000,000 at 12%) accrues 60,000; Q (6,000,000 at 4%) accrues 20,000. Equal
        // balances, so a balance split gives 50/50; the accrual split gives 75/25.
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 88_000_000, 5.0, subOrder: 0)
            .WithTranche("MACR", 6_000_000, 12.0, subOrder: 1)
            .WithTrancheInClass("MACR", "Q", 6_000_000, 4.0)
            .WithSequentialWaterfall("A", "MACR")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct", "SET_MODLOSS_STRUCT(ML_INTEREST(SINGLE('MACR')))")
            .BuildAndRun(OnePeriod(modificationLoss: 40_000));

        First(cf, "MACR").ModificationLoss.Should().BeApproximately(30_000, 1,
            "75% of the class's accrual is MACR's, so 75% of the bite is");
        First(cf, "Q").ModificationLoss.Should().BeApproximately(10_000, 1,
            "not the 20,000 an equal-balance split would give");
    }

    // ---- seams the first round of tests left unpinned ------------------------------------

    [Fact]
    public void OnlyNotionalRungsAreDockedForTheCreditEventToCome()
    {
        // The `if (rung.Effect == Notional)` guard on the reservation was unpinned: the earlier
        // fixture used a credit event SMALLER than the first rung's notional, so the reservation
        // was fully consumed at rung 1 and was already zero by the time an interest rung was
        // reached. Symmetric in exactly the dimension it needed to distinguish.
        //
        // 1,200,000 of credit events against C's 1,000,000 leaves 200,000 still to come when the
        // interest rung is visited. An interest bite reduces no notional, so it must not be
        // docked for a write-down — otherwise 100,000 migrates from a bite that leaves credit
        // enhancement alone to one that erodes it.
        var (_, cf) = Ladder().BuildAndRun(
            OnePeriod(modificationLoss: 600_000, defaultedPrincipal: 1_200_000));

        var b = First(cf, "B");
        b.ModificationLoss.Should().BeApproximately(BMonthlyAccrual, 1,
            "the interest rung keeps its whole Interest Accrual Amount");
        b.ModificationWritedown.Should().BeApproximately(500_000, 1,
            "and only the remainder reaches the notional rung below it");
    }

    [Fact]
    public void TheNettedInterestDueSizesAProRataAsk_SoNoFundsAreStranded()
    {
        // `DynamicClass.InterestDue` is what sizes a class's share inside a PRORATA interest
        // cascade. Left gross, the modified class asks for interest its own PayInterest then
        // refuses, and the funds are stranded ABOVE the classes below it rather than paid on.
        // Unpinned until now, and on the live path for this deal family.
        //
        // B accrues 100,000 and is modified by 50,000, so it should ask 50,000. C accrues
        // 50,000. With 120,000 available both are paid in full; asking gross gives B two thirds
        // of the pot and leaves C 10,000 short.
        var (_, cf) = new TestDealBuilder()
            .WithTranche("B", 10_000_000, 12.0, subOrder: 0)
            .WithTranche("C", 10_000_000, 6.0, subOrder: 1)
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(PRORATA('B','C'))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('B'), SINGLE('C')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('B'), SINGLE('C')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('B'), SINGLE('C')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('C'), SINGLE('B')))")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct", "SET_MODLOSS_STRUCT(ML_INTEREST(SINGLE('B')))")
            .BuildAndRun(OnePeriodWithInterest(modificationLoss: 50_000, interest: 120_000));

        First(cf, "B").ModificationLoss.Should().BeApproximately(50_000, 1);
        First(cf, "B").Interest.Should().BeApproximately(50_000, 1, "B asks only what it is due");
        First(cf, "C").Interest.Should().BeApproximately(50_000, 1,
            "so C is paid in full rather than short by B's over-ask");
    }

    [Fact]
    public void AStarvedClassAccruesOnlyTheUnmodifiedRemainderAsShortfall()
    {
        // The shortfall netting was pinned on the PAID path only. A class that receives NOTHING
        // takes the other branch — AccrueUnpaidInterest — and booked its FULL coupon as a
        // shortfall, so the permanent modification became interest the deal repays later. That
        // is the timing-difference failure this netting exists to prevent, on the branch no test
        // reached.
        //
        // A is senior and takes the whole 100,000 of collateral interest, so B is paid nothing.
        // B accrues 100,000 and is modified by 50,000, so it is owed 50,000, not 100,000.
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 89_000_000, 5.0, subOrder: 0)
            .WithTranche("B", BBalance, BCouponPct, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct", "SET_MODLOSS_STRUCT(ML_INTEREST(SINGLE('B')))")
            .BuildAndRun(OnePeriodWithInterest(modificationLoss: 50_000, interest: 100_000));

        var b = First(cf, "B");
        b.Interest.Should().BeApproximately(0, 1, "nothing reached B");
        b.ModificationLoss.Should().BeApproximately(50_000, 1);
        b.InterestShortfall.Should().BeApproximately(BMonthlyAccrual - 50_000, 1,
            "only the part the modification did NOT extinguish is still owed");
    }

    [Fact]
    public void ARolledBackCellKeepsItsModificationStamps()
    {
        // TrancheCashflow.Copy is what StartTrans/Rollback preserve a period across. Dropping
        // the two new fields from it changed nothing in the suite, so a rolled-back cell would
        // silently lose the modification stamps.
        var tcf = new TrancheCashflow(FirstPayDate, "B")
        {
            ModificationLoss = 1_234.0,
            ModificationWritedown = 5_678.0
        };

        var copy = tcf.Copy();

        copy.ModificationLoss.Should().Be(1_234.0);
        copy.ModificationWritedown.Should().Be(5_678.0);
    }

    [Fact]
    public void ANotionalRungStampsAClassThatReceivesNoPrincipal()
    {
        // The measure of what a notional rung APPLIED is the balance delta, not the CumWritedown
        // delta — `DynamicClass.Writedown` reduces the balance unconditionally but advances
        // CumWritedown only `if (RecievesPrincipal())`. An interest-only tranche overrides that
        // to false, so its notional fell while the delta read zero: the reported
        // ModificationWritedown was 0 and the pseudo-class propagation never fired.
        //
        // Every other ladder fixture holds PI-only classes, which are symmetric in exactly the
        // dimension that separates the two measures — so this was fixed but unpinned.
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 89_000_000, 5.0, subOrder: 0)
            .WithTranche("C", CBalance, 6.0, subOrder: 1)
            .WithTrancheInClass("C", "C-IO", CBalance, 3.0, cashflowType: "IO")
            .WithSequentialWaterfall("A", "C")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct", "SET_MODLOSS_STRUCT(ML_NOTIONAL(SINGLE('C')))")
            .BuildAndRun(OnePeriod(modificationLoss: 400_000));

        First(cf, "C-IO").ModificationWritedown.Should().BeGreaterThan(0,
            "the IO tranche's notional fell, so the stamp must record it — CumWritedown does not");
    }

    [Fact]
    public void AProRataNotionalRungSplitsByNotionalAndTransfersTheWholeBite()
    {
        // STACR's eighth, ninth, eleventh and thirteenth priorities are pro-rata NOTIONAL, and
        // only the pro-rata INTEREST rung had a test. The document caps these on the AGGREGATE
        // ("until the aggregate amount allocated ... is equal to the aggregate of the
        // Preliminary Class Notional Amounts"), unlike their interest twins.
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 89_000_000, 5.0, subOrder: 0)
            .WithTranche("M", 800_000, 12.0, subOrder: 1)
            .WithTranche("MH", 200_000, 6.0, subOrder: 2)
            .WithSequentialWaterfall("A", "M", "MH")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct",
                "SET_MODLOSS_STRUCT(ML_NOTIONAL(PRORATA('M','MH')))")
            .WithPayRule("ModLossWriteup", "SET_MODLOSS_WRITEUP('A')")
            .BuildAndRun(OnePeriod(modificationLoss: 500_000));

        First(cf, "M").ModificationWritedown.Should().BeApproximately(400_000, 1,
            "pro rata on Class Notional Amount, 800k of 1,000k");
        First(cf, "MH").ModificationWritedown.Should().BeApproximately(100_000, 1);
        First(cf, "A").ModificationWritedown.Should().BeApproximately(-500_000, 1,
            "and the whole bite transfers, not just one leg of it");
    }

    [Fact]
    public void AnInterestRungStampsTheClassRowAsWellAsItsTranches()
    {
        // The notional counterpart stamps both levels and the controller serializes both
        // collections, so stamping only the tranche rows gave a class-row reader the notional
        // bite and never the interest one.
        var (_, cf) = Ladder().BuildAndRun(OnePeriod(modificationLoss: 1_050_000));

        Class(cf, "B").ModificationLoss.Should().BeApproximately(50_000, 1);
    }

    private static TrancheCashflow Class(DealCashflows dealCashflows, string trancheName)
    {
        var match = dealCashflows.ClassCashflows.First(t => t.Key.TrancheName == trancheName);
        return match.Value.Cashflows.OrderBy(c => c.Key).First().Value;
    }

    // ---- round-4: the newest fixes, and the round-3 changes that were unpinned ------------

    [Fact]
    public void AProRataRungThatOnlyHalfResolves_Throws()
    {
        // PRORATA drops names it cannot find, and "any leaves at all" accepted the remainder —
        // so a two-class rung became a one-class rung still sized for two. Measured: 20,000
        // migrated from an interest bite (which leaves credit enhancement alone) to a notional
        // one (which erodes it), with no error. The builder states the arity it emitted.
        var act = () => new TestDealBuilder()
            .WithTranche("A", 90_000_000, 5.0, subOrder: 0)
            .WithTranche("M", 8_000_000, 12.0, subOrder: 1)
            .WithTranche("MH", 2_000_000, 6.0, subOrder: 2)
            .WithSequentialWaterfall("A", "M", "MH")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct",
                "SET_MODLOSS_STRUCT(ML_INTEREST(PRORATA('M','MHX'), 'M', 2))")
            .BuildAndRun(OnePeriod(modificationLoss: 500_000));

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("only 1 resolved");
    }

    [Fact]
    public void AMistypedSingleClassInterestRung_ThrowsSomethingReadable()
    {
        // The two-argument overload is the only one the builder emits for an interest rung, and
        // it checked its target not at all — a SINGLE that did not resolve dereferenced null, so
        // the deal author got "Object reference not set to an instance of an object."
        var act = () => new TestDealBuilder()
            .WithTranche("A", 90_000_000, 5.0, subOrder: 0)
            .WithTranche("M", 8_000_000, 12.0, subOrder: 1)
            .WithSequentialWaterfall("A", "M")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct", "SET_MODLOSS_STRUCT(ML_INTEREST(SINGLE('MX'), 'MX'))")
            .BuildAndRun(OnePeriod(modificationLoss: 500_000));

        var text = act.Should().Throw<Exception>().Which.ToString();
        text.Should().Contain("names no class in group");
        text.Should().NotContain("Object reference not set");
    }

    [Fact]
    public void TheReservationIsReadOffTheWritedownLaddersOwnOrder()
    {
        // The step reserves each notional rung's capacity for the period's credit-event
        // write-down. It used to walk the MODIFICATION ladder's own order and assume the two
        // coincide; they need not. Here the write-down ladder is B-then-C while the modification
        // ladder is C-then-B, so a 1,000,000 credit event lands on B, and C's rung is undocked.
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 89_000_000, 5.0, subOrder: 0)
            .WithTranche("B", BBalance, BCouponPct, subOrder: 1)
            .WithTranche("C", CBalance, 6.0, subOrder: 2)
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('C')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('C')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('C')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('C')))")
            // write-down hits B FIRST
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('B'), SINGLE('C'), SINGLE('A')))")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            // ...but the modification ladder is C FIRST
            .WithPayRule("ModLossStruct",
                "SET_MODLOSS_STRUCT(ML_NOTIONAL(SINGLE('C'), 1), ML_NOTIONAL(SINGLE('B'), 1))")
            .BuildAndRun(OnePeriod(modificationLoss: 400_000, defaultedPrincipal: 1_000_000));

        First(cf, "C").ModificationWritedown.Should().BeApproximately(400_000, 1,
            "the credit event goes to B under the deal's own write-down order, so C's rung is "
            + "undocked and absorbs the whole modification");
        First(cf, "B").ModificationWritedown.Should().BeApproximately(0, 1);
    }

    [Fact]
    public void ADuplicatedClassInTheWritedownLadderIsReservedOnce()
    {
        // `OrderedLeaves` walks the declared tree, where a class can appear in more than one leg;
        // `IPayable.Leafs()` returns a HashSet and dedupes. Undeduped, a repeated class had its
        // capacity counted twice against the write-down and its earlier reservation overwritten.
        // B IS SIZED SO ITS RESERVED CAPACITY BINDS. With B at 10,000,000 the deduped capacity
        // (10,000,000 - 500,000) and the undeduped one (10,000,000) BOTH exceed the 1,500,000 on
        // offer, so the rung takes the same amount either way and the test passes against both
        // rules — which is what the first version of it did. At 1,600,000 the two diverge.
        const double bBinding = 1_600_000;
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 89_000_000, 5.0, subOrder: 0)
            .WithTranche("B", bBinding, BCouponPct, subOrder: 1)
            .WithTranche("C", CBalance, 6.0, subOrder: 2)
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('C')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('C')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('C')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('C')))")
            .WithPayRule("WritedownStruct",
                "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('C'), SINGLE('C'), SINGLE('B'), SINGLE('A')))")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct",
                "SET_MODLOSS_STRUCT(ML_NOTIONAL(SINGLE('C'), 1), ML_NOTIONAL(SINGLE('B'), 1))")
            .BuildAndRun(OnePeriod(modificationLoss: 1_500_000, defaultedPrincipal: 1_500_000));

        // C holds 1,000,000 and the credit event takes all of it ONCE; the remaining 500,000 of
        // write-down reserves against B, leaving B's rung 1,600,000 - 500,000 = 1,100,000.
        // Counting C twice would consume write-down that does not exist, leave B undocked, and
        // let it absorb the whole 1,500,000.
        First(cf, "C").ModificationWritedown.Should().BeApproximately(0, 1);
        First(cf, "B").ModificationWritedown.Should().BeApproximately(1_100_000, 1,
            "counting C twice would leave B's rung undocked and absorbing 1,500,000");
    }

    [Fact]
    public void AnExcessSpreadStripIsAllowedWhenItsAbsorberDoesNotRun()
    {
        // The refusal is about `AbsorbLossFromExcessSpread`, which only runs when the deal has
        // no OC target and no EXCESS step. Refusing on the strip's mere presence rejected deals
        // where the reservation walk is exact.
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 89_000_000, 5.0, subOrder: 0)
            .WithTranche("C", CBalance, 6.0, subOrder: 1)
            .WithTranche("XS", 100_000_000, 0.0, subOrder: 2,
                cashflowType: "IO", couponType: "ResidualInterest")
            .WithSequentialWaterfall("A", "C")
            .WithOcTarget(1.0, 100_000)
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct", "SET_MODLOSS_STRUCT(ML_NOTIONAL(SINGLE('C'), 1))")
            .BuildAndRun(OnePeriod(modificationLoss: 100_000));

        First(cf, "C").ModificationWritedown.Should().BeApproximately(100_000, 1);
    }

    [Fact]
    public void AFloatingPointNegativeIsToleratedButARealGainIsNot()
    {
        // The pre-first-pay fold sums stub periods, so cancellation can produce -1e-14. Its
        // positive twin has a 0.005 tolerance; without a matching one this hard-failed a run.
        var tiny = () => Ladder().BuildAndRun(OnePeriod(modificationLoss: -0.0000001));
        tiny.Should().NotThrow();

        var real = () => Ladder().BuildAndRun(OnePeriod(modificationLoss: -50_000));
        real.Should().Throw<Exception>().Which.ToString().Should().Contain("Modification Gain Amount");
    }

    [Fact]
    public void ADealWithNoLadderKeepsANegativeAccrualUnclamped()
    {
        // The netting helper is an IDENTITY when nothing was booked, deliberately: an
        // unconditional Math.Max would clamp a negative accrual (a Formula coupon can produce
        // one) on every deal, including the ones this change is supposed to leave alone.
        var cf = new TrancheCashflow(FirstPayDate, "B");
        DynamicClass.NetOfModification(-1_000.0, cf).Should().Be(-1_000.0);

        cf.ModificationLoss = 200.0;
        DynamicClass.NetOfModification(1_000.0, cf).Should().Be(800.0);
        DynamicClass.NetOfModification(100.0, cf).Should().Be(0.0, "netted, then floored");
    }

    [Fact]
    public void ALockedOutClassInARungStillAbsorbs_SoNothingIsDropped()
    {
        // WHY A RUNG NEVER ABSORBS LESS THAN IT IS OFFERED. `RungCapacity` reads
        // WritedownCapacity, which is lockout-BLIND, while `SequentialStructure.PayWritedown`
        // skips a locked-out payable on its first pass — which looks like it could leave a
        // rung short. It cannot: that structure re-runs its residual with lockout IGNORED, so
        // the locked-out class absorbs after all.
        //
        // This is the invariant that makes "charge what was applied" unnecessary, and it is
        // worth pinning rather than assuming: if it ever stopped holding, the ladder would
        // silently drop the difference instead of passing it to the priority below.
        //
        // Rung one is SEQ(D, C) with D locked out and 1,400,000 offered against their combined
        // 1,400,000 of notional.
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", 88_000_000, 5.0, subOrder: 0)
            .WithTranche("B", BBalance, BCouponPct, subOrder: 1)
            .WithTranche("C", CBalance, 6.0, subOrder: 2)
            .WithTranche("D", 400_000, 6.0, subOrder: 3)
            .WithPayRule("Lock", "LOCKOUT('D')")
            .WithPayRule("InterestStruct",
                "SET_INTEREST_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('C'), SINGLE('D')))")
            .WithPayRule("SchedStruct",
                "SET_SCHED_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('C'), SINGLE('D')))")
            .WithPayRule("PrepayStruct",
                "SET_PREPAY_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('C'), SINGLE('D')))")
            .WithPayRule("RecovStruct",
                "SET_RECOV_STRUCT(SEQ(SINGLE('A'), SINGLE('B'), SINGLE('C'), SINGLE('D')))")
            .WithPayRule("WritedownStruct",
                "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('D'), SINGLE('C'), SINGLE('B'), SINGLE('A')))")
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN")
            .WithPayRule("ModLossStruct",
                "SET_MODLOSS_STRUCT(ML_NOTIONAL(SEQ(SINGLE('D'), SINGLE('C')), 2), "
                + "ML_NOTIONAL(SINGLE('B'), 1))")
            .BuildAndRun(OnePeriod(modificationLoss: 1_400_000));

        var applied = First(cf, "C").ModificationWritedown + First(cf, "D").ModificationWritedown;
        applied.Should().BeApproximately(1_400_000, 1,
            "the locked-out class absorbs on the residual pass, so the rung takes its whole "
            + "offer and nothing is left behind");
        First(cf, "B").ModificationWritedown.Should().BeApproximately(0, 1,
            "and the priority below is therefore not reached");
    }

    private static TrancheCashflow First(DealCashflows dealCashflows, string trancheName)
    {
        var match = dealCashflows.TrancheCashflows.First(t => t.Key.TrancheName == trancheName);
        return match.Value.Cashflows.OrderBy(c => c.Key).First().Value;
    }
}
