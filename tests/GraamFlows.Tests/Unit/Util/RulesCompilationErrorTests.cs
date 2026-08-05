using FluentAssertions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.RulesEngine;
using Xunit;

namespace GraamFlows.Tests.Unit.Util;

/// <summary>
/// Guards on <see cref="RulesBuilder.BuildCode(IList{IPayRule}, IList{IDealTrigger}, IList{ITranche})"/>.
///
/// Context: a deal with a tranche <c>couponType="Formula"</c> but a null/empty
/// <c>couponFormula</c> (harmony cashflow_runs 165d985a / da0818de) used to dereference
/// the missing formula and throw a bare <see cref="NullReferenceException"/> at
/// <c>RulesBuilder.cs:88</c> — a stack trace that named neither the tranche nor the field,
/// so the agent that authored the deal had nothing actionable to fix.
///
/// These tests assert the compiler now raises a <see cref="RulesCompilationException"/>
/// whose message identifies the offending entity and field for every formula-bearing
/// input (pay rule, formula trigger, formula-coupon tranche), and that a well-formed
/// formula-coupon tranche still compiles.
/// </summary>
public class RulesCompilationErrorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildCode_FormulaCouponTrancheWithoutFormula_ThrowsActionable(string? couponFormula)
    {
        var tranche = new Tranche
        {
            DealName = "TESTDEAL",
            TrancheName = "B3",
            CouponType = "Formula",
            CouponFormula = couponFormula,
        };

        var act = () => RulesBuilder.BuildCode(
            payRules: Array.Empty<IPayRule>(),
            triggers: Array.Empty<IDealTrigger>(),
            tranches: new ITranche[] { tranche });

        act.Should().Throw<RulesCompilationException>()
            .WithMessage("*B3*couponType*Formula*couponFormula*",
                because: "the message must name the tranche and the missing field so the deal can be fixed");
    }

    [Fact]
    public void BuildCode_FormulaCouponTrancheWithFormula_Compiles()
    {
        var tranche = new Tranche
        {
            DealName = "TESTDEAL",
            TrancheName = "B3",
            CouponType = "Formula",
            CouponFormula = "eff_wac",
        };

        var code = RulesBuilder.BuildCode(
            payRules: Array.Empty<IPayRule>(),
            triggers: Array.Empty<IDealTrigger>(),
            tranches: new ITranche[] { tranche });

        var assemblyStream = RulesBuilder.BuildAssembly(code);
        assemblyStream.Length.Should().BeGreaterThan(0,
            because: "a well-formed formula-coupon tranche should still compile to a loadable assembly");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildCode_PayRuleWithoutFormula_ThrowsActionable(string? formula)
    {
        var payRule = new PayRule
        {
            DealName = "TESTDEAL",
            RuleName = "PayInterest",
            ClassGroupName = "GroupA",
            Formula = formula,
        };

        var act = () => RulesBuilder.BuildCode(
            payRules: new IPayRule[] { payRule },
            triggers: Array.Empty<IDealTrigger>(),
            tranches: Array.Empty<ITranche>());

        act.Should().Throw<RulesCompilationException>()
            .WithMessage("*PayInterest*formula*",
                because: "the message must name the pay rule and the missing field");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildCode_FormulaTriggerWithoutFormula_ThrowsActionable(string? triggerFormula)
    {
        var trigger = new DealTrigger
        {
            DealName = "TESTDEAL",
            TriggerName = "DelinqTrigger",
            TriggerType = "FORMULA_CONDITION",
            GroupNum = "1",
            TriggerFormula = triggerFormula,
        };

        var act = () => RulesBuilder.BuildCode(
            payRules: Array.Empty<IPayRule>(),
            triggers: new IDealTrigger[] { trigger },
            tranches: Array.Empty<ITranche>());

        act.Should().Throw<RulesCompilationException>()
            .WithMessage("*DelinqTrigger*FORMULA_CONDITION*triggerFormula*",
                because: "the message must name the trigger, its type, and the missing field");
    }
}
