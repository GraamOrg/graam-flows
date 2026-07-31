using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
/// graam-harmony#3691: a class-cut interest-only strip (e.g. SGAMT A-1AX cut off
/// A-1A) must carry a NOTIONAL that amortizes with the bond it is cut off of — not
/// the pool, and not sit flat at issuance.
///
/// On the unified-waterfall + ComposableStructure path (every harmony deal), an IO
/// tranche was routed to the pool-referenced notional (<c>SettleNotionalBalances</c>,
/// keyed on <c>CashflowType.InterestOnly</c>), so a senior-referenced strip tracked
/// the POOL (which amortizes slower than the senior) — overstating its notional. The
/// engine already had the reference mechanism (<c>PayNotionalClasses</c> off
/// <c>ExchangableTranche</c>), but the DTO transform never emitted
/// <c>PayFrom = Notional</c> for a class-cut IO, and the pool pass clobbered it even
/// when it did. This test drives the full DTO → <see cref="WaterfallController"/> →
/// engine path and asserts the notional strip mirrors its reference bond A1A exactly,
/// receives no principal, and does NOT track the pool.
/// </summary>
public class NotionalIoTracksReferenceTests
{
    private static readonly DateTime FirstPayDate = new(2026, 2, 25);
    private const double A1ABalance = 60_000_000; // the reference bond (senior)
    private const double BBalance = 40_000_000; // a junior, so A1A ≠ pool as it pays down
    private const double PoolBalance = A1ABalance + BBalance;

    [Fact]
    public void NotionalIo_UnifiedWaterfall_TracksItsReferenceBondNotThePool()
    {
        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);
        var actionResult = controller.Execute(BuildRequest());

        var ok = actionResult.Result as OkObjectResult;
        ok.Should().NotBeNull("the waterfall request should succeed");
        var response = (ok!.Value as WaterfallResponse)!;

        response.TrancheCashflows.Should().ContainKey("A1AX", "the notional IO must appear in the output");
        var a1a = response.TrancheCashflows["A1A"];
        var a1ax = response.TrancheCashflows["A1AX"];

        // 1) The notional amortizes with A1A EXACTLY, every period.
        for (var i = 0; i < a1a.Count; i++)
            a1ax[i].Balance.Should().BeApproximately(a1a[i].Balance, 1.0,
                $"A-1AX notional must equal its reference A-1A balance in period {i + 1}");

        // 2) It is interest-only — it receives NO principal.
        a1ax.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal).Should().BeApproximately(0, 1.0,
            "a notional IO strip receives no principal");

        // 3) It does not sit flat at issuance — the #3691 symptom.
        a1ax.Last().Balance.Should().BeApproximately(0, 1.0,
            "the notional must amortize to zero as A-1A does, not sit flat at issuance");

        // 4) It tracks A-1A, NOT the pool. Once A-1A has paid down but the junior B is
        //    still outstanding, the pool balance is strictly larger than A-1AX.
        var a1aGone = Enumerable.Range(0, a1a.Count).First(i => a1a[i].Balance < BBalance);
        a1ax[a1aGone].Balance.Should().BeLessThan(PoolBalance * 0.99,
            "A-1AX must follow the senior A-1A, not the (larger, slower) pool balance");
    }

    private static WaterfallRequest BuildRequest() => new()
    {
        ProjectionDate = FirstPayDate.AddMonths(-1),
        CollateralCashflows = BuildCollateral(),
        Deal = new DealDto
        {
            DealName = "NOTIONAL_IO_TEST",
            WaterfallType = "ComposableStructure",
            ClosingDate = FirstPayDate.AddMonths(-1),
            Tranches = new List<TrancheDto>
            {
                Senior("A1A", A1ABalance, 0),
                Senior("B", BBalance, 1),
                // The class-cut IO: interest-only, its notional tracks A1A (#3691).
                new()
                {
                    TrancheName = "A1AX",
                    OriginalBalance = A1ABalance,
                    TrancheType = "Offered",
                    CashflowType = "IO",
                    CouponType = "Fixed",
                    FixedCoupon = 1.5,
                    ClassReference = "A1AX", // group membership = self
                    NotionalReference = "A1A", // the class-cut signal: notional tracks A1A
                    SubordinationOrder = 50,
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
                    new() { Type = "INTEREST", Structure = Seq("A1A", "A1AX", "B") },
                    // Sequential: A1A pays down BEFORE B, so A1A ≠ pool while B is outstanding.
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A1A", "B") },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A1A", "B") },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A1A", "B") },
                    new() { Type = "WRITEDOWN", Structure = Seq("B", "A1A") }
                }
            }
        }
    };

    private static TrancheDto Senior(string name, double balance, int subOrder) => new()
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

    private static List<PeriodCashflowDto> BuildCollateral()
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        const double wac = 6.0;
        for (var i = 0; i < 36 && balance > 1.0; i++)
        {
            var interest = balance * wac / 100 / 12;
            var scheduled = i == 35 ? balance : balance * 0.05;
            var unscheduled = i == 35 ? 0.0 : balance * 0.02;
            var endBalance = balance - scheduled - unscheduled;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1,
                CashflowDate = FirstPayDate.AddMonths(i),
                GroupNum = "1",
                BeginBalance = balance,
                Balance = endBalance,
                ScheduledPrincipal = scheduled,
                UnscheduledPrincipal = unscheduled,
                Interest = interest,
                NetInterest = interest,
                Wac = wac
            });
            balance = endBalance;
        }

        return periods;
    }
}
