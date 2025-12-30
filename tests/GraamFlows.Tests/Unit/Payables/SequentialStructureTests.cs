using FluentAssertions;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall.Structures.PayableStructures;
using Xunit;

namespace GraamFlows.Tests.Unit.Payables;

/// <summary>
/// Tests for SequentialStructure PayInterest behavior.
/// </summary>
public class SequentialStructureTests
{
    private readonly DateTime _testDate = TestConstants.DefaultFirstPayDate;
    private readonly ConstantTestRateProvider _rateProvider = new();

    [Fact]
    public void PayInterest_SingleChild_PaysFullAmount()
    {
        // Arrange
        var child = TestPayableBuilder.Mock(interestDue: 1000);
        var seq = TestPayableBuilder.Sequential(child);

        // Act
        var paid = seq.PayInterest(null, _testDate, 5000, _rateProvider, null);

        // Assert
        paid.Should().Be(1000);
        child.TotalInterestPaid.Should().Be(1000);
    }

    [Fact]
    public void PayInterest_TwoChildren_FirstPaidFirst()
    {
        // Arrange
        var child1 = TestPayableBuilder.Mock(interestDue: 1000, name: "A");
        var child2 = TestPayableBuilder.Mock(interestDue: 500, name: "B");
        var seq = TestPayableBuilder.Sequential(child1, child2);

        // Act
        var paid = seq.PayInterest(null, _testDate, 2000, _rateProvider, null);

        // Assert
        paid.Should().Be(1500);
        child1.TotalInterestPaid.Should().Be(1000);
        child2.TotalInterestPaid.Should().Be(500);
    }

    [Fact]
    public void PayInterest_InsufficientFunds_FirstChildPartial_SecondGetsNothing()
    {
        // Arrange
        var child1 = TestPayableBuilder.Mock(interestDue: 1000, name: "A");
        var child2 = TestPayableBuilder.Mock(interestDue: 500, name: "B");
        var seq = TestPayableBuilder.Sequential(child1, child2);

        // Act - only 800 available
        var paid = seq.PayInterest(null, _testDate, 800, _rateProvider, null);

        // Assert
        paid.Should().Be(800);
        child1.TotalInterestPaid.Should().Be(800);
        child2.TotalInterestPaid.Should().Be(0);
    }

    [Fact]
    public void PayInterest_InsufficientFunds_FirstPaid_SecondPartial()
    {
        // Arrange
        var child1 = TestPayableBuilder.Mock(interestDue: 1000, name: "A");
        var child2 = TestPayableBuilder.Mock(interestDue: 500, name: "B");
        var seq = TestPayableBuilder.Sequential(child1, child2);

        // Act - 1200 available
        var paid = seq.PayInterest(null, _testDate, 1200, _rateProvider, null);

        // Assert
        paid.Should().Be(1200);
        child1.TotalInterestPaid.Should().Be(1000);
        child2.TotalInterestPaid.Should().Be(200);
    }

    [Fact]
    public void PayInterest_LockedOutChild_Skipped()
    {
        // Arrange
        var child1 = TestPayableBuilder.Mock(interestDue: 1000, isLockedOut: true, name: "A");
        var child2 = TestPayableBuilder.Mock(interestDue: 500, name: "B");
        var seq = TestPayableBuilder.Sequential(child1, child2);

        // Act
        var paid = seq.PayInterest(null, _testDate, 2000, _rateProvider, null);

        // Assert
        paid.Should().Be(500);
        child1.TotalInterestPaid.Should().Be(0);
        child2.TotalInterestPaid.Should().Be(500);
    }

    [Fact]
    public void PayInterest_AllLockedOut_ReturnZero()
    {
        // Arrange
        var child1 = TestPayableBuilder.Mock(interestDue: 1000, isLockedOut: true);
        var child2 = TestPayableBuilder.Mock(interestDue: 500, isLockedOut: true);
        var seq = TestPayableBuilder.Sequential(child1, child2);

        // Act
        var paid = seq.PayInterest(null, _testDate, 2000, _rateProvider, null);

        // Assert
        paid.Should().Be(0);
    }

    [Fact]
    public void PayInterest_ZeroAvailable_NothingPaid()
    {
        // Arrange
        var child = TestPayableBuilder.Mock(interestDue: 1000);
        var seq = TestPayableBuilder.Sequential(child);

        // Act
        var paid = seq.PayInterest(null, _testDate, 0, _rateProvider, null);

        // Assert
        paid.Should().Be(0);
        child.TotalInterestPaid.Should().Be(0);
    }

    [Fact]
    public void InterestDue_SumsAllChildren()
    {
        // Arrange
        var child1 = TestPayableBuilder.Mock(interestDue: 1000);
        var child2 = TestPayableBuilder.Mock(interestDue: 500);
        var child3 = TestPayableBuilder.Mock(interestDue: 250);
        var seq = TestPayableBuilder.Sequential(child1, child2, child3);

        // Act
        var due = seq.InterestDue(_testDate, _rateProvider, null);

        // Assert
        due.Should().Be(1750);
    }

    [Fact]
    public void PaySp_SequentialDistribution()
    {
        // Arrange
        var child1 = TestPayableBuilder.Mock(balance: 1000, name: "A");
        var child2 = TestPayableBuilder.Mock(balance: 500, name: "B");
        var seq = TestPayableBuilder.Sequential(child1, child2);

        // Act
        seq.PaySp(null, _testDate, 1200, () => { });

        // Assert
        child1.TotalSpPaid.Should().Be(1000);
        child2.TotalSpPaid.Should().Be(200);
        child1.Balance.Should().Be(0);
        child2.Balance.Should().Be(300);
    }

    [Fact]
    public void PayWritedown_DistributesSequentially()
    {
        // Arrange
        var child1 = TestPayableBuilder.Mock(balance: 1000, name: "A");
        var child2 = TestPayableBuilder.Mock(balance: 500, name: "B");
        var seq = TestPayableBuilder.Sequential(child1, child2);

        // Act
        seq.PayWritedown(null, _testDate, 700, () => { });

        // Assert
        child1.TotalWritedownPaid.Should().Be(700);
        child2.TotalWritedownPaid.Should().Be(0);
    }

    [Fact]
    public void CurrentBalance_SumsAllChildren()
    {
        // Arrange
        var child1 = TestPayableBuilder.Mock(balance: 1000);
        var child2 = TestPayableBuilder.Mock(balance: 500);
        var seq = TestPayableBuilder.Sequential(child1, child2);

        // Act
        var balance = seq.CurrentBalance(_testDate);

        // Assert
        balance.Should().Be(1500);
    }

    [Fact]
    public void Leafs_ReturnsAllChildLeafs()
    {
        // Arrange
        var child1 = TestPayableBuilder.Mock(name: "A");
        var child2 = TestPayableBuilder.Mock(name: "B");
        var seq = TestPayableBuilder.Sequential(child1, child2);

        // Act
        var leafs = seq.Leafs();

        // Assert
        leafs.Should().HaveCount(2);
        leafs.Should().Contain(child1);
        leafs.Should().Contain(child2);
    }

    [Fact]
    public void IsLeaf_ReturnsFalse()
    {
        // Arrange
        var seq = TestPayableBuilder.Sequential(TestPayableBuilder.Mock());

        // Assert
        seq.IsLeaf.Should().BeFalse();
    }
}
