using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Objects.DataObjects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
/// graam-harmony#5221 — the Net WAC and Eff WAC columns were blank for every deal.
///
/// `PeriodCashflows` carries `NetWac` (computed next to `WAC` on every period) and
/// `EffectiveWac` (stamped by the structure during the run), but `PeriodCashflowDto` declared
/// neither, so `CollateralCashflowMapper` could not carry them and no consumer ever saw them.
/// A blank column is indistinguishable from a zero, which is why this sat behind a WAC that
/// rendered fine on the very same row.
///
/// The mapper was only half the path. `/api/Waterfall` converts the collateral the caller POSTS
/// back into `PeriodCashflows` (`ConvertCollateralCashflows`), and that conversion dropped both
/// fields on the way IN — and the `CollateralCashflows(IList&lt;PeriodCashflows&gt;)` ctor only
/// stores the list, so nothing downstream put them back. Fixing only the mapper left the column
/// blank on the endpoint that returns the collateral the waterfall distributed, which is the
/// one a reinvesting deal has to read. So the end-to-end assertions below are the real pin and
/// the mapper test is the unit beneath them.
/// </summary>
public class CollateralWavgWireTests
{
    private static readonly DateTime FirstPayDate = new(2026, 2, 25);
    private const double PoolBalance = 100_000_000;

    // -- the mapper, on its own ---------------------------------------------------------------

    [Fact]
    public void The_wire_carries_every_wavg_the_period_computed()
    {
        var period = new PeriodCashflows
        {
            CashflowDate = FirstPayDate,
            GroupNum = "1",
            BeginBalance = PoolBalance,
            Balance = 99_000_000,
            Interest = 500_000,
            NetInterest = 480_000,
            ServiceFee = 20_000,
            WAC = 6.0,
            NetWac = 5.76,
            EffectiveWac = 5.5,
            WAM = 340,
            WALA = 6
        };

        var dto = CollateralCashflowMapper.ToDtos(new[] { period }).Single();

        // The two that were dropped...
        dto.NetWac.Should().Be(5.76, "net WAC is computed on every period and has to reach the wire");
        dto.EffectiveWac.Should().Be(5.5, "the WAC the waterfall distributed on has to reach the wire");
        // ...next to the ones that always arrived, so a mapper that stopped copying anything fails here.
        dto.Wac.Should().Be(6.0);
        dto.Wam.Should().Be(340);
        dto.Wala.Should().Be(6);
    }

    // -- end to end, through the endpoint a consumer actually reads ---------------------------

    [Fact]
    public void A_stated_net_wac_survives_the_round_trip_through_the_waterfall()
    {
        // The regression that a mapper-only fix does not catch: post collateral carrying
        // `netWac` (exactly what `/api/CalcCollateral` now hands back) and read it off the
        // collateral the waterfall returns. Before the inbound fix this was 0.00 on every row
        // while `wac` beside it came through untouched.
        var collateral = Collateral(statedNetWac: true);
        var response = Run(collateral);

        var rows = response.CollateralCashflows!;
        rows.Should().NotBeEmpty("the run must return the collateral it distributed");
        rows[0].NetWac.Should().BeApproximately(collateral[0].NetWac, 1e-9);
        rows.Should().OnlyContain(r => r.NetWac > 0, "no period may report a net WAC of zero here");
    }

