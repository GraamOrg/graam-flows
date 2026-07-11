using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
/// End-to-end regression for graam-harmony#2808: an exchangeable / combinable
/// class (e.g. EFMT 2026-CES2 A1 = A1A + A1B) was never paid on the
/// unified-waterfall + ComposableStructure path that every harmony deal uses.
///
/// The engine already carried the exchange overlay (<c>PayExchangeables</c>),
/// but it only fires when the exchange class's DealStructure has
/// <c>PayFrom = Exchange</c> AND a comma-joined <c>ExchangableTranche</c>. The
/// unified-waterfall DTO transform dropped both for an <c>Exchanged</c> tranche,
/// so the exchange class sat flat at its issuance balance forever ($0 principal /
/// interest / writedown). This test drives the full DTO → <see cref="WaterfallController"/>
/// → engine path and asserts the exchange class now mirrors the combined
/// cashflows of its components and amortizes to zero as they do.
/// </summary>
public class ExchangeablePassthroughTests
{
    private static readonly DateTime FirstPayDate = new(2026, 2, 25);
    private const double A1ABalance = 60_000_000;
    private const double A1BBalance = 40_000_000;
    private const double PoolBalance = A1ABalance + A1BBalance;

    [Fact]
    public void ExchangeClass_UnifiedWaterfall_MirrorsComponentsAndAmortizes()
    {
        var request = BuildRequest();
        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);

        var actionResult = controller.Execute(request);

        var ok = actionResult.Result as OkObjectResult;
        ok.Should().NotBeNull("the waterfall request should succeed");
        var response = ok!.Value as WaterfallResponse;
        response.Should().NotBeNull();

        var a1a = response!.TrancheCashflows["A1A"];
        var a1b = response.TrancheCashflows["A1B"];
        response.TrancheCashflows.Should().ContainKey("A1", "the exchange class must appear in the output");
        var a1 = response.TrancheCashflows["A1"];

        a1.Should().NotBeEmpty("the exchange class must produce cashflows");

