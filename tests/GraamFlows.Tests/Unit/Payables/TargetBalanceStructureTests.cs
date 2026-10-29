using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall;
using GraamFlows.Waterfall.Structures.PayableStructures;
using Xunit;

namespace GraamFlows.Tests.Unit.Payables;

/// <summary>
/// TARGET pays its senior leg down to a target BALANCE (a deal variable) and the rest to the
/// subordinate leg — "until the Class A Note Balance has been reduced to the Class A Target
/// Balance, second, to the Class B Noteholders …".
/// </summary>
public class TargetBalanceStructureTests
{
    private readonly DateTime _testDate = TestConstants.DefaultFirstPayDate;

    private sealed class Vars : IDealVariableProvider
    {
        private readonly Dictionary<string, object> _values = new();
        public int Reads { get; private set; }

        public void SetVariable(string varName, object varValue) => _values[varName] = varValue;

        public double GetVariable(string varName, DateTime? asOfDate = null)
        {
            Reads++;
            return Convert.ToDouble(_values.TryGetValue(varName, out var v) ? v : 0.0);
        }

        public object GetVariableObj(string varName, DateTime? asOfDate = null) =>
            _values.TryGetValue(varName, out var v) ? v : 0.0;
    }

    [Fact]
    public void PaySp_PaysTheSeniorDownToItsTarget_ThenTheSubordinate()
    {
        var vars = new Vars();
        vars.SetVariable("TargetA", 700.0);
        var seniors = TestPayableBuilder.Mock(balance: 800, name: "A");
        var subs = TestPayableBuilder.Mock(balance: 200, name: "B");
        var target = new TargetBalanceStructure(vars, "TargetA", seniors, subs);

        target.PaySp(null, _testDate, 150, () => { });

        seniors.TotalSpPaid.Should().Be(100, "A pays down to its 700 target");
        subs.TotalSpPaid.Should().Be(50);
    }

    [Fact]
    public void PaySp_ASeniorAlreadyAtItsTarget_PaysTheSubordinateOnly()
    {
        var vars = new Vars();
        vars.SetVariable("TargetA", 800.0);
        var seniors = TestPayableBuilder.Mock(balance: 800, name: "A");
        var subs = TestPayableBuilder.Mock(balance: 200, name: "B");

        new TargetBalanceStructure(vars, "TargetA", seniors, subs).PaySp(null, _testDate, 50, () => { });

        seniors.TotalSpPaid.Should().Be(0);
        subs.TotalSpPaid.Should().Be(50);
    }

    [Fact]
    public void PaySp_WhatTheSubordinateCannotAbsorb_GoesBackToTheSenior()
    {
        var vars = new Vars();
        vars.SetVariable("TargetA", 700.0);
        var seniors = TestPayableBuilder.Mock(balance: 800, name: "A");
        var subs = TestPayableBuilder.Mock(balance: 30, name: "B");

        new TargetBalanceStructure(vars, "TargetA", seniors, subs).PaySp(null, _testDate, 200, () => { });

        subs.TotalSpPaid.Should().Be(30);
        seniors.TotalSpPaid.Should().Be(170, "100 to its target, then the 70 B could not take");
    }

    [Fact]
    public void PaySp_AZeroTarget_IsSequential()
    {
        var vars = new Vars();
        vars.SetVariable("TargetA", 0.0);
        var seniors = TestPayableBuilder.Mock(balance: 800, name: "A");
        var subs = TestPayableBuilder.Mock(balance: 200, name: "B");

        new TargetBalanceStructure(vars, "TargetA", seniors, subs).PaySp(null, _testDate, 300, () => { });

        seniors.TotalSpPaid.Should().Be(300);
        subs.TotalSpPaid.Should().Be(0);
    }

    [Fact]
    public void PaySp_RunsThePayRulesBeforeReadingTheTarget()
    {
        // The target is a computed variable set by the period's pay rules: read before they run,
        // it is last period's.
        var vars = new Vars();
        vars.SetVariable("TargetA", 800.0);
        var seniors = TestPayableBuilder.Mock(balance: 800, name: "A");
        var subs = TestPayableBuilder.Mock(balance: 200, name: "B");

        new TargetBalanceStructure(vars, "TargetA", seniors, subs)
            .PaySp(null, _testDate, 100, () => vars.SetVariable("TargetA", 750.0));

        seniors.TotalSpPaid.Should().Be(50);
        subs.TotalSpPaid.Should().Be(50);
    }

    [Fact]
    public void Writedown_HitsTheSubordinateFirst()
    {
        var vars = new Vars();
        var seniors = TestPayableBuilder.Mock(balance: 800, name: "A");
        var subs = TestPayableBuilder.Mock(balance: 200, name: "B");

        new TargetBalanceStructure(vars, "TargetA", seniors, subs).PayWritedown(null, _testDate, 250, () => { });

        subs.TotalWritedownPaid.Should().Be(200);
        seniors.TotalWritedownPaid.Should().Be(50);
    }

    // ------------------------------------------------------------------ CSCAP edge cases

    [Fact]
    public void Cscap_ARetiredSenior_PaysTheSubordinate_AndReturns()
    {
        // Used to recurse on the same zero senior balance until the stack overflowed.
        var seniors = TestPayableBuilder.Mock(balance: 0, name: "A");
        var subs = TestPayableBuilder.Mock(balance: 500, name: "B");

        new EnhancementCapStructure(0.57, seniors, subs).PaySp(null, _testDate, 100, () => { });

        subs.TotalSpPaid.Should().Be(100);
        seniors.TotalSpPaid.Should().Be(0);
    }

    [Fact]
    public void Cscap_TheLastSeniorDollar_IsNotANaN()
    {
        // Senior 100, subordinates retired: paying 100 leaves 0 / 0 support.
        var seniors = TestPayableBuilder.Mock(balance: 100, name: "D");
        var subs = TestPayableBuilder.Mock(balance: 0, name: "E");

        new EnhancementCapStructure(0.5, seniors, subs).PaySp(null, _testDate, 100, () => { });

        seniors.TotalSpPaid.Should().Be(100);
        double.IsNaN(seniors.TotalSpPaid).Should().BeFalse();
    }

    // ------------------------------------------------------------------ full turbo

    [Fact]
    public void OcTarget_AtOrBelowTheFullTurboShare_IsTheWholePool()
    {
        var cfg = new OcTargetConfig
        {
            TargetPct = 0.01, FloorAmt = 4_000_000, FormulaType = "max",
            FullTurboPoolPct = 0.10, CutoffPoolBalance = 400_000_000
        };

        cfg.CalculateTargetOc(50_000_000).Should().Be(4_000_000, "above 10% of the cutoff pool");
        cfg.CalculateTargetOc(40_000_000).Should().Be(40_000_000, "at 10%: every note is turbo'd");
        cfg.CalculateTargetOc(10_000_000).Should().Be(10_000_000);
    }

    [Fact]
    public void OcTarget_WithoutAFullTurboShare_IsUnchanged()
    {
        var cfg = new OcTargetConfig { TargetPct = 0.01, FloorAmt = 4_000_000, FormulaType = "max" };

        cfg.CalculateTargetOc(10_000_000).Should().Be(4_000_000);
    }
}
