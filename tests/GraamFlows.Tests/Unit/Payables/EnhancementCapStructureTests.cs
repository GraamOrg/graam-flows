using FluentAssertions;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall.Structures.PayableStructures;
using Xunit;

namespace GraamFlows.Tests.Unit.Payables;

/// <summary>
/// Tests for EnhancementCapStructure (CSCAP).
/// When credit support would exceed the cap after paying seniors,
/// excess principal is redirected to subordinates.
/// </summary>
public class EnhancementCapStructureTests
{
    private readonly DateTime _testDate = TestConstants.DefaultFirstPayDate;
    private readonly ConstantTestRateProvider _rateProvider = new();

    [Fact]
    public void PaySp_BelowCap_AllToSeniors()
    {
        // Senior=800, Sub=200, total=1000. CS = 200/1000 = 20%.
        // Cap=50%. After paying 100 to seniors: CS = 200/900 = 22%. Still below cap.
        var seniors = TestPayableBuilder.Mock(balance: 800, name: "Senior");
        var subs = TestPayableBuilder.Mock(balance: 200, name: "Sub");
        var cscap = new EnhancementCapStructure(0.50, seniors, subs);

        cscap.PaySp(null, _testDate, 100, () => { });

        seniors.TotalSpPaid.Should().Be(100);
        subs.TotalSpPaid.Should().Be(0);
    }

    [Fact]
    public void PaySp_ExceedsCap_ExcessToSubs()
    {
        // Senior=900, Sub=100, total=1000. CS = 100/1000 = 10%.
        // Cap=5.5%. After paying 500 to seniors: senBal=400, CS = 100/500 = 20% > 5.5%.
        // Excess enhancement redirected to subs.
        var seniors = TestPayableBuilder.Mock(balance: 900, name: "Senior");
        var subs = TestPayableBuilder.Mock(balance: 100, name: "Sub");
        var cscap = new EnhancementCapStructure(0.055, seniors, subs);

        cscap.PaySp(null, _testDate, 500, () => { });

        // Subs should receive some principal due to cap enforcement
        subs.TotalSpPaid.Should().BeGreaterThan(0, "Subs should receive excess when CS exceeds cap");
        (seniors.TotalSpPaid + subs.TotalSpPaid).Should().BeApproximately(500, 1);
    }

    [Fact]
    public void PayInterest_SeniorsFirst_ThenSubs()
    {
        var seniors = TestPayableBuilder.Mock(interestDue: 500, name: "Senior");
        var subs = TestPayableBuilder.Mock(interestDue: 200, name: "Sub");
        var cscap = new EnhancementCapStructure(0.10, seniors, subs);

        var paid = cscap.PayInterest(null, _testDate, 700, _rateProvider, null);

        paid.Should().Be(700);
        seniors.TotalInterestPaid.Should().Be(500);
        subs.TotalInterestPaid.Should().Be(200);
    }

    [Fact]
    public void PayWritedown_DistributedToChildren()
    {
        var seniors = TestPayableBuilder.Mock(balance: 1000, name: "Senior");
        var subs = TestPayableBuilder.Mock(balance: 500, name: "Sub");
        var cscap = new EnhancementCapStructure(0.10, seniors, subs);

        cscap.PayWritedown(null, _testDate, 300, () => { });

        (seniors.TotalWritedownPaid + subs.TotalWritedownPaid).Should().BeApproximately(300, 1);
    }

    [Fact]
    public void InterestDue_SumsBoth()
    {
        var seniors = TestPayableBuilder.Mock(interestDue: 400);
        var subs = TestPayableBuilder.Mock(interestDue: 100);
        var cscap = new EnhancementCapStructure(0.10, seniors, subs);

        cscap.InterestDue(_testDate, _rateProvider, null).Should().Be(500);
    }

    [Fact]
    public void CurrentBalance_SumsBoth()
    {
        var seniors = TestPayableBuilder.Mock(balance: 800);
        var subs = TestPayableBuilder.Mock(balance: 200);
        var cscap = new EnhancementCapStructure(0.10, seniors, subs);

        cscap.CurrentBalance(_testDate).Should().Be(1000);
    }
}
