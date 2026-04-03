using FluentAssertions;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall.Structures.PayableStructures;
using Xunit;

namespace GraamFlows.Tests.Unit.Payables;

/// <summary>
/// Tests for ProformaStructure.
/// Distributes payments according to fixed percentage shares.
/// </summary>
public class ProformaStructureTests
{
    private readonly DateTime _testDate = TestConstants.DefaultFirstPayDate;
    private readonly ConstantTestRateProvider _rateProvider = new();

    [Fact]
    public void PaySp_TwoPayables_SplitByFormaPercent()
    {
        var p1 = TestPayableBuilder.Mock(balance: 1000, name: "A");
        var p2 = TestPayableBuilder.Mock(balance: 1000, name: "B");
        var proforma = new ProformaStructure(p1, 0.6, p2, 0.4);

        proforma.PaySp(null, _testDate, 500, () => { });

        p1.TotalSpPaid.Should().BeApproximately(300, 1);
        p2.TotalSpPaid.Should().BeApproximately(200, 1);
    }

    [Fact]
    public void PaySp_ThreePayables_SplitByFormaPercent()
    {
        var p1 = TestPayableBuilder.Mock(balance: 1000, name: "A");
        var p2 = TestPayableBuilder.Mock(balance: 1000, name: "B");
        var p3 = TestPayableBuilder.Mock(balance: 1000, name: "C");
        var proforma = new ProformaStructure(p1, 0.5, p2, 0.3, p3, 0.2);

        proforma.PaySp(null, _testDate, 1000, () => { });

        p1.TotalSpPaid.Should().BeApproximately(500, 1);
        p2.TotalSpPaid.Should().BeApproximately(300, 1);
        p3.TotalSpPaid.Should().BeApproximately(200, 1);
    }

    [Fact]
    public void PaySp_SinglePayable_GetsAll()
    {
        var p1 = TestPayableBuilder.Mock(balance: 1000, name: "A");
        var proforma = new ProformaStructure(p1);

        proforma.PaySp(null, _testDate, 500, () => { });

        p1.TotalSpPaid.Should().Be(500);
    }

    [Fact]
    public void PaySp_OnePayableExhausted_RedistributesToOther()
    {
        var p1 = TestPayableBuilder.Mock(balance: 100, name: "A");
        var p2 = TestPayableBuilder.Mock(balance: 1000, name: "B");
        var proforma = new ProformaStructure(p1, 0.5, p2, 0.5);

        proforma.PaySp(null, _testDate, 500, () => { });

        p1.TotalSpPaid.Should().Be(100);
        p2.TotalSpPaid.Should().BeApproximately(400, 1);
    }

    [Fact]
    public void Constructor_InvalidPercentages_Throws()
    {
        var p1 = TestPayableBuilder.Mock(name: "A");
        var p2 = TestPayableBuilder.Mock(name: "B");

        var act = () => new ProformaStructure(p1, 0.5, p2, 0.3);

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void PayInterest_SplitByFormaShare()
    {
        var p1 = TestPayableBuilder.Mock(interestDue: 600, name: "A");
        var p2 = TestPayableBuilder.Mock(interestDue: 400, name: "B");
        var proforma = new ProformaStructure(p1, 0.5, p2, 0.5);

        var paid = proforma.PayInterest(null, _testDate, 1000, _rateProvider, null);

        // Proforma splits by forma percentage, capped at interest due
        // p1 gets min(500, 600) = 500, p2 gets min(500, 400) = 400, total = 900
        paid.Should().Be(900);
        p1.TotalInterestPaid.Should().Be(500);
        p2.TotalInterestPaid.Should().Be(400);
    }
}
