using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     <c>prepaymentType: "CPY"</c> on <c>/api/calccollateral</c>: the assets' call-protection dates
///     ride on the asset, the convention is a switch on the scenario, and the response says which
///     convention ran and which assets it could not hold.
/// </summary>
public class CpyPrepaymentRequestTests
{
    private static readonly DateTime ProjectionDate = new(2024, 6, 1);
    private static readonly DateTime Open = new(2027, 3, 10);

    private static AssetDto Loan(string id, bool dated) => new()
    {
        AssetName = id,
        AssetId = id,
        InterestRateType = "FRM",
        OriginalDate = new DateTime(2024, 1, 1),
        OriginalBalance = 10_000_000.0,
        CurrentBalance = 10_000_000.0,
        OriginalInterestRate = 5.0,
        CurrentInterestRate = 5.0,
        OriginalAmortizationTerm = 120,
        BalloonAmortizationTerm = 360,
        GroupNum = id,
        LockoutEndDate = dated ? new DateTime(2025, 1, 10) : null,
        DefeasanceEndDate = dated ? Open : null,
    };

    private static CalcCollateralRequest Request(string? prepaymentType, params AssetDto[] assets) => new()
    {
        Assets = assets.ToList(),
        ProjectionDate = ProjectionDate,
        Assumptions = new AssumptionsDto { Cpr = 25.0, PrepaymentType = prepaymentType },
    };

    private static ActionResult Post(CalcCollateralRequest request) =>
        new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(request).Result!;

    private static CalcCollateralResponse Ok(CalcCollateralRequest request) =>
        Post(request).Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<CalcCollateralResponse>().Subject;

    [Fact]
    public void A_Scalar_Cpy_Holds_A_Dated_Loan_Until_Its_Open_Month()
    {
        // The scalar path is the one that builds its assumptions through the CPR helper: CPY has to
        // survive it, or the dates would be read under plain CPR.
        var response = Ok(Request("CPY", Loan("A", dated: true)));

        var before = response.Cashflows.Where(cf => cf.CashflowDate < new DateTime(Open.Year, Open.Month, 1)).ToList();
        before.Should().NotBeEmpty();
        before.Should().OnlyContain(cf => cf.UnscheduledPrincipal == 0.0);
        response.Cashflows.Sum(cf => cf.UnscheduledPrincipal).Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void Per_Period_Arrays_Run_Cpy_Too()
    {
        var request = Request("CPY", Loan("A", dated: true));
        request.Assumptions.CprVector = Enumerable.Repeat(25.0, 360).ToArray();

        var response = Ok(request);

        response.Cashflows.Where(cf => cf.CashflowDate < new DateTime(Open.Year, Open.Month, 1))
            .Should().OnlyContain(cf => cf.UnscheduledPrincipal == 0.0);
    }

    [Fact]
    public void The_Response_Says_Cpy_Ran_And_Which_Assets_Had_No_Dates()
    {
        var resolution = Ok(Request("CPY", Loan("A", dated: true), Loan("B", dated: false))).PrepaymentResolution;

        resolution.Should().NotBeNull();
        resolution!.PrepaymentType.Should().Be("CPY");
        resolution.AssetsWithCallProtectionDates.Should().Be(1);
        resolution.AssetsWithoutCallProtectionDates.Should().Be(1);
        resolution.AssetIdsWithoutCallProtectionDates.Should().Equal("B");
    }

    [Fact]
    public void The_Response_Says_Cpr_Ran_When_No_Type_Is_Sent()
    {
        var resolution = Ok(Request(null, Loan("A", dated: true))).PrepaymentResolution;

        resolution!.PrepaymentType.Should().Be("CPR");
        resolution.AssetsWithCallProtectionDates.Should().BeNull();
        resolution.AssetsWithoutCallProtectionDates.Should().BeNull();
    }

    [Fact]
    public void Cpy_Is_Accepted_Case_And_Space_Insensitively()
    {
        Ok(Request(" cpy ", Loan("A", dated: true))).PrepaymentResolution!.PrepaymentType.Should().Be("CPY");
    }

    [Fact]
    public void Cpy_With_PolyPaths_Strings_Is_Refused_Not_Run_As_Cpr()
    {
        var request = Request("CPY", Loan("A", dated: true));
        request.Assumptions.CprVectorStr = "25.0";

        var error = Post(request).Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString();

        error.Should().Contain("CPY").And.Contain("cprVector");
    }
}
