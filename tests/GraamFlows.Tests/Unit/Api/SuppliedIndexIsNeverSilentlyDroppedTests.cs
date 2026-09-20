using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     graam-flows#102, driven through the endpoints rather than through the resolver.
///
///     The three fallbacks this closes all answered 200 with no marker, so a run priced on an
///     index the caller never supplied was indistinguishable from a correct one. Tests that only
///     asserted "the request succeeded" passed on every one of them; these assert what the run
///     priced ON — the reset coupon, the tranche's index value, and the disclosure the response
///     now carries.
/// </summary>
public class SuppliedIndexIsNeverSilentlyDroppedTests
{
    private static readonly DateTime ProjDate = new(2025, 1, 1);
    private const double Margin = 2.0;

    private static List<double[]> Flat(double rate) => new() { new[] { 0.0, rate } };

    // ---------------------------------------------------------------- /api/CalcCollateral

    /// <summary>An ARM that resets ~3 months in off its index + margin, then holds.</summary>
    private static AssetDto Arm(string index) => new()
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

    private static ActionResult<CalcCollateralResponse> Collateral(
        Dictionary<string, List<double[]>>? marketRates, string assetIndex = "Libor1M")
    {
        var controller = new CalcCollateralController(
            NullLogger<CalcCollateralController>.Instance);
        return controller.Calculate(new CalcCollateralRequest
        {
            Assets = new List<AssetDto> { Arm(assetIndex) },
            ProjectionDate = ProjDate,
            Assumptions = new AssumptionsDto { Cpr = 0.0, Cdr = 0.0, Severity = 0.0 },
            MarketRates = marketRates,
        });
    }

    private static CalcCollateralResponse OkCollateral(
        Dictionary<string, List<double[]>>? marketRates, string assetIndex = "Libor1M")
    {
        var result = Collateral(marketRates, assetIndex);
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull("the request must succeed: {0}",
            (result.Result as ObjectResult)?.Value);
        return (CalcCollateralResponse)ok!.Value!;
    }

    private static string ErrorOf(ActionResult<CalcCollateralResponse> result)
    {
        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        return JsonSerializer.Serialize(bad.Value);
    }

    [Fact]
    public void B_CurvesSuppliedAndNoneResolved_IsRejectedRatherThanPricedAtFive()
    {
        // The reported bug. `SOFR_3M` is not a MarketDataInstEnum member, so the whole
        // dictionary vanished and the ARM reset off an invented 5% — margin + 5 = 7.0, a number
        // nothing in the response distinguished from a real one.
        var result = Collateral(new Dictionary<string, List<double[]>> { ["SOFR_3M"] = Flat(4.0) });

        result.Result.Should().BeOfType<BadRequestObjectResult>(
            "the caller supplied curves; answering with a rate they never gave is worse than "
            + "an error");
        var error = ErrorOf(result);
        error.Should().Contain("SOFR_3M");
        error.Should().Contain("Sofr30Avg", "the error must name the legal values");
    }