        // The exchange class must have actually paid down — the symptom was it
        // sitting flat at its issuance balance for the whole deal.
        var a1TotalPrincipal = a1.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal);
        a1TotalPrincipal.Should().BeGreaterThan(0,
            "the exchange class must receive principal, not sit flat at issuance (graam-harmony#2808)");
        a1.Last().Balance.Should().BeApproximately(0, 1.0,
            "the exchange class must amortize to zero as its components do");

        // Per-period mirror: A1 == A1A + A1B for principal, interest and writedown.
        var writedownExercised = false;
        for (var i = 0; i < a1.Count; i++)
        {
            var expectedPrincipal = a1a[i].ScheduledPrincipal + a1a[i].UnscheduledPrincipal
                + a1b[i].ScheduledPrincipal + a1b[i].UnscheduledPrincipal;
            var actualPrincipal = a1[i].ScheduledPrincipal + a1[i].UnscheduledPrincipal;
            actualPrincipal.Should().BeApproximately(expectedPrincipal, 1.0,
                $"A1 principal must equal A1A+A1B in period {i + 1}");

            a1[i].Interest.Should().BeApproximately(a1a[i].Interest + a1b[i].Interest, 1.0,
                $"A1 interest must equal A1A+A1B in period {i + 1}");

            var expectedWritedown = a1a[i].Writedown + a1b[i].Writedown;
            a1[i].Writedown.Should().BeApproximately(expectedWritedown, 1.0,
                $"A1 writedown must equal A1A+A1B in period {i + 1}");
            if (expectedWritedown > 0.01)
                writedownExercised = true;

            a1[i].Balance.Should().BeApproximately(a1a[i].Balance + a1b[i].Balance, 1.0,
                $"A1 balance must equal A1A+A1B in period {i + 1}");
        }

        writedownExercised.Should().BeTrue(
            "the collateral carries losses, so the writedown pass-through must be exercised");

        // Lifetime totals tie.
        a1TotalPrincipal.Should().BeApproximately(
            a1a.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal)
            + a1b.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal), 1.0);
        a1.Sum(c => c.Writedown).Should().BeApproximately(
            a1a.Sum(c => c.Writedown) + a1b.Sum(c => c.Writedown), 1.0);
    }

    private static WaterfallRequest BuildRequest()
    {
        return new WaterfallRequest
        {
            ProjectionDate = FirstPayDate.AddMonths(-1),
            CollateralCashflows = BuildCollateral(),
            Deal = new DealDto
            {
                DealName = "EXCH_TEST",
                WaterfallType = "ComposableStructure",
                ClosingDate = FirstPayDate.AddMonths(-1),
                Tranches = new List<TrancheDto>
                {
                    Senior("A1A", A1ABalance, 0),
                    Senior("A1B", A1BBalance, 1),
                    // Exchangeable mirror: A1 = A1A + A1B (100% combination).
                    new()
                    {
                        TrancheName = "A1",
                        OriginalBalance = PoolBalance,
                        TrancheType = "Exchanged",
                        CashflowType = "PI",
                        CouponType = "Formula",
                        CouponFormula = "eff_wac",
                        SubordinationOrder = 50,
                        FirstPayDate = FirstPayDate,
                        PayFrequency = 12,
                        PayDay = FirstPayDate.Day
                    }
                },
                ExchangeShares = new List<ExchangeShareDto>
                {
                    new()
                    {
                        ExchangeTranche = "A1",
                        Shares = new List<ExShareDto>
                        {
                            new() { TrancheName = "A1A", ShareAmount = A1ABalance },
                            new() { TrancheName = "A1B", ShareAmount = A1BBalance }
                        }
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
                        new()
                        {
                            Type = "INTEREST",
                            Structure = Seq("A1A", "A1B")
                        },
                        new()
                        {
                            Type = "PRINCIPAL", Source = "scheduled",
                            Default = Seq("A1A", "A1B")
                        },
                        new()
                        {
                            Type = "PRINCIPAL", Source = "unscheduled",
                            Default = Seq("A1A", "A1B")
                        },
                        new()
                        {
                            Type = "PRINCIPAL", Source = "recovery",
                            Default = Seq("A1A", "A1B")
                        },
                        new()
                        {
                            Type = "WRITEDOWN",
                            // Junior-first: A1B absorbs losses before A1A.
                            Structure = Seq("A1B", "A1A")
                        }
                    }
                }
            }
        };
    }

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

    /// <summary>
    /// Deterministic amortizing pool with two loss periods, sized so the seniors
    /// (which make up 100% of the pool) pay down to zero and the junior senior is
    /// written down. GroupNum "1" matches the auto-generated DealStructures.
    /// </summary>
    private static List<PeriodCashflowDto> BuildCollateral()
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        const double wac = 6.0; // annual %
        var cumDefault = 0.0;
        var cumLoss = 0.0;

        for (var i = 0; i < 24 && balance > 1.0; i++)
        {
            var date = FirstPayDate.AddMonths(i);
            var interest = balance * wac / 100 / 12;

            // Losses in periods 4 and 5 to exercise the writedown pass-through.
            var defaulted = i is 3 or 4 ? balance * 0.02 : 0.0;
            var recovery = defaulted * 0.4;
            var loss = defaulted - recovery;

            // Amortize the performing balance; final period pays the remainder so
            // the pool (and therefore the tranches) fully retires.
            var performing = balance - defaulted;
            var scheduled = i == 23 ? performing : performing * 0.06;
            var unscheduled = i == 23 ? 0.0 : performing * 0.01;

            cumDefault += defaulted;
            cumLoss += loss;
            var endBalance = balance - scheduled - unscheduled - defaulted;

            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1,
                CashflowDate = date,
                GroupNum = "1",
                BeginBalance = balance,
                Balance = endBalance,
                ScheduledPrincipal = scheduled,
                UnscheduledPrincipal = unscheduled,
                Interest = interest,
                NetInterest = interest,
                DefaultedPrincipal = defaulted,
                RecoveryPrincipal = recovery,
                CollateralLoss = loss,
                Wac = wac,
                CumDefaultedPrincipal = cumDefault,
                CumCollateralLoss = cumLoss
            });

            balance = endBalance;
        }

        return periods;
    }
}
