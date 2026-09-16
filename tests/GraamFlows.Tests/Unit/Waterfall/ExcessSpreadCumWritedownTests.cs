using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// graam-harmony#5221 — the Cum WD column was blank on the excess-spread strip.
///
/// <c>CumWritedown</c> advances in exactly one place, <c>DynamicClass.Writedown</c>, and only
/// <c>if (RecievesPrincipal())</c>. An XS strip receives no principal — its balance is the pool
/// notional, reset every period — so it never reaches that path. Its loss is instead booked by
/// <c>AbsorbLossFromExcessSpread</c>, the excess-spread first-loss step, which wrote
/// <c>cf.Writedown</c> DIRECTLY and left the cumulative counter untouched.
///
/// The visible result: the strip that absorbs the deal's entire first loss reported a per-period
/// writedown in every row and a cumulative of 0.00 in every row. On the funded bonds — which do
/// reach <c>DynamicClass.Writedown</c> — the column was correct all along, so the defect read as
/// "blank for XS only".
///
/// The property pinned here holds on every class shape and cannot drift with the fixture: the
/// cumulative column IS the running total of the period column, whichever path booked it.
/// </summary>
public class ExcessSpreadCumWritedownTests
{
    private static readonly DateTime FirstPayDate = new(2026, 2, 25);
    private const double A1Balance = 70_000_000;
    private const double B1Balance = 30_000_000;
    private const double PoolBalance = A1Balance + B1Balance;

    [Fact]
    public void The_excess_spread_strip_reports_a_running_cumulative_writedown()
    {
        var xs = Run().TrancheCashflows["XS"];

        // Anti-vacuity: the absorb path has to have fired, or "cumulative equals the running
        // total" is 0 == 0 in every row and pins nothing.
        xs.Sum(c => c.Writedown).Should().BeGreaterThan(0,
            "the XS strip absorbs the period loss out of the excess spread it swept");

        AssertCumulativeIsTheRunningTotal(xs, "XS");
    }

    [Fact]
    public void A_funded_bond_still_reports_a_running_cumulative_writedown()
    {
        // The regression guard on the path that was already correct: a funded bond books its
        // loss through DynamicClass.Writedown, and carrying the counter onto rows that path did
        // not mint must not disturb it.
        var b1 = Run().TrancheCashflows["B1"];

        b1.Sum(c => c.Writedown).Should().BeGreaterThan(0,
            "the loss beyond the excess spread cascades to the most junior funded bond");

        AssertCumulativeIsTheRunningTotal(b1, "B1");
    }

    private static void AssertCumulativeIsTheRunningTotal(List<TrancheCashflowDto> rows, string name)
    {
        var running = 0.0;
        for (var i = 0; i < rows.Count; i++)
        {
            running += rows[i].Writedown;
            rows[i].CumWritedown.Should().BeApproximately(running, 0.01,
                $"{name} period {i + 1}: the cumulative writedown is the running total of the " +
                "period writedowns");
        }
    }

    // ------------------------------------------------------------------------------------------

    private static WaterfallResponse Run()
    {
        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);
        var ok = controller.Execute(BuildRequest()).Result as OkObjectResult;
        ok.Should().NotBeNull("the waterfall request should succeed");
        return (ok!.Value as WaterfallResponse)!;
    }

    private static WaterfallRequest BuildRequest() => new()
    {
        ProjectionDate = FirstPayDate.AddMonths(-1),
        CollateralCashflows = BuildCollateral(),
        Deal = new DealDto
        {
            DealName = "EXCESS_SPREAD_CUM_WRITEDOWN_TEST",
            WaterfallType = "ComposableStructure",
            ClosingDate = FirstPayDate.AddMonths(-1),
            Tranches = new List<TrancheDto>
            {
                Note("A1", A1Balance, 0),
                Note("B1", B1Balance, 1),
                // The excess-spread strip: IO, ExcessInterest, a pool-sized notional.
                new()
                {
                    TrancheName = "XS",
                    OriginalBalance = PoolBalance,
                    TrancheType = "Offered",
                    CashflowType = "IO",
                    CouponType = "ExcessInterest",
                    FixedCoupon = 0.0,
                    ClassReference = "XS",
                    SubordinationOrder = 2,
                    FirstPayDate = FirstPayDate,
                    PayFrequency = 12,
                    PayDay = FirstPayDate.Day
                }
            },
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = new List<string>
                {
                    "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED",
                    "PRINCIPAL_RECOVERY", "WRITEDOWN"
                },
                Steps = new List<WaterfallStepDto>
                {
                    new() { Type = "INTEREST", Structure = Seq("A1", "B1", "XS") },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A1", "B1") },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A1", "B1") },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A1", "B1") },
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

    private static PayableStructureDto Seq(params string[] tranches) => new()
    {
        Type = "SEQ",
        Tranches = tranches.ToList()
    };

    /// <summary>
    /// A pool that loses more than its excess spread can cover, so the strip absorbs in every
    /// period AND the remainder cascades onto the funded bonds.
    /// </summary>
    private static List<PeriodCashflowDto> BuildCollateral()
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        const double wac = 9.0;
        for (var i = 0; i < 24 && balance > 1.0; i++)
        {
            var interest = balance * wac / 100 / 12;
            var defaulted = balance * 0.01;
            var recovery = defaulted * 0.5;
            var scheduled = i == 23 ? Math.Max(0, balance - defaulted) : balance * 0.01;
            var endBalance = balance - scheduled - defaulted;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1,
                CashflowDate = FirstPayDate.AddMonths(i),
                GroupNum = "1",
                BeginBalance = balance,
                Balance = endBalance,
                ScheduledPrincipal = scheduled,
                Interest = interest,
                NetInterest = interest,
                DefaultedPrincipal = defaulted,
                RecoveryPrincipal = recovery,
                CollateralLoss = defaulted - recovery,
                Wac = wac
            });
            balance = endBalance;
        }

        return periods;
    }
}
