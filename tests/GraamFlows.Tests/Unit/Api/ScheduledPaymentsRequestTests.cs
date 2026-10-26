using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     <c>scheduledPayments</c> on an asset of <c>/api/calccollateral</c>: the asset pays the stated
///     schedule, and a schedule sent with a feature it replaces is a 400 that says why. Synthetic.
/// </summary>
public class ScheduledPaymentsRequestTests
{
    private static readonly DateTime ProjectionDate = new(2026, 2, 1);
    private const double RatePct = 6.0;

    // An annual payer: the January rows pay, the others nothing.
    private static readonly double[] Payments =
        Enumerable.Range(0, 24).Select(i => ProjectionDate.AddMonths(i).Month == 1 ? 5_000.0 : 0.0).ToArray();

    private static AssetDto Receivable() => new()
    {
        AssetName = "EQ",
        AssetId = "EQ",
        InterestRateType = "FRM",
        OriginalDate = ProjectionDate,
        OriginalBalance = Value(),
        CurrentBalance = Value(),
        OriginalInterestRate = RatePct,
        CurrentInterestRate = RatePct,
        OriginalAmortizationTerm = Payments.Length,
        GroupNum = "1",
        ScheduledPayments = Payments
    };

    private static double Value() => Payments.Select((p, i) => p / Math.Pow(1 + RatePct / 1200.0, i + 1)).Sum();

    private static ActionResult Post(AssetDto asset) =>
        new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(new CalcCollateralRequest
        {
            Assets = new List<AssetDto> { asset },
            ProjectionDate = ProjectionDate,
            Assumptions = new AssumptionsDto { Cpr = 0.0, Cdr = 0.0 }
        }).Result!;

    [Fact]
    public void The_Asset_Pays_Its_Stated_Schedule()
    {
        var cashflows = Post(Receivable()).Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<CalcCollateralResponse>().Subject.Cashflows
            .OrderBy(cf => cf.CashflowDate).ToList();

        cashflows.Select(cf => Math.Round(cf.Interest + cf.ScheduledPrincipal, 2))
            .Should().Equal(Payments);
        cashflows[^1].Balance.Should().BeApproximately(0.0, 0.005);
    }

    [Fact]
    public void A_Schedule_On_A_Less_Than_Monthly_Asset_Is_A_Bad_Request()
    {
        var asset = Receivable();
        asset.PaymentFrequency = 1;
        asset.NextPaymentDate = new DateTime(2027, 1, 15);

        Post(asset).Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()
            .Should().Contain("scheduled payments cannot be combined with a payment frequency of 1");
    }
}
