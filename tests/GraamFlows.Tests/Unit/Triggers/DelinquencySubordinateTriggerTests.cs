using FluentAssertions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Triggers;
using Xunit;

namespace GraamFlows.Tests.Unit.Triggers;

/// <summary>
/// Regression tests for <see cref="DelinquencySubordinateTrigger"/>.
///
/// Reported by harmony issue #687: the breakeven-cdr-stack eval
/// (harmony PR #679) crashed with a NullReferenceException at every
/// CDR level when projecting an agent-assembled deal whose prospectus
/// extraction left the trigger threshold (<c>TriggerParam</c>) null.
/// The pre-fix code path:
/// <c>_threshold (null) -> GetTriggerThreshold -> double.TryParse(null)
/// (returns false) -> DynamicGroup.GetVariable(null, ...) -> NRE</c>
/// was deterministic on both ends of the search bracket (0.5% and
/// 20.0%), so no tranche could be priced.
///
/// The fix treats a null/empty threshold as "trigger never fires" —
/// the safe default that matches how a deal with no delinquency
/// trigger at all behaves. A `TriggerValue.RequiredValue == 0` makes
/// the no-op visible to trace consumers.
/// </summary>
public class DelinquencySubordinateTriggerTests
{
    [Fact]
    public void TestTrigger_NullThreshold_DoesNotThrowAndReturnsNonFiring()
    {
        // Arrange — assemble a delinquency trigger with TriggerParam=null,
        // matching what harmony emits when the prospectus extractor
        // didn't populate a threshold.
        var deal = new TestDealBuilder()
            .WithTranche("A", balance: 1000, couponPct: 5.0, subOrder: 0)
            .Build();

        var trigger = new DealTrigger
        {
            DealName = deal.DealName,
            TriggerName = "DqStepDownTrigger",
            TriggerType = "DELINQ_TRIGGER_SUB_6",
            GroupNum = "1",
            TriggerParam = null!,   // ← the #687 reproducer
            TriggerParam2 = null!,  // no senior-tranche scoping
            TriggerFormula = null!,
            PossibleValues = null!,
        };

        // base Trigger ctor only stores `assumps` — never dereferences
        // it from this trigger's body. Same for `DynamicGroup` in
        // TestTrigger when the null-threshold guard fires.
        var sut = new DelinquencySubordinateTrigger(
            deal,
            trigger,
            assumps: null!,
            monthsAvg: 6,
            cashflows: new List<PeriodCashflows>());

        // Act + Assert — the guard fires before any of the otherwise-
        // dereferenced fields are touched, so passing null for the
        // dynamic-group parameter is safe AND proves the guard short-
        // circuited (no NRE).
        var act = () => sut.TestTrigger(group: null!, cashflowDate: DateTime.Today,
            periodCf: new PeriodCashflows());

        act.Should().NotThrow<NullReferenceException>();

        var result = sut.TestTrigger(group: null!, cashflowDate: DateTime.Today,
            periodCf: new PeriodCashflows());

        result.Should().NotBeNull();
        result.TriggerName.Should().Be("DqStepDownTrigger");
        // TriggerResult=true == "not firing" == step-down proceeds.
        // Matches the existing `denom <= 0` no-op return at the same
        // line position; consistent semantics.
        result.TriggerResult.Should().BeTrue(
            "a missing threshold should not block step-down — that's the safe default");
        // RequiredValue == 0 signals "no real threshold" to downstream
        // trace consumers; lets users distinguish a no-op from a 0%
        // threshold (which the trigger format itself doesn't actually
        // support, but the explicit 0 vs absent contrast is the only
        // public signal).
        result.RequiredValue.Should().Be(0);
    }

    [Fact]
    public void TestTrigger_EmptyStringThreshold_DoesNotThrowAndReturnsNonFiring()
    {
        // Same guard, empty-string case — some agent paths emit ""
        // instead of null. `string.IsNullOrEmpty` covers both.
        var deal = new TestDealBuilder()
            .WithTranche("A", balance: 1000, couponPct: 5.0, subOrder: 0)
            .Build();

        var trigger = new DealTrigger
        {
            DealName = deal.DealName,
            TriggerName = "DqStepDownTrigger",
            TriggerType = "DELINQ_TRIGGER_SUB_6",
            GroupNum = "1",
            TriggerParam = "",  // empty — also the #687 hazard
            TriggerParam2 = null!,
            TriggerFormula = null!,
            PossibleValues = null!,
        };

        var sut = new DelinquencySubordinateTrigger(
            deal,
            trigger,
            assumps: null!,
            monthsAvg: 6,
            cashflows: new List<PeriodCashflows>());

        var result = sut.TestTrigger(group: null!, cashflowDate: DateTime.Today,
            periodCf: new PeriodCashflows());

        result.TriggerResult.Should().BeTrue();
        result.RequiredValue.Should().Be(0);
    }
}