    [Fact]
    public void B_ACurveWithNoPointsIsAlsoRejected()
    {
        var result = Collateral(new Dictionary<string, List<double[]>> { ["Libor1M"] = new() });

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Compat_AnUnrecognizedNameBesideAGoodOneStillRuns_OnTheGoodCurve()
    {
        // THE COMPATIBILITY TEST. A caller that sends a recognized index alongside an
        // unrecognized one must keep working, and must price off the curve that landed — not off
        // the 5% fallback and not off 0.
        var response = OkCollateral(new Dictionary<string, List<double[]>>
        {
            ["Libor1M"] = Flat(4.0),
            ["SOFR_3M"] = Flat(4.3),
        });

        var cfs = response.Cashflows.OrderBy(cf => cf.Period).ToList();
        cfs[15].Wac.Should().BeApproximately(Margin + 4.0, 0.05,
            "the recognized curve must still drive the reset");

        var disclosed = response.MarketRateResolution!;
        disclosed.IndexSource.Should().Be(MarketRateSource.Supplied);
        disclosed.ResolvedIndices.Should().Contain("Libor1M");
        disclosed.UnrecognizedIndices.Should().ContainSingle().Which.Should().Be("SOFR_3M");
        disclosed.Warnings.Should().NotBeNullOrEmpty(
            "the dropped entry is exactly what the old code never said");
    }

    [Fact]
    public void A_ACurveLessRequestStillPricesOnFive_ButTheResponseSaysSo()
    {
        // The fallback #102 keeps, because a fixed-rate caller depends on it. The behaviour is
        // byte-for-byte what it was; what is new is that the response admits it.
        var response = OkCollateral(marketRates: null);

        response.Cashflows.OrderBy(cf => cf.Period).ToList()[15].Wac
            .Should().BeApproximately(Margin + 5.0, 0.05,
                "omitting the curve preserves the legacy flat-rate reset");

        var disclosed = response.MarketRateResolution!;
        disclosed.IndexSource.Should().Be(MarketRateSource.Assumed,
            "a run priced on an assumed index must be knowable from the response");
        disclosed.AssumedRate.Should().Be(5.0);
        disclosed.Warnings.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AnOrdinaryRunIsMarkedSupplied_AndCarriesNoWarning()
    {
        var response = OkCollateral(new Dictionary<string, List<double[]>>
        {
            ["Libor1M"] = Flat(4.0),
        });

        var disclosed = response.MarketRateResolution!;
        disclosed.IndexSource.Should().Be(MarketRateSource.Supplied);
        disclosed.AssumedRate.Should().BeNull();
        disclosed.Warnings.Should().BeNull();
    }

    // -------------------------------------------------------------------- /api/Waterfall

    private const int Periods = 60;

    private static List<PeriodCashflowDto> Tape()
    {
        var rows = new List<PeriodCashflowDto>();
        double bal = 120_000_000;
        var d = new DateTime(2025, 1, 25);
        for (var p = 0; p < Periods; p++)
        {
            const double sched = 2_000_000d;
            rows.Add(new PeriodCashflowDto
            {
                Period = p,
                CashflowDate = d.AddMonths(p),
                GroupNum = "1",
                BeginBalance = bal,
                Balance = bal - sched,
                ScheduledPrincipal = sched,
                Interest = bal * 0.05 / 12,
                NetInterest = bal * 0.05 / 12,
                Wac = 5.0,
                Wam = 360 - p,
            });
            bal -= sched;
        }
        return rows;
    }

    /// <summary>The repo's own sample deal — a real payable set, which a hand-built one is not.</summary>
    private static DealDto SampleDeal()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GraamFlows.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must be able to find the repo root");
        var json = File.ReadAllText(Path.Combine(
            dir!.FullName, "src/GraamFlows.Api/Samples/stacr25dna1_unified.json"));
        var deal = JsonSerializer.Deserialize<DealDto>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        });
        deal.Should().NotBeNull();
        return deal!;
    }

    /// <summary>The sample's floating classes, re-indexed onto one tenor under test.</summary>
    private static DealDto DealIndexedOn(string floaterIndex)
    {
        var deal = SampleDeal();
        foreach (var tranche in deal.Tranches)
            if (string.Equals(tranche.CouponType, "Floating", StringComparison.OrdinalIgnoreCase))
                tranche.FloaterIndex = floaterIndex;
        return deal;
    }

    private static ActionResult<WaterfallResponse> Waterfall(
        DealDto deal, Dictionary<string, List<double[]>>? marketRates)
    {
        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);
        return controller.Execute(new WaterfallRequest
        {
            ProjectionDate = new DateTime(2025, 1, 25),
            CollateralCashflows = Tape(),
            Deal = deal,
            MarketRates = marketRates,
        });
    }

    private static WaterfallResponse OkWaterfall(
        DealDto deal, Dictionary<string, List<double[]>>? marketRates)
    {
        var result = Waterfall(deal, marketRates);
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull("the waterfall must run: {0}",
            (result.Result as ObjectResult)?.Value);
        return (WaterfallResponse)ok!.Value!;
    }

    /// <summary>
    ///     Every swap tenor the engine names, including the nine the waterfall's own switch
    ///     omitted. Driven off the enum so a new member is covered without a test edit.
    /// </summary>
    public static TheoryData<string> EverySwapTenor()
    {
        var data = new TheoryData<string>();
        foreach (var inst in Enum.GetValues<Objects.TypeEnum.MarketDataInstEnum>())
            if (inst.ToString().StartsWith("Swap", StringComparison.Ordinal))
                data.Add(inst.ToString());
        return data;
    }

    [Theory]
    [MemberData(nameof(EverySwapTenor))]
    public void C_ACorrectlySpelledTenorReachesTheTranche(string tenor)
    {
        // Swap4Y/6Y/7Y/8Y/9Y/12Y/15Y/20Y/25Y parsed the enum and were then dropped by a switch
        // with no case for them and no default. MarketData's fields are bare doubles, so the
        // index read back 0.0 and the floater priced at its margin alone — under a 200.
        var response = OkWaterfall(
            DealIndexedOn(tenor),
            new Dictionary<string, List<double[]>> { [tenor] = Flat(4.0) });

        var floater = response.TrancheCashflows["A1"];
        floater.Should().NotBeEmpty();
        floater[0].IndexValue.Should().BeApproximately(4.0, 1e-9,
            "the tranche must price on the {0} curve the request supplied, not on 0.0", tenor);
    }

    [Fact]
    public void C_CurvesSuppliedAndNoneResolved_IsRejectedRatherThanPricedAtZero()
    {
        var result = Waterfall(
            DealIndexedOn("Sofr30Avg"),
            new Dictionary<string, List<double[]>> { ["SOFR_3M"] = Flat(4.0) });

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>(
            "this path had no flat-rate floor at all: an unassigned MarketData is 0.0").Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("SOFR_3M");
    }

    [Fact]
    public void Compat_Waterfall_AnUnrecognizedNameBesideAGoodOneStillRuns()
    {
        var response = OkWaterfall(
            DealIndexedOn("Sofr30Avg"),
            new Dictionary<string, List<double[]>>
            {
                ["Sofr30Avg"] = Flat(4.0),
                ["SOFR_3M"] = Flat(4.3),
            });

        response.TrancheCashflows["A1"][0].IndexValue.Should().BeApproximately(4.0, 1e-9);
        response.MarketRateResolution!.UnrecognizedIndices.Should()
            .ContainSingle().Which.Should().Be("SOFR_3M");
    }

    [Fact]
    public void A_Waterfall_ACurveLessRequestIsDisclosedAsAssumed()
    {
        var response = OkWaterfall(DealIndexedOn("Sofr30Avg"), marketRates: null);

        response.TrancheCashflows["A1"][0].IndexValue.Should().BeApproximately(5.0, 1e-9,
            "the legacy flat 5% is preserved for a curve-less request");
        response.MarketRateResolution!.IndexSource.Should().Be(MarketRateSource.Assumed);
        response.MarketRateResolution.AssumedRate.Should().Be(5.0);
    }
}
