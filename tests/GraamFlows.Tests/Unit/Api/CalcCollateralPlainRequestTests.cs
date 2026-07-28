using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
/// Regression for the graam-flows#37 follow-up: BuildMarketRateArrays runs on
/// EVERY CalcCollateral request (with the flat-rate fallback provider when no
/// curve is supplied), so a plain fixed-rate pool with no MarketRates must
/// succeed. A `Cast&lt;int&gt;().Max()` in that method threw on the published
/// runtime and 400'd every call; this pins the plain path end-to-end.
/// Mirrors the failing harmony payload (single aggregate FRM, all-zero assumps,
/// no market rates).
/// </summary>
public class CalcCollateralPlainRequestTests
{
    [Fact]
    public void PlainFrm_NoMarketRates_Succeeds()
    {
        var request = new CalcCollateralRequest
        {
            Assets = new List<AssetDto>
            {
                new()
                {
                    AssetName = "Pool_Aggregate",
                    AssetId = "Pool_Aggregate",
                    InterestRateType = "FRM",
                    OriginalDate = new DateTime(2026, 7, 28),
                    OriginalBalance = 553_236_352.0,
                    CurrentBalance = 553_236_352.0,
                    OriginalInterestRate = 7.565,
                    CurrentInterestRate = 7.565,
                    OriginalAmortizationTerm = 360,
                    ServiceFee = 0.25,
                    GroupNum = "1",
                    IsIO = false,
                },
            },
            ProjectionDate = new DateTime(2025, 4, 25),
            Assumptions = new AssumptionsDto
            {
                Cpr = 0.0, Cdr = 0.0, Severity = 0.0, Delinquency = 0.0, Advancing = 0.0,
            },
            // No MarketRates — exercises the flat-rate fallback provider, which
            // still routes through BuildMarketRateArrays.
        };

        var controller = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance);
        var result = controller.Calculate(request);

        var ok = result.Result.Should().BeOfType<OkObjectResult>(
            "a plain fixed-rate pool with no market rates must not 400").Subject;
        var response = ok.Value.Should().BeOfType<CalcCollateralResponse>().Subject;
        response.Cashflows.Should().NotBeEmpty();
    }
}
