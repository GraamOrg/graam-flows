using FluentAssertions;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall.Structures.PayableStructures;
using Xunit;

namespace GraamFlows.Tests.Unit.Payables;

/// <summary>
/// Tests for ProrataStructure PayInterest behavior.
/// </summary>
public class ProrataStructureTests
{
    private readonly DateTime _testDate = TestConstants.DefaultFirstPayDate;
    private readonly ConstantTestRateProvider _rateProvider = new();

    [Fact]
    public void PayInterest_TwoChildren_ProportionalByInterestDue()
    {
        // Arrange - A owes 1000, B owes 500 -> 2:1 ratio
        var childA = TestPayableBuilder.Mock(interestDue: 1000, name: "A");
        var childB = TestPayableBuilder.Mock(interestDue: 500, name: "B");
        var prorata = TestPayableBuilder.Prorata(childA, childB);

        // Act - 1500 available, exactly enough
        var paid = prorata.PayInterest(null, _testDate, 1500, _rateProvider, null);

        // Assert
        paid.Should().Be(1500);
        childA.TotalInterestPaid.Should().Be(1000);
        childB.TotalInterestPaid.Should().Be(500);
    }

    [Fact]
    public void PayInterest_InsufficientFunds_ProportionalSplit()
    {
        // Arrange - A owes 1000, B owes 500 -> 2:1 ratio
        var childA = TestPayableBuilder.Mock(interestDue: 1000, name: "A");
        var childB = TestPayableBuilder.Mock(interestDue: 500, name: "B");
        var prorata = TestPayableBuilder.Prorata(childA, childB);

        // Act - only 750 available (50% of total)
        var paid = prorata.PayInterest(null, _testDate, 750, _rateProvider, null);

        // Assert - proportional split
        paid.Should().Be(750);
        // A gets 2/3 * 750 = 500 (but capped at interest due)
        // B gets 1/3 * 750 = 250
        childA.TotalInterestPaid.Should().BeApproximately(500, 1);
        childB.TotalInterestPaid.Should().BeApproximately(250, 1);
    }

    [Fact]
    public void PayInterest_EqualInterestDue_EqualSplit()
    {
        // Arrange - equal interest due
        var childA = TestPayableBuilder.Mock(interestDue: 500, name: "A");
        var childB = TestPayableBuilder.Mock(interestDue: 500, name: "B");
        var prorata = TestPayableBuilder.Prorata(childA, childB);

        // Act
        var paid = prorata.PayInterest(null, _testDate, 1000, _rateProvider, null);

        // Assert
        paid.Should().Be(1000);
        childA.TotalInterestPaid.Should().Be(500);
        childB.TotalInterestPaid.Should().Be(500);
    }

    [Fact]
    public void PayInterest_OneChildZeroInterestDue_AllGoesToOther()
    {
        // Arrange
        var childA = TestPayableBuilder.Mock(interestDue: 0, name: "A");
        var childB = TestPayableBuilder.Mock(interestDue: 500, name: "B");
        var prorata = TestPayableBuilder.Prorata(childA, childB);

        // Act
        var paid = prorata.PayInterest(null, _testDate, 1000, _rateProvider, null);

        // Assert
        paid.Should().Be(500);
        childA.TotalInterestPaid.Should().Be(0);
        childB.TotalInterestPaid.Should().Be(500);
    }

    [Fact]
    public void PayInterest_AllZeroInterestDue_NothingPaid()
    {
        // Arrange
        var childA = TestPayableBuilder.Mock(interestDue: 0, name: "A");
        var childB = TestPayableBuilder.Mock(interestDue: 0, name: "B");
        var prorata = TestPayableBuilder.Prorata(childA, childB);

        // Act
        var paid = prorata.PayInterest(null, _testDate, 1000, _rateProvider, null);

        // Assert
        paid.Should().Be(0);
    }

    [Fact]
    public void PayInterest_LockedOutChild_ExcludedFromProrata()
    {
        // Arrange
        var childA = TestPayableBuilder.Mock(interestDue: 500, isLockedOut: true, name: "A");
        var childB = TestPayableBuilder.Mock(interestDue: 500, name: "B");
        var prorata = TestPayableBuilder.Prorata(childA, childB);

        // Act
        var paid = prorata.PayInterest(null, _testDate, 1000, _rateProvider, null);

        // Assert - locked out A is skipped
        paid.Should().Be(500);
        childA.TotalInterestPaid.Should().Be(0);
        childB.TotalInterestPaid.Should().Be(500);
    }

    [Fact]
    public void InterestDue_SumsAllChildren()
    {
        // Arrange
        var childA = TestPayableBuilder.Mock(interestDue: 1000);
        var childB = TestPayableBuilder.Mock(interestDue: 500);
        var prorata = TestPayableBuilder.Prorata(childA, childB);

        // Act
        var due = prorata.InterestDue(_testDate, _rateProvider, null);

        // Assert
        due.Should().Be(1500);
    }

    [Fact]
    public void PaySp_DistributesToBothChildren()
    {
        // Arrange - A has 1000, B has 500
        var childA = TestPayableBuilder.Mock(balance: 1000, name: "A");
        var childB = TestPayableBuilder.Mock(balance: 500, name: "B");
        var prorata = TestPayableBuilder.Prorata(childA, childB);

        // Act - pay 750
        prorata.PaySp(null, _testDate, 750, () => { });

        // Assert - both children receive principal (exact split depends on implementation details)
        var totalPaid = childA.TotalSpPaid + childB.TotalSpPaid;
        totalPaid.Should().BeApproximately(750, 1);
        childA.TotalSpPaid.Should().BeGreaterThan(0);
        childB.TotalSpPaid.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CurrentBalance_SumsAllChildren()
    {
        // Arrange
        var childA = TestPayableBuilder.Mock(balance: 1000);
        var childB = TestPayableBuilder.Mock(balance: 500);
        var prorata = TestPayableBuilder.Prorata(childA, childB);

        // Act
        var balance = prorata.CurrentBalance(_testDate);

        // Assert
        balance.Should().Be(1500);
    }

    [Fact]
    public void ThreeChildren_ProportionalDistribution()
    {
        // Arrange - A:B:C = 3:2:1
        var childA = TestPayableBuilder.Mock(interestDue: 300, name: "A");
        var childB = TestPayableBuilder.Mock(interestDue: 200, name: "B");
        var childC = TestPayableBuilder.Mock(interestDue: 100, name: "C");
        var prorata = TestPayableBuilder.Prorata(childA, childB, childC);

        // Act - 600 available
        var paid = prorata.PayInterest(null, _testDate, 600, _rateProvider, null);

        // Assert
        paid.Should().Be(600);
        childA.TotalInterestPaid.Should().Be(300);
        childB.TotalInterestPaid.Should().Be(200);
        childC.TotalInterestPaid.Should().Be(100);
    }

    [Fact]
    public void Leafs_ReturnsAllChildLeafs()
    {
        // Arrange
        var childA = TestPayableBuilder.Mock(name: "A");
        var childB = TestPayableBuilder.Mock(name: "B");
        var prorata = TestPayableBuilder.Prorata(childA, childB);

        // Act
        var leafs = prorata.Leafs();

        // Assert
        leafs.Should().HaveCount(2);
        leafs.Should().Contain(childA);
        leafs.Should().Contain(childB);
    }
}
