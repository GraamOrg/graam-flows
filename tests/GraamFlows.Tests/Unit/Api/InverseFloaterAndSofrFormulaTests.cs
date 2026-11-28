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
///     An inverse floater, and a coupon conditioned on the index, through the waterfall endpoint.
///
///     An agency REMIC's Terms Sheet states an inverse floater as "12.10% − (30-day Average SOFR ×
///     2)", held between a minimum and a maximum rate: a Floating coupon with a negative
///     <see cref="TrancheDto.ResetSlope" />. The DTO did not carry the slope, so every Floating
///     class ran at slope 1, an inverse as a plain floater. A "toggle" class pays one formula
///     below an index level and another above it ("If 30-day Average SOFR &lt;= 4.40%: SOFR +
///     6.40%, capped at 10.80%; if above, 0.00%"), which needs the index inside a coupon formula:
///     <c>sofr_30avg</c>.
/// </summary>
public class InverseFloaterAndSofrFormulaTests
{
    private const int Periods = 24;

    private static List<double[]> Flat(double rate) => new() { new[] { 0.0, rate } };

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

    private static DealDto SampleDeal()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GraamFlows.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull();
        var json = File.ReadAllText(Path.Combine(
            dir!.FullName, "src/GraamFlows.Api/Samples/stacr25dna1_unified.json"));
        return JsonSerializer.Deserialize<DealDto>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        })!;
    }

    /// <summary>The sample deal with its first floating class restated by <paramref name="restate" />.</summary>
    private static (DealDto deal, string name) DealWith(Action<TrancheDto> restate)
    {
        var deal = SampleDeal();
        var tranche = deal.Tranches.First(t =>
            string.Equals(t.CouponType, "Floating", StringComparison.OrdinalIgnoreCase));
        tranche.FloaterIndex = "Sofr30Avg";
        restate(tranche);
        return (deal, tranche.TrancheName);
    }

    private static double FirstCoupon(DealDto deal, string name, double sofr)
    {
        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);
        var result = controller.Execute(new WaterfallRequest
        {
            ProjectionDate = new DateTime(2025, 1, 25),
            CollateralCashflows = Tape(),
            Deal = deal,
            MarketRates = new Dictionary<string, List<double[]>> { ["Sofr30Avg"] = Flat(sofr) },
        });
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull("the waterfall must run: {0}", (result.Result as ObjectResult)?.Value);
        var cashflows = ((WaterfallResponse)ok!.Value!).TrancheCashflows[name];
        cashflows.Should().NotBeEmpty();
        return cashflows[0].Coupon;
    }

    [Theory]
    [InlineData(4.0, 4.10)] // 12.10 − 2 × 4.0
    [InlineData(3.0, 6.05)] // 6.10, held at the 6.05% maximum
    [InlineData(6.5, 0.00)] // −0.90, held at the 0.00% minimum
    public void An_inverse_floater_pays_its_spread_less_its_slope_times_the_index(double sofr, double coupon)
    {
        var (deal, name) = DealWith(t =>
        {
            t.FloaterSpread = 1210; // basis points: 12.10%
            t.ResetSlope = -2.0;
            t.Cap = 6.05;
            t.Floor = 0.0;
        });
        FirstCoupon(deal, name, sofr).Should().BeApproximately(coupon, 1e-9);
    }

    [Fact]
    public void A_floater_that_states_no_slope_pays_the_index_once()
    {
        var (deal, name) = DealWith(t =>
        {
            t.FloaterSpread = 180; // basis points: 1.80%
            t.ResetSlope = null;
            t.Cap = 6.00;
            t.Floor = 1.80;
        });
        FirstCoupon(deal, name, 4.0).Should().BeApproximately(5.80, 1e-9);
    }

    [Theory]
    [InlineData(4.00, 10.40)] // at or below 4.40%: SOFR + 6.40%
    [InlineData(4.40, 10.80)] // the boundary is the lower branch, at the 10.80% cap
    [InlineData(4.50, 0.00)] // above 4.40%: 0.00%
    public void A_coupon_formula_reads_the_30_day_average_sofr(double sofr, double coupon)
    {
        var (deal, name) = DealWith(t =>
        {
            t.CouponType = "Formula";
            t.CouponFormula = "sofr_30avg <= 4.40 ? MIN(MAX(sofr_30avg + 6.40, 6.40), 10.80) : 0.0";
        });
        FirstCoupon(deal, name, sofr).Should().BeApproximately(coupon, 1e-9);
    }

    /// <summary>
    ///     The two legs of a toggle as a caller writes them: below the level a floater held between
    ///     6.40% and 10.80%, above it an inverse floater (63.60% − 12 × SOFR) held between 0.00% and
    ///     10.80% — the legs meet at 10.80% at the level.
    /// </summary>
    [Theory]
    [InlineData(4.00, 10.40)]
    [InlineData(4.40, 10.80)]
    [InlineData(4.50, 9.60)]
    [InlineData(5.30, 0.00)]
    public void A_toggle_written_as_two_held_legs_pays_the_leg_its_index_level_selects(double sofr, double coupon)
    {
        var (deal, name) = DealWith(t =>
        {
            t.CouponType = "Formula";
            t.CouponFormula =
                "(sofr_30avg <= 4.4 ? MIN(MAX(6.4 + 1 * sofr_30avg, 6.4), 10.8) : MIN(MAX(63.6 + -12 * sofr_30avg, 0), 10.8))";
        });
        FirstCoupon(deal, name, sofr).Should().BeApproximately(coupon, 1e-9);
    }
}