    [Fact]
    public void An_unstated_net_wac_is_derived_rather_than_reported_as_zero()
    {
        // A caller whose collateral predates the field gets the engine's own rule applied to
        // the two values it derives net WAC from everywhere else — not a silent zero that a
        // reader cannot tell apart from a real 0% net WAC.
        var collateral = Collateral(statedNetWac: false);
        var rows = Run(collateral).CollateralCashflows!;

        var expected = collateral[0].NetInterest * 1200 / collateral[0].BeginBalance;
        expected.Should().BeGreaterThan(0, "the fixture has to make the derivation observable");
        rows[0].NetWac.Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void The_effective_wac_the_run_struck_reaches_the_wire()
    {
        // EffectiveWac is stamped during the run (net of the servicing fee and expenses), so
        // unlike NetWac it is produced here rather than carried. It must be BELOW the gross WAC
        // — equality would mean the expense step never ran and the assertion pins nothing.
        var rows = Run(Collateral(statedNetWac: true)).CollateralCashflows!;

        rows[0].EffectiveWac.Should().BeGreaterThan(0);
        rows[0].EffectiveWac.Should().BeLessThan(rows[0].Wac,
            "the effective WAC is struck net of the servicing fee the expense step paid");
    }

    // ------------------------------------------------------------------------------------------

    private static WaterfallResponse Run(List<PeriodCashflowDto> collateral)
    {
        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);
        var raw = controller.Execute(BuildRequest(collateral)).Result;
        var ok = raw as OkObjectResult;
        ok.Should().NotBeNull($"the waterfall request should succeed, got {raw?.GetType().Name}: "
            + $"{(raw as ObjectResult)?.Value}");
        return (ok!.Value as WaterfallResponse)!;
    }

    private static WaterfallRequest BuildRequest(List<PeriodCashflowDto> collateral) => new()
    {
        ProjectionDate = FirstPayDate.AddMonths(-1),
        CollateralCashflows = collateral,
        IncludeCollateralCashflows = true,
        Deal = new DealDto
        {
            DealName = "COLLATERAL_WAVG_WIRE_TEST",
            WaterfallType = "ComposableStructure",
            ClosingDate = FirstPayDate.AddMonths(-1),
            Tranches = new List<TrancheDto>
            {
                Note("A1", 70_000_000, 0),
                Note("B1", 30_000_000, 1)
            },
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                // EXPENSE is what strikes EffectiveWac (`PayExpensesStep`), so it has to be in
                // the order or the last assertion above pins a structural zero. The deal states
                // no expense tranche: the step still runs and still nets the collateral's own
                // servicing fee out of the WAC, which is the quantity under test.
                ExecutionOrder = new List<string>
                {
                    "EXPENSE", "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED",
                    "WRITEDOWN"
                },
                Steps = new List<WaterfallStepDto>
                {
                    new() { Type = "INTEREST", Structure = Seq("A1", "B1") },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A1", "B1") },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A1", "B1") },
                    new() { Type = "WRITEDOWN", Structure = Seq("B1", "A1") }
                }
            }
        }
    };

    private static TrancheDto Note(string name, double balance, int subOrder) => new()
    {
        TrancheName = name,
        OriginalBalance = balance,
        TrancheType = "Offered",
        CashflowType = "PI",
        CouponType = "Fixed",
        FixedCoupon = 5.0,
        SubordinationOrder = subOrder,
        FirstPayDate = FirstPayDate,
        PayFrequency = 12,
        PayDay = FirstPayDate.Day
    };

    private static PayableStructureDto Seq(params string[] tranches) =>
        new() { Type = "SEQ", Tranches = tranches.ToList() };

    private static List<PeriodCashflowDto> Collateral(bool statedNetWac)
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        const double wac = 9.0;
        for (var i = 0; i < 12 && balance > 1.0; i++)
        {
            var interest = balance * wac / 100 / 12;
            var serviceFee = balance * 0.25 / 100 / 12;
            var scheduled = i == 11 ? balance : balance * 0.02;
            var netInterest = interest - serviceFee;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1,
                CashflowDate = FirstPayDate.AddMonths(i),
                GroupNum = "1",
                BeginBalance = balance,
                Balance = balance - scheduled,
                ScheduledPrincipal = scheduled,
                Interest = interest,
                NetInterest = netInterest,
                ServiceFee = serviceFee,
                Wac = wac,
                NetWac = statedNetWac ? netInterest * 1200 / balance : 0,
                Wam = 360 - i
            });
            balance -= scheduled;
        }

        return periods;
    }
}
