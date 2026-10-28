using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     ABS describes prepayments only, so it pairs with any default convention. The scalar ABS path
///     used to hard-code CDR, which ran ABS + MDR and ABS + ORIGMDR as CDR while the per-period
///     arrays paired them correctly. And the PolyPaths strings run CPR/CDR whatever is declared, so
///     any other declared type there is refused rather than silently run as CPR/CDR.
/// </summary>
public class AbsDefaultTypePairingTests
{
    private static readonly DateTime ProjectionDate = new(2024, 6, 1);
    private const int Periods = 72;

    private static AssetDto Loan(string id) => new()
    {
        AssetName = id,
        AssetId = id,
        InterestRateType = "FRM",
        OriginalDate = new DateTime(2024, 1, 1),
        OriginalBalance = 25_000.0,
        CurrentBalance = 24_000.0,
        OriginalInterestRate = 9.0,
        CurrentInterestRate = 9.0,
        OriginalAmortizationTerm = 72,
        GroupNum = "1",
    };

    private static CalcCollateralRequest Request(string prepaymentType, string defaultType) => new()
    {
        Assets = new List<AssetDto> { Loan("A"), Loan("B") },
        ProjectionDate = ProjectionDate,
        Assumptions = new AssumptionsDto
        {
            Cpr = 1.5,
            Cdr = 0.1,
            Severity = 50.0,
            PrepaymentType = prepaymentType,
            DefaultType = defaultType,
        },
    };

    private static ActionResult Post(CalcCollateralRequest request) =>
        new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(request).Result!;

    private static CalcCollateralResponse Ok(CalcCollateralRequest request) =>
        Post(request).Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<CalcCollateralResponse>().Subject;

    private static CalcCollateralRequest AsVectors(CalcCollateralRequest request)
    {
        request.Assumptions.CprVector = Enumerable.Repeat(request.Assumptions.Cpr, Periods).ToArray();
        request.Assumptions.CdrVector = Enumerable.Repeat(request.Assumptions.Cdr, Periods).ToArray();
        return request;
    }

    [Theory]
    [InlineData("MDR")]
    [InlineData("ORIGMDR")]
    [InlineData("CDR")]
    public void A_Scalar_Abs_Run_Matches_The_Vector_Run_Cell_For_Cell(string defaultType)
    {
        var scalar = Ok(Request("ABS", defaultType)).Cashflows;
        var vector = Ok(AsVectors(Request("ABS", defaultType))).Cashflows;

        scalar.Should().HaveCount(vector.Count);
        for (var i = 0; i < scalar.Count; i++)
        {
            scalar[i].DefaultedPrincipal.Should().BeApproximately(vector[i].DefaultedPrincipal, 1e-9, $"period {i}");
            scalar[i].UnscheduledPrincipal.Should().BeApproximately(vector[i].UnscheduledPrincipal, 1e-9, $"period {i}");
            scalar[i].CollateralLoss.Should().BeApproximately(vector[i].CollateralLoss, 1e-9, $"period {i}");
        }
    }

    [Theory]
    [InlineData("MDR")]
    [InlineData("ORIGMDR")]
    public void A_Scalar_Abs_Run_Does_Not_Read_A_Monthly_Rate_As_Cdr(string defaultType)
    {
        // 0.1 as a monthly hazard is ~1.2% a year; read as CDR it is 0.1% a year.
        var monthly = Ok(Request("ABS", defaultType)).Cashflows.Sum(cf => cf.DefaultedPrincipal);
        var annual = Ok(Request("ABS", "CDR")).Cashflows.Sum(cf => cf.DefaultedPrincipal);

        monthly.Should().BeGreaterThan(5 * annual);
    }

    [Theory]
    [InlineData("ABS", "CDR", "prepaymentType")]
    [InlineData("SMM", "CDR", "prepaymentType")]
    [InlineData("CPR", "MDR", "defaultType")]
    [InlineData("CPR", "ORIGMDR", "defaultType")]
    public void PolyPaths_Strings_With_Any_Other_Type_Are_Refused_Not_Run_As_Cpr_Cdr(
        string prepaymentType, string defaultType, string field)
    {
        var request = Request(prepaymentType, defaultType);
        request.Assumptions.CprVectorStr = "1.5";

        var error = Post(request).Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString();

        error.Should().Contain($"assumptions.{field}").And.Contain("cprVector");
    }

    [Fact]
    public void PolyPaths_Strings_With_Cpr_And_Cdr_Still_Run()
    {
        var request = Request("CPR", "CDR");
        request.Assumptions.CprVectorStr = "1.0R12,6.0";

        Ok(request).Cashflows.Should().NotBeEmpty();
    }
}
