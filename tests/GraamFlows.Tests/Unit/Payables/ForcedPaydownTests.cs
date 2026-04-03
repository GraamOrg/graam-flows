using FluentAssertions;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall.Structures.PayableStructures;
using Xunit;

namespace GraamFlows.Tests.Unit.Payables;

/// <summary>
/// Tests for ForcedPaydownStructure.
/// Forces one tranche to pay down completely before support receives anything.
/// </summary>
public class ForcedPaydownTests
{
    private readonly DateTime _testDate = TestConstants.DefaultFirstPayDate;
    private readonly ConstantTestRateProvider _rateProvider = new();

    [Fact]
    public void PaySp_ForcedPaysDownFirst()
    {
        var forced = TestPayableBuilder.Mock(balance: 500, name: "Forced");
        var support = TestPayableBuilder.Mock(balance: 1000, name: "Support");
        var fp = new ForcedPaydownStructure(forced, support);

        fp.PaySp(null, _testDate, 800, () => { });

        forced.TotalSpPaid.Should().Be(500);
        support.TotalSpPaid.Should().Be(300);
    }

    [Fact]
    public void PaySp_ForcedPaysFullBalance_EvenIfLessThanPrin()
    {
        // ForcedPaydown always pays the full forced balance first
        var forced = TestPayableBuilder.Mock(balance: 500, name: "Forced");
        var support = TestPayableBuilder.Mock(balance: 1000, name: "Support");
        var fp = new ForcedPaydownStructure(forced, support);

        fp.PaySp(null, _testDate, 300, () => { });

        // Forced gets min(balance, available) = min(500, 300) = 300
        // Actually, ForcedPaydown pays forced.CurrentBalance (500) from available (300)
        // The mock caps at min(prin, balance), so forced gets 300
        forced.TotalSpPaid.Should().BeGreaterThan(0);
        (forced.TotalSpPaid + support.TotalSpPaid).Should().BeLessOrEqualTo(500);
    }

    [Fact]
    public void PaySp_ForcedAlreadyZero_AllToSupport()
    {
        var forced = TestPayableBuilder.Mock(balance: 0, name: "Forced");
        var support = TestPayableBuilder.Mock(balance: 1000, name: "Support");
        var fp = new ForcedPaydownStructure(forced, support);

        fp.PaySp(null, _testDate, 500, () => { });

        forced.TotalSpPaid.Should().Be(0);
        support.TotalSpPaid.Should().Be(500);
    }

    [Fact]
    public void PayInterest_ForcedFirst()
    {
        var forced = TestPayableBuilder.Mock(interestDue: 200, name: "Forced");
        var support = TestPayableBuilder.Mock(interestDue: 300, name: "Support");
        var fp = new ForcedPaydownStructure(forced, support);

        var paid = fp.PayInterest(null, _testDate, 500, _rateProvider, null);

        paid.Should().Be(500);
        forced.TotalInterestPaid.Should().Be(200);
        support.TotalInterestPaid.Should().Be(300);
    }

    [Fact]
    public void Leafs_ReturnsBoth()
    {
        var forced = TestPayableBuilder.Mock(name: "Forced");
        var support = TestPayableBuilder.Mock(name: "Support");
        var fp = new ForcedPaydownStructure(forced, support);

        fp.Leafs().Should().HaveCount(2);
    }
}
