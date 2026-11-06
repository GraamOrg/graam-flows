using FluentAssertions;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Payables;

/// <summary>
///     A vertical interest takes its balance share of a short month, whatever the cascade carries
///     unpaid. The figures are Benchmark 2019-B15's shape: 3.33% vertical, $2.3M of monthly interest.
/// </summary>
public class VerticalInterestTests
{
    private readonly DateTime _testDate = TestConstants.DefaultFirstPayDate;
    private readonly ConstantTestRateProvider _rateProvider = new();

    [Fact]
    public void PayInterest_FundsCoverEverything_EachIsPaidItsDue()
    {
        var vertical = TestPayableBuilder.Mock(balance: 26_280_000, interestDue: 81_298.95, name: "VRR");
        var cascade = TestPayableBuilder.Mock(balance: 762_000_000, interestDue: 2_218_701.05, name: "CASCADE");
        vertical.IsVerticalInterest = true;
        var structure = TestPayableBuilder.Prorata(vertical, cascade);

        var paid = structure.PayInterest(null, _testDate, 2_300_000, _rateProvider, null);

        paid.Should().BeApproximately(2_300_000, 0.01);
        vertical.TotalInterestPaid.Should().BeApproximately(81_298.95, 0.01);
        cascade.TotalInterestPaid.Should().BeApproximately(2_218_701.05, 0.01);
    }

    [Fact]
    public void PayInterest_ShortMonth_TheVerticalTakesItsBalanceShare()
    {
        var vertical = TestPayableBuilder.Mock(balance: 3_333_333.33, interestDue: 10_000, name: "VRR");
        var cascade = TestPayableBuilder.Mock(balance: 96_666_666.67, interestDue: 290_000, name: "CASCADE");
        vertical.IsVerticalInterest = true;
        var structure = TestPayableBuilder.Prorata(vertical, cascade);

        var paid = structure.PayInterest(null, _testDate, 270_000, _rateProvider, null);

        paid.Should().BeApproximately(270_000, 0.01);
        vertical.TotalInterestPaid.Should().BeApproximately(9_000, 0.01);
        cascade.TotalInterestPaid.Should().BeApproximately(261_000, 0.01);
    }

    [Fact]
    public void PayInterest_TheCascadesUnpaidInterestDoesNotDilutePay()
    {
        // The cascade owes $290,000 and carries $290,000 unpaid. Split by due, the vertical would
        // take 1.7% of the month; its share is 3.33%.
        var vertical = TestPayableBuilder.Mock(balance: 3_333_333.33, interestDue: 10_000, name: "VRR");
        var cascade = TestPayableBuilder.Mock(balance: 96_666_666.67, interestDue: 580_000, name: "CASCADE");
        vertical.IsVerticalInterest = true;
        var structure = TestPayableBuilder.Prorata(vertical, cascade);

        var paid = structure.PayInterest(null, _testDate, 270_000, _rateProvider, null);

        paid.Should().BeApproximately(270_000, 0.01);
        vertical.TotalInterestPaid.Should().BeApproximately(9_000, 0.01);
        cascade.TotalInterestPaid.Should().BeApproximately(261_000, 0.01);
    }

    [Fact]
    public void PayInterest_WhatTheVerticalIsNotDueGoesToTheCascade()
    {
        var vertical = TestPayableBuilder.Mock(balance: 10_000_000, interestDue: 1_000, name: "VRR");
        var cascade = TestPayableBuilder.Mock(balance: 90_000_000, interestDue: 300_000, name: "CASCADE");
        vertical.IsVerticalInterest = true;
        var structure = TestPayableBuilder.Prorata(vertical, cascade);

        var paid = structure.PayInterest(null, _testDate, 100_000, _rateProvider, null);

        paid.Should().BeApproximately(100_000, 0.01);
        vertical.TotalInterestPaid.Should().BeApproximately(1_000, 0.01);
        cascade.TotalInterestPaid.Should().BeApproximately(99_000, 0.01);
    }

    [Fact]
    public void PayInterest_TwoVerticalClassesEachTakeTheirShare()
    {
        var rr = TestPayableBuilder.Mock(balance: 2_000_000, interestDue: 10_000, name: "RR");
        var rrInterest = TestPayableBuilder.Mock(balance: 3_000_000, interestDue: 15_000, name: "RRInterest");
        var cascade = TestPayableBuilder.Mock(balance: 95_000_000, interestDue: 475_000, name: "CASCADE");
        rr.IsVerticalInterest = rrInterest.IsVerticalInterest = true;
        var structure = TestPayableBuilder.Prorata(rr, rrInterest, cascade);

        structure.PayInterest(null, _testDate, 400_000, _rateProvider, null);

        rr.TotalInterestPaid.Should().BeApproximately(8_000, 0.01);
        rrInterest.TotalInterestPaid.Should().BeApproximately(12_000, 0.01);
        cascade.TotalInterestPaid.Should().BeApproximately(380_000, 0.01);
    }
}
