using FluentAssertions;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall.Structures.PayableStructures;
using Xunit;

namespace GraamFlows.Tests.Unit.Payables;

/// <summary>
/// Tests for FixedStructure.
/// Pays a fixed amount to the primary tranche, remainder to support.
/// </summary>
public class FixedStructureTests
{
    private readonly DateTime _testDate = TestConstants.DefaultFirstPayDate;
    private readonly ConstantTestRateProvider _rateProvider = new();

    [Fact]
    public void PaySp_FixedAmountToFirst_RemainderToSupport()
    {
        var primary = TestPayableBuilder.Mock(balance: 1000, name: "Primary");
        var support = TestPayableBuilder.Mock(balance: 500, name: "Support");
        var fixedStruct = new FixedStructure(300, primary, support);

        fixedStruct.PaySp(null, _testDate, 500, () => { });

        primary.TotalSpPaid.Should().Be(300);
        support.TotalSpPaid.Should().Be(200);
    }

    [Fact]
    public void PaySp_InsufficientForFixed_AllToFixed()
    {
        var primary = TestPayableBuilder.Mock(balance: 1000, name: "Primary");
        var support = TestPayableBuilder.Mock(balance: 500, name: "Support");
        var fixedStruct = new FixedStructure(300, primary, support);

        fixedStruct.PaySp(null, _testDate, 200, () => { });

        primary.TotalSpPaid.Should().Be(200);
        support.TotalSpPaid.Should().Be(0);
    }

    [Fact]
    public void PaySp_FixedExceedsBalance_CappedAtBalance()
    {
        var primary = TestPayableBuilder.Mock(balance: 100, name: "Primary");
        var support = TestPayableBuilder.Mock(balance: 500, name: "Support");
        var fixedStruct = new FixedStructure(300, primary, support);

        fixedStruct.PaySp(null, _testDate, 500, () => { });

        primary.TotalSpPaid.Should().Be(100);
        support.TotalSpPaid.Should().BeApproximately(400, 1);
    }

    [Fact]
    public void PayInterest_PrimaryFirst()
    {
        var primary = TestPayableBuilder.Mock(interestDue: 200, name: "Primary");
        var support = TestPayableBuilder.Mock(interestDue: 100, name: "Support");
        var fixedStruct = new FixedStructure(300, primary, support);

        var paid = fixedStruct.PayInterest(null, _testDate, 300, _rateProvider, null);

        paid.Should().Be(300);
        primary.TotalInterestPaid.Should().Be(200);
        support.TotalInterestPaid.Should().Be(100);
    }

    [Fact]
    public void CurrentBalance_SumsBoth()
    {
        var primary = TestPayableBuilder.Mock(balance: 1000);
        var support = TestPayableBuilder.Mock(balance: 500);
        var fixedStruct = new FixedStructure(300, primary, support);

        fixedStruct.CurrentBalance(_testDate).Should().Be(1500);
    }
}
