using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     <c>assumptions.delinquencyType</c>: "PctCurrBal" (the default) or "PctOrigBal". The basis was
///     carried before but never read, so every run that carried delinquency ran on the current
///     balance; that is still what runs when the field is absent, for every prepayment type. The
///     basis moves only DelinqBalance (what the delinquency triggers read) and the P&amp;I disclosure
///     lines; delinquency carries no cash.
/// </summary>
public class DelinquencyBasisTests
{
    private static readonly DateTime ProjectionDate = new(2024, 6, 1);
    private const double Original = 100_000.0;

    private static AssetDto Loan(string id) => new()
    {
        AssetName = id,
        AssetId = id,
        InterestRateType = "FRM",
        OriginalDate = new DateTime(2022, 6, 1),
        OriginalBalance = Original,
        CurrentBalance = 80_000.0,
        OriginalInterestRate = 7.0,
        CurrentInterestRate = 7.0,
        OriginalAmortizationTerm = 120,
        GroupNum = "1",
    };

    private static CalcCollateralRequest Request(string? delinquencyType, string prepaymentType = "CPR",
        double cpr = 10.0) => new()
    {
        Assets = new List<AssetDto> { Loan("A"), Loan("B") },
        ProjectionDate = ProjectionDate,
        Assumptions = new AssumptionsDto
        {
            Cpr = cpr,
            Cdr = 1.0,
            Severity = 40.0,
            Delinquency = 5.0,
            Advancing = 0.0,
            PrepaymentType = prepaymentType,
            DelinquencyType = delinquencyType,
        },
    };

    private static ActionResult Post(CalcCollateralRequest request) =>
        new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(request).Result!;

    private static CalcCollateralResponse Ok(CalcCollateralRequest request) =>
        Post(request).Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<CalcCollateralResponse>().Subject;

    [Theory]
    [InlineData("CPR")]
    [InlineData("ABS")]
    public void With_No_Basis_Delinquency_Is_A_Share_Of_The_Current_Balance_For_Every_Prepay_Type(string prepay)
    {
        var cashflows = Ok(Request(null, prepay, prepay == "ABS" ? 1.0 : 10.0)).Cashflows;

        cashflows.Should().OnlyContain(cf => Math.Abs(cf.DelinqBalance - 0.05 * cf.Balance) < 1e-6);
    }

    [Fact]
    public void An_Absent_Basis_And_PctCurrBal_Run_Identically()
    {
        var absent = Ok(Request(null)).Cashflows;
        var current = Ok(Request("PctCurrBal")).Cashflows;

        absent.Select(cf => (cf.DelinqBalance, cf.UnAdvancedPrincipal, cf.UnAdvancedInterest, cf.Balance))
            .Should().Equal(current.Select(cf => (cf.DelinqBalance, cf.UnAdvancedPrincipal, cf.UnAdvancedInterest, cf.Balance)));
    }

    [Fact]
    public void On_The_Original_Balance_Delinquency_Is_That_Share_Of_Original_Until_The_Balance_Caps_It()
    {
        var cashflows = Ok(Request("PctOrigBal", cpr: 30.0)).Cashflows;
        var dqOnOriginal = 0.05 * 2 * Original;

        cashflows.Should().OnlyContain(cf => Math.Abs(cf.DelinqBalance - Math.Min(dqOnOriginal, cf.Balance)) < 1e-6);
        cashflows.Should().Contain(cf => cf.Balance < dqOnOriginal && cf.Balance > 0,
            "the run reaches the periods where the cap binds");
    }

    [Fact]
    public void The_Basis_Moves_No_Cash()
    {
        var current = Ok(Request("PctCurrBal")).Cashflows;
        var original = Ok(Request("PctOrigBal")).Cashflows;

        original.Select(cf => (cf.Balance, cf.ScheduledPrincipal, cf.UnscheduledPrincipal, cf.Interest,
                cf.DefaultedPrincipal, cf.RecoveryPrincipal, cf.CollateralLoss))
            .Should().Equal(current.Select(cf => (cf.Balance, cf.ScheduledPrincipal, cf.UnscheduledPrincipal,
                cf.Interest, cf.DefaultedPrincipal, cf.RecoveryPrincipal, cf.CollateralLoss)));
        original.Sum(cf => cf.DelinqBalance).Should().BeGreaterThan(current.Sum(cf => cf.DelinqBalance));
    }

    [Fact]
    public void The_Unadvanced_Principal_Reads_The_Same_Share_As_DelinqBalance()
    {
        // Advancing 0: the whole delinquent slice of scheduled principal is unadvanced, so its share
        // of scheduled principal is the delinquent share of the balance.
        var cashflows = Ok(Request("PctOrigBal")).Cashflows.Where(cf => cf.Balance > 0 && cf.ScheduledPrincipal > 0);

        cashflows.Should().OnlyContain(cf =>
            Math.Abs(cf.UnAdvancedPrincipal / cf.ScheduledPrincipal - cf.DelinqBalance / cf.Balance) < 1e-9);
    }

    [Fact]
    public void A_Delinquency_Trigger_Reading_DelinqBalance_Flips_At_A_Different_Period()
    {
        // The delinquency triggers compare DelinqBalance with the pool or subordinate balance. A 10%
        // test never fails on 5% of the current balance; on 5% of the original it fails once the pool
        // has paid down below half its original balance.
        static int? FirstBreach(List<PeriodCashflowDto> cfs) =>
            cfs.FindIndex(cf => cf.Balance > 0 && cf.DelinqBalance / cf.Balance > 0.10) is var i and >= 0 ? i : null;

        FirstBreach(Ok(Request("PctCurrBal")).Cashflows).Should().BeNull();
        FirstBreach(Ok(Request("PctOrigBal")).Cashflows).Should().NotBeNull();
    }

    [Theory]
    [InlineData(null, "PctCurrBal")]
    [InlineData("PctCurrBal", "PctCurrBal")]
    [InlineData(" pctorigbal ", "PctOrigBal")]
    public void The_Response_Says_Which_Basis_Ran(string? sent, string ran)
    {
        Ok(Request(sent)).DelinquencyResolution!.DelinquencyType.Should().Be(ran);
    }

    [Fact]
    public void An_Unknown_Basis_Is_Refused()
    {
        Post(Request("PctSchedBal")).Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()
            .Should().Contain("assumptions.delinquencyType").And.Contain("PctOrigBal");
    }

    [Fact]
    public void PctOrigBal_With_PolyPaths_Strings_Is_Refused()
    {
        var request = Request("PctOrigBal");
        request.Assumptions.DelinquencyVectorStr = "5.0";

        Post(request).Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()
            .Should().Contain("PctOrigBal").And.Contain("cprVector");
    }

    [Fact]
    public void Per_Period_Arrays_Honour_The_Basis()
    {
        var request = Request("PctOrigBal");
        request.Assumptions.DelinquencyVector = Enumerable.Repeat(5.0, 120).ToArray();

        var vector = Ok(request).Cashflows;
        var scalar = Ok(Request("PctOrigBal")).Cashflows;

        vector.Select(cf => cf.DelinqBalance).Should().Equal(scalar.Select(cf => cf.DelinqBalance));
    }
}
