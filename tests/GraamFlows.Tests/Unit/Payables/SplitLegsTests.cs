using FluentAssertions;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall;
using GraamFlows.Waterfall.Structures.PayableStructures;
using Xunit;

namespace GraamFlows.Tests.Unit.Payables;

/// <summary>
/// Tests for SplitStructure's legs (the DSL's PROFORMA is a split written in fractions).
/// Distributes payments according to fixed percentage shares.
/// </summary>
public class SplitLegsTests
{
    private readonly DateTime _testDate = TestConstants.DefaultFirstPayDate;
    private readonly ConstantTestRateProvider _rateProvider = new();

    [Fact]
    public void PaySp_TwoPayables_SplitByFormaPercent()
    {
        var p1 = TestPayableBuilder.Mock(balance: 1000, name: "A");
        var p2 = TestPayableBuilder.Mock(balance: 1000, name: "B");
        var proforma = Split(new[] { 60.0, 40.0 }, p1, p2);

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
        var proforma = Split(new[] { 50.0, 30.0, 20.0 }, p1, p2, p3);

        proforma.PaySp(null, _testDate, 1000, () => { });

        p1.TotalSpPaid.Should().BeApproximately(500, 1);
        p2.TotalSpPaid.Should().BeApproximately(300, 1);
        p3.TotalSpPaid.Should().BeApproximately(200, 1);
    }

    [Fact]
    public void PaySp_SinglePayable_GetsAll()
    {
        var p1 = TestPayableBuilder.Mock(balance: 1000, name: "A");
        var proforma = Split(new[] { 100.0 }, p1);

        proforma.PaySp(null, _testDate, 500, () => { });

        p1.TotalSpPaid.Should().Be(500);
    }

    [Fact]
    public void PaySp_OnePayableExhausted_RedistributesToOther()
    {
        var p1 = TestPayableBuilder.Mock(balance: 100, name: "A");
        var p2 = TestPayableBuilder.Mock(balance: 1000, name: "B");
        var proforma = Split(new[] { 50.0, 50.0 }, p1, p2);

        proforma.PaySp(null, _testDate, 500, () => { });

        p1.TotalSpPaid.Should().Be(100);
        p2.TotalSpPaid.Should().BeApproximately(400, 1);
    }

    [Fact]
    public void Constructor_InvalidPercentages_Throws()
    {
        var p1 = TestPayableBuilder.Mock(name: "A");
        var p2 = TestPayableBuilder.Mock(name: "B");

        var act = () => Split(new[] { 50.0, 30.0 }, p1, p2);

        act.Should().Throw<ArgumentException>().WithMessage("*not 100%*");
    }

    [Fact]
    public void PayInterest_SplitByFormaShare()
    {
        var p1 = TestPayableBuilder.Mock(interestDue: 600, name: "A");
        var p2 = TestPayableBuilder.Mock(interestDue: 400, name: "B");
        var proforma = Split(new[] { 50.0, 50.0 }, p1, p2);

        var paid = proforma.PayInterest(null, _testDate, 1000, _rateProvider, null);

        // Each leg its share, capped at the interest it is due: p1 500 of 600, p2 400; the 100 p2
        // does not need goes to p1, so nothing it is owed is left unpaid.
        paid.Should().Be(1000);
        p1.TotalInterestPaid.Should().Be(600);
        p2.TotalInterestPaid.Should().Be(400);
    }

    [Fact]
    public void PaySp_MoreLegsThanThree_EachItsShare()
    {
        var legs = Enumerable.Range(0, 5).Select(i => TestPayableBuilder.Mock(balance: 1000, name: $"L{i}")).ToArray();
        var split = Split(new[] { 10.0, 20.0, 30.0, 25.0, 15.0 }, legs);

        split.PaySp(null, _testDate, 1000, () => { });

        legs.Select(l => l.TotalSpPaid).Should().Equal(100, 200, 300, 250, 150);
    }

    private static SplitStructure Split(double[] shares, params IPayable[] legs) => new(shares, legs);
}
