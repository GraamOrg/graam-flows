using FluentAssertions;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall.Structures.PayableStructures;
using Xunit;

namespace GraamFlows.Tests.Unit.Payables;

public class ShiftingInterestStructureTests
{
    private readonly DateTime _testDate = TestConstants.DefaultFirstPayDate;
    private readonly ConstantTestRateProvider _rateProvider = new();

    [Fact]
    public void PaySp_SplitsByShiftPercent()
    {
        var seniors = TestPayableBuilder.Mock(balance: 1000, name: "Senior");
        var subs = TestPayableBuilder.Mock(balance: 500, name: "Sub");
        var shifti = new ShiftingInterestStructure(0.7, seniors, subs);

        shifti.PaySp(null, _testDate, 1000, () => { });

        seniors.TotalSpPaid.Should().BeApproximately(700, 1);
        subs.TotalSpPaid.Should().BeApproximately(300, 1);
    }

    [Fact]
    public void PaySp_ZeroShift_AllToSubs()
    {
        var seniors = TestPayableBuilder.Mock(balance: 1000, name: "Senior");
        var subs = TestPayableBuilder.Mock(balance: 500, name: "Sub");
        var shifti = new ShiftingInterestStructure(0.0, seniors, subs);

        shifti.PaySp(null, _testDate, 400, () => { });

        seniors.TotalSpPaid.Should().Be(0);
        subs.TotalSpPaid.Should().BeApproximately(400, 1);
    }

    [Fact]
    public void PaySp_FullShift_AllToSeniors()
    {
        var seniors = TestPayableBuilder.Mock(balance: 1000, name: "Senior");
        var subs = TestPayableBuilder.Mock(balance: 500, name: "Sub");
        var shifti = new ShiftingInterestStructure(1.0, seniors, subs);

        shifti.PaySp(null, _testDate, 800, () => { });

        seniors.TotalSpPaid.Should().BeApproximately(800, 1);
        subs.TotalSpPaid.Should().Be(0);
    }

    [Fact]
    public void PaySp_OverflowFromSeniorsToSubs()
    {
        // Seniors can only absorb 200, rest overflows to subs
        var seniors = TestPayableBuilder.Mock(balance: 200, name: "Senior");
        var subs = TestPayableBuilder.Mock(balance: 1000, name: "Sub");
        var shifti = new ShiftingInterestStructure(1.0, seniors, subs);

        shifti.PaySp(null, _testDate, 500, () => { });

        seniors.TotalSpPaid.Should().Be(200);
        subs.TotalSpPaid.Should().BeApproximately(300, 1);
    }

    [Fact]
    public void PayInterest_SeniorsFirst_ThenSubs()
    {
        var seniors = TestPayableBuilder.Mock(interestDue: 500, name: "Senior");
        var subs = TestPayableBuilder.Mock(interestDue: 300, name: "Sub");
        var shifti = new ShiftingInterestStructure(0.5, seniors, subs);

        var paid = shifti.PayInterest(null, _testDate, 800, _rateProvider, null);

        paid.Should().Be(800);
        seniors.TotalInterestPaid.Should().Be(500);
        subs.TotalInterestPaid.Should().Be(300);
    }

    [Fact]
    public void PayInterest_InsufficientFunds_SeniorsPaidFirst()
    {
        var seniors = TestPayableBuilder.Mock(interestDue: 500, name: "Senior");
        var subs = TestPayableBuilder.Mock(interestDue: 300, name: "Sub");
        var shifti = new ShiftingInterestStructure(0.5, seniors, subs);

        var paid = shifti.PayInterest(null, _testDate, 600, _rateProvider, null);

        paid.Should().Be(600);
        seniors.TotalInterestPaid.Should().Be(500);
        subs.TotalInterestPaid.Should().Be(100);
    }

    [Fact]
    public void InterestDue_SumsBothChildren()
    {
        var seniors = TestPayableBuilder.Mock(interestDue: 500);
        var subs = TestPayableBuilder.Mock(interestDue: 300);
        var shifti = new ShiftingInterestStructure(0.5, seniors, subs);

        shifti.InterestDue(_testDate, _rateProvider, null).Should().Be(800);
    }

    [Fact]
    public void PayWritedown_DistributedByShiftPercent()
    {
        var seniors = TestPayableBuilder.Mock(balance: 1000, name: "Senior");
        var subs = TestPayableBuilder.Mock(balance: 500, name: "Sub");
        var shifti = new ShiftingInterestStructure(0.5, seniors, subs);

        shifti.PayWritedown(null, _testDate, 300, () => { });

        // Writedown distributed proportionally (same as principal)
        (seniors.TotalWritedownPaid + subs.TotalWritedownPaid).Should().BeApproximately(300, 1);
    }

    [Fact]
    public void Leafs_ReturnsBothChildren()
    {
        var seniors = TestPayableBuilder.Mock(name: "Senior");
        var subs = TestPayableBuilder.Mock(name: "Sub");
        var shifti = new ShiftingInterestStructure(0.5, seniors, subs);

        shifti.Leafs().Should().HaveCount(2);
    }

    [Fact]
    public void GetChildren_ReturnsTwoChildren()
    {
        var seniors = TestPayableBuilder.Mock(name: "Senior");
        var subs = TestPayableBuilder.Mock(name: "Sub");
        var shifti = new ShiftingInterestStructure(0.5, seniors, subs);

        shifti.GetChildren().Should().HaveCount(2);
    }
}
