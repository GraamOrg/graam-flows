using FluentAssertions;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall.Structures.PayableStructures;
using Xunit;

namespace GraamFlows.Tests.Unit.Payables;

/// <summary>
/// Tests for nested/composed payable structures, which is the core pattern
/// used by ComposableStructure (e.g., SEQ(PRORATA('A','B'), SINGLE('C'))).
/// </summary>
public class NestedPayableTests
{
    private readonly DateTime _testDate = TestConstants.DefaultFirstPayDate;
    private readonly ConstantTestRateProvider _rateProvider = new();

    [Fact]
    public void SeqOfProrata_PaysFirstGroupProrataThenSecondSequentially()
    {
        // SEQ(PRORATA(A, B), C) - A & B share first, then C gets remainder
        var a = TestPayableBuilder.Mock(balance: 500, name: "A");
        var b = TestPayableBuilder.Mock(balance: 500, name: "B");
        var c = TestPayableBuilder.Mock(balance: 1000, name: "C");
        var prorata = TestPayableBuilder.Prorata(a, b);
        var seq = TestPayableBuilder.Sequential(prorata, c);

        seq.PaySp(null, _testDate, 1500, () => { });

        // Prorata group (A+B=1000) absorbs first 1000
        a.TotalSpPaid.Should().BeApproximately(500, 1);
        b.TotalSpPaid.Should().BeApproximately(500, 1);
        c.TotalSpPaid.Should().BeApproximately(500, 1);
    }

    [Fact]
    public void SeqOfProrata_InsufficientFunds_OnlyFirstGroupPaid()
    {
        var a = TestPayableBuilder.Mock(balance: 500, name: "A");
        var b = TestPayableBuilder.Mock(balance: 500, name: "B");
        var c = TestPayableBuilder.Mock(balance: 1000, name: "C");
        var prorata = TestPayableBuilder.Prorata(a, b);
        var seq = TestPayableBuilder.Sequential(prorata, c);

        seq.PaySp(null, _testDate, 600, () => { });

        // Prorata distributes by BeginBalance ratio to the two children
        (a.TotalSpPaid + b.TotalSpPaid).Should().BeApproximately(600, 1);
        c.TotalSpPaid.Should().Be(0);
    }

    [Fact]
    public void ProrataOfSeq_BothGroupsReceivePrincipal()
    {
        // PRORATA(SEQ(A, B), SEQ(C, D)) - both groups get some principal
        var a = TestPayableBuilder.Mock(balance: 600, name: "A");
        var b = TestPayableBuilder.Mock(balance: 400, name: "B");
        var c = TestPayableBuilder.Mock(balance: 500, name: "C");
        var d = TestPayableBuilder.Mock(balance: 500, name: "D");
        var seq1 = TestPayableBuilder.Sequential(a, b);
        var seq2 = TestPayableBuilder.Sequential(c, d);
        var prorata = TestPayableBuilder.Prorata(seq1, seq2);

        prorata.PaySp(null, _testDate, 500, () => { });

        // Both sequential groups should receive some principal
        (a.TotalSpPaid + b.TotalSpPaid).Should().BeGreaterThan(0, "seq1 should receive principal");
        (c.TotalSpPaid + d.TotalSpPaid).Should().BeGreaterThan(0, "seq2 should receive principal");

        // Within each seq, senior is paid first
        a.TotalSpPaid.Should().BeGreaterThan(0, "A (senior in seq1) should be paid");
        c.TotalSpPaid.Should().BeGreaterThan(0, "C (senior in seq2) should be paid");
    }

    [Fact]
    public void DeepNesting_SeqOfSeqOfSingle()
    {
        // SEQ(SEQ(A, B), SEQ(C, D))
        var a = TestPayableBuilder.Mock(balance: 100, name: "A");
        var b = TestPayableBuilder.Mock(balance: 200, name: "B");
        var c = TestPayableBuilder.Mock(balance: 300, name: "C");
        var d = TestPayableBuilder.Mock(balance: 400, name: "D");
        var inner1 = TestPayableBuilder.Sequential(a, b);
        var inner2 = TestPayableBuilder.Sequential(c, d);
        var outer = TestPayableBuilder.Sequential(inner1, inner2);

        outer.PaySp(null, _testDate, 500, () => { });

        // Inner1 (300 total) absorbs first, then inner2 gets 200
        a.TotalSpPaid.Should().Be(100);
        b.TotalSpPaid.Should().Be(200);
        c.TotalSpPaid.Should().Be(200);
        d.TotalSpPaid.Should().Be(0);
    }

    [Fact]
    public void Leafs_NestedStructure_ReturnsAllTerminals()
    {
        var a = TestPayableBuilder.Mock(name: "A");
        var b = TestPayableBuilder.Mock(name: "B");
        var c = TestPayableBuilder.Mock(name: "C");
        var prorata = TestPayableBuilder.Prorata(a, b);
        var seq = TestPayableBuilder.Sequential(prorata, c);

        seq.Leafs().Should().HaveCount(3);
    }

    [Fact]
    public void InterestDue_NestedStructure_SumsAllLeaves()
    {
        var a = TestPayableBuilder.Mock(interestDue: 100, name: "A");
        var b = TestPayableBuilder.Mock(interestDue: 200, name: "B");
        var c = TestPayableBuilder.Mock(interestDue: 300, name: "C");
        var prorata = TestPayableBuilder.Prorata(a, b);
        var seq = TestPayableBuilder.Sequential(prorata, c);

        seq.InterestDue(_testDate, _rateProvider, null).Should().Be(600);
    }

    [Fact]
    public void PayInterest_Nested_SequentialThenProrata()
    {
        // SEQ(PRORATA(A, B), C) - interest sequential: prorata group first, then C
        var a = TestPayableBuilder.Mock(interestDue: 200, name: "A");
        var b = TestPayableBuilder.Mock(interestDue: 300, name: "B");
        var c = TestPayableBuilder.Mock(interestDue: 400, name: "C");
        var prorata = TestPayableBuilder.Prorata(a, b);
        var seq = TestPayableBuilder.Sequential(prorata, c);

        var paid = seq.PayInterest(null, _testDate, 600, _rateProvider, null);

        // Prorata group gets up to 500, then C gets remaining 100
        paid.Should().Be(600);
        a.TotalInterestPaid.Should().Be(200);
        b.TotalInterestPaid.Should().Be(300);
        c.TotalInterestPaid.Should().Be(100);
    }

    [Fact]
    public void PayWritedown_NestedSequential_WritesDownInOrder()
    {
        // SEQ(A, B) - writedown hits A first
        var a = TestPayableBuilder.Mock(balance: 500, name: "A");
        var b = TestPayableBuilder.Mock(balance: 500, name: "B");
        var seq = TestPayableBuilder.Sequential(a, b);

        seq.PayWritedown(null, _testDate, 700, () => { });

        a.TotalWritedownPaid.Should().Be(500);
        b.TotalWritedownPaid.Should().Be(200);
    }
}
