using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
/// graam-flows#37: CalcCollateral must reset ARM/hybrid coupons off a supplied
/// forward-rate curve (index + margin), not a hardcoded flat 5%. These drive the
/// controller end-to-end and assert the post-reset coupon tracks the curve, moves
/// with the scenario, and falls back to the legacy flat rate when no curve is sent.
/// </summary>
public class CalcCollateralArmCurveTests
{
    private readonly ITestOutputHelper _output;

    public CalcCollateralArmCurveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly DateTime ProjDate = new(2025, 1, 1);
    private const double Margin = 2.0;

    // ARM: 5% initial, resets ~3 months in off <index> + 2.0 margin, then holds
    // (long adjustment period) so the post-reset coupon is stable. Wide caps so
    // the reset isn't clamped for the rates under test.
    private static AssetDto ArmAsset(string index = "Libor1M") => new()
    {
        AssetName = "ARM",
        AssetId = "ARM",
        InterestRateType = "ARM",
        OriginalDate = ProjDate.AddMonths(-1),
        OriginalBalance = 100_000,
        CurrentBalance = 100_000,
        OriginalInterestRate = 5.0,
        CurrentInterestRate = 5.0,
        InitialRate = 5.0,
        OriginalAmortizationTerm = 360,
        InitialAdjustmentPeriod = 3,
        AdjustmentPeriod = 120,
        IndexName = index,
        IndexMargin = Margin,
        AdjustmentCap = 10.0,
        LifeAdjustmentCap = 20.0,
        LifeAdjustmentFloor = 0.0,
        ServiceFee = 0.0,
        GroupNum = "1",
    };

    private static List<PeriodCashflowDto> Run(
        Dictionary<string, List<double[]>>? marketRates, string index = "Libor1M")
    {
        var request = new CalcCollateralRequest
        {
            Assets = new List<AssetDto> { ArmAsset(index) },
            ProjectionDate = ProjDate,
            Assumptions = new AssumptionsDto { Cpr = 0.0, Cdr = 0.0, Severity = 0.0 },
            MarketRates = marketRates,
        };

        var controller = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance);
        var result = controller.Calculate(request);
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<CalcCollateralResponse>().Subject;
        return response.Cashflows.OrderBy(cf => cf.Period).ToList();
    }

    private static Dictionary<string, List<double[]>> FlatCurve(double rate, string index = "Libor1M") =>
        new() { [index] = new List<double[]> { new[] { 0.0, rate } } };

    [Fact]
    public void ArmReset_TracksSuppliedCurve_MarginPlusIndex()
    {
        // Curve at 4.0 → post-reset coupon = margin(2) + 4 = 6.0. The reset lands
        // a few months in (initial rate 5.0 through period 3, then 6.0).
        var cfs = Run(FlatCurve(4.0));
        cfs[2].Wac.Should().BeApproximately(5.0, 0.05, "pre-reset coupon is the initial rate");
        cfs[15].Wac.Should().BeApproximately(Margin + 4.0, 0.05,
            "ARM coupon resets to margin + the supplied index rate");
    }

    [Fact]
    public void ArmReset_MovesWithScenario()
    {
        // A different curve level moves the post-reset coupon.
        var low = Run(FlatCurve(3.0))[15].Wac;
        var high = Run(FlatCurve(6.0))[15].Wac;

        low.Should().BeApproximately(Margin + 3.0, 0.05);
        high.Should().BeApproximately(Margin + 6.0, 0.05);
        high.Should().BeGreaterThan(low, "a higher rate curve raises the reset coupon");
    }

    [Fact]
    public void NoCurve_FallsBackToLegacyFlatRate()
    {
        // Without a curve the provider is the legacy ConstantRateProvider(5.0), so
        // the reset is margin + 5 = 7.0 — the pre-fix behavior, preserved.
        var cfs = Run(marketRates: null);
        cfs[15].Wac.Should().BeApproximately(Margin + 5.0, 0.05,
            "omitting the curve preserves the legacy flat-rate reset");
    }

    [Fact]
    public void SofrIndexedArm_ResetsOffCurve_DoesNotThrow()
    {
        // A SOFR-indexed ARM. MarketDataInstEnum.Sofr30Avg has ordinal 20; before
        // the BuildMarketRateArrays fix (a 5-element array indexed by that ordinal)
        // this threw IndexOutOfRangeException. It must now reset off the SOFR curve:
        // margin(2) + 4.5 = 6.5.
        var cfs = Run(FlatCurve(4.5, "Sofr30Avg"), index: "Sofr30Avg");
        cfs[2].Wac.Should().BeApproximately(5.0, 0.05, "pre-reset coupon is the initial rate");
        cfs[15].Wac.Should().BeApproximately(Margin + 4.5, 0.05,
            "the SOFR-indexed ARM resets to margin + the supplied SOFR rate");
    }

    [Fact]
    public void ForwardPath_LaterResetsFollowRisingCurve()
    {
        // A rising forward path: 4% now, 9% at 10y. Reset at ~month 3 uses ~4%,
        // so an early post-reset coupon is well below the far end of the curve.
        var rising = new Dictionary<string, List<double[]>>
        {
            ["Libor1M"] = new List<double[]>
            {
                new[] { 0.0, 4.0 },
                new[] { 120.0, 9.0 },
            },
        };
        var cfs = Run(rising);
        cfs[15].Wac.Should().BeApproximately(Margin + 4.0, 0.15,
            "the ~month-3 reset uses the near end of the forward curve (~4%)");
    }
}
