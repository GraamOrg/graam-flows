using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
///     `cum_mod_writedown`: the Modification Loss Amounts the ladder's NOTIONAL priorities have
///     written off, readable by a trigger formula.
///
///     Agency CRT's Cumulative Net Loss Percentage is built on the Principal Loss Amount, and the
///     Principal Loss Amount includes "the amounts included in the first, third, fifth, eighth,
///     ninth, eleventh and thirteenth priorities" of the Modification Loss Priority — the notional
///     ones. `ccl` is the pool's own loss and cannot see them, so a test written `ccl / ib`
///     passed where the document's failed and the mezzanine kept its principal. With modification
///     losses on, that was most of a deal's out-of-tolerance tie-out cells (graam-harmony #6264).
///
///     The tests run BEFORE the step, so the period's own bite is planned ahead of them; without
///     that the test reads a Payment Date late.
/// </summary>
public class ModificationLossInLossTestsTests
{
    private static readonly DateTime P1 = TestConstants.DefaultFirstPayDate;
    private static readonly DateTime P2 = P1.AddMonths(1);
    private static readonly DateTime P3 = P1.AddMonths(2);

    // 12% on 10,000,000: 100,000 a month of interest-rung capacity on B.
    private const double BBalance = 10_000_000;
    private const double CBalance = 1_000_000;

    private static TestDealBuilder Ladder(bool withLadder = true)
    {
        var builder = new TestDealBuilder()
            .WithTranche("A", 89_000_000, 5.0, subOrder: 0)
            .WithTranche("B", BBalance, 12.0, subOrder: 1)
            .WithTranche("C", CBalance, 6.0, subOrder: 2)
            .WithSequentialWaterfall("A", "B", "C")
            .WithFormulaTrigger("ModProbe", "cum_mod_writedown", "FORMULA_VALUE");

        if (!withLadder)
            return builder.WithExecutionOrder("INTEREST", "PRINCIPAL_SCHEDULED",
                "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN");

        return builder
            .WithExecutionOrder("MODIFICATION_LOSS", "INTEREST", "PRINCIPAL_SCHEDULED",
                "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN")
            .WithPayRule("ModLossStruct",
                "SET_MODLOSS_STRUCT(ML_NOTIONAL(SINGLE('C')), ML_INTEREST(SINGLE('B')), " +
                "ML_NOTIONAL(SINGLE('B')))")
            .WithPayRule("ModLossWriteup", "SET_MODLOSS_WRITEUP('A')");
    }

    private static CollateralCashflows Periods(params double[] modificationLosses)
    {
        var builder = new TestCollateralBuilder().WithGroupNum("1");
        for (var i = 0; i < modificationLosses.Length; i++)
            builder.WithPeriod(
                date: P1.AddMonths(i),
                beginBalance: 100_000_000,
                scheduledPrincipal: 0,
                unscheduledPrincipal: 0,
                interest: 100_000_000 * 0.08 / 12,
                modificationLoss: modificationLosses[i]);
        return builder.Build();
    }

    private static TriggerResult On(DealCashflows cf, string trigger, DateTime date)
    {
        return cf.TriggerResults.Single(t => t.TriggerName == trigger && t.CashflowDate == date);
    }

    [Fact]
    public void ThisPaymentDatesNotionalBite_IsCounted_BeforeTheTestsReadIt()
    {
        var (_, cf) = Ladder().BuildAndRun(Periods(0, 400_000, 300_000));

        // Both inside C's notional, so every dollar is a notional bite.
        On(cf, "ModProbe", P2).ActualValue.Should().BeApproximately(400_000, 1,
            "the tests run before the step, and the step's own total would only arrive next month");
        On(cf, "ModProbe", P3).ActualValue.Should().BeApproximately(700_000, 1,
            "cumulative: last month's bite plus this month's");
    }

    [Fact]
    public void InterestBites_AreNotPrincipalLosses()
    {
        // 1,050,000: C's whole 1,000,000 notional, then 50,000 off B's Interest Accrual Amount.
        var (_, cf) = Ladder().BuildAndRun(Periods(0, 1_050_000));

        On(cf, "ModProbe", P2).ActualValue.Should().BeApproximately(1_000_000, 1,
            "only the notional priorities are in the Principal Loss Amount; an interest bite " +
            "reduces what B is paid and leaves the reference pool's losses where they were");
    }

    [Fact]
    public void ACumulativeLossTest_FailsOnThePaymentDateTheBiteLands()
    {
        // ib is the 100,000,000 the group issued at. 600,000 of modification notional is 0.6%,
        // over a 0.5% limit that pool losses alone (zero here) never reach.
        var (_, cf) = Ladder()
            .WithFormulaTrigger("CumNetLossTest", "(ccl + cum_mod_writedown) / ib < 0.005")
            .WithFormulaTrigger("PoolOnlyTest", "ccl / ib < 0.005")
            .BuildAndRun(Periods(0, 600_000, 0));

        On(cf, "CumNetLossTest", P1).Passed.Should().BeTrue();
        On(cf, "CumNetLossTest", P2).Passed.Should().BeFalse(
            "the Payment Date the modification is written off is the one its test fails on");
        On(cf, "CumNetLossTest", P3).Passed.Should().BeFalse("and the loss stays counted");
        On(cf, "PoolOnlyTest", P2).Passed.Should().BeTrue(
            "the old formula never sees a modification loss — the miss this variable exists for");
    }

    [Fact]
    public void ThePlannedBite_IsWhatTheStepApplies()
    {
        // The plan and the step walk the same ladder. If they disagreed, the step refuses the run
        // rather than leave the tests reading a loss the deal did not book; a clean run over a
        // bite that spans two notional rungs and an interest rung is the check that they agree.
        var (_, cf) = Ladder().BuildAndRun(Periods(0, 1_000_000, 300_000));

        var cWritten = cf.TrancheCashflows.First(t => t.Key.TrancheName == "C").Value.Cashflows
            .Values.Sum(c => c.ModificationWritedown);
        var bWritten = cf.TrancheCashflows.First(t => t.Key.TrancheName == "B").Value.Cashflows
            .Values.Sum(c => c.ModificationWritedown);

        // P3: C is gone, so 100,000 goes to B's interest and 200,000 to B's notional.
        On(cf, "ModProbe", P3).ActualValue.Should().BeApproximately(cWritten + bWritten, 1,
            "what the tests counted is exactly what the step wrote off");
        On(cf, "ModProbe", P3).ActualValue.Should().BeApproximately(1_200_000, 1);
    }

    [Fact]
    public void ADealWithNoModificationLossPriority_ReadsZero()
    {
        var (_, cf) = Ladder(withLadder: false).BuildAndRun(Periods(0, 600_000));

        On(cf, "ModProbe", P2).ActualValue.Should().Be(0,
            "with no ladder nothing is written off, so an existing `ccl / ib` deal is unchanged " +
            "by adding the term");
    }
}
