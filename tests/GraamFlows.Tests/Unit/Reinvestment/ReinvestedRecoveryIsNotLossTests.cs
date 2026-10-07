using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Reinvestment;

/// <summary>
/// Recovery the reinvestment loop SPENDS is not a loss (graam-harmony#5596).
///
/// `CfCore.BuildReinvestment` subtracts the recovery cash it spends on new collateral from
/// `RecoveryPrincipal` — right for CASH, since spent money must not also be distributed. But
/// both loss formulas (`BaseStructure.WritedownAmt` and the `CollateralCashflows` stats) read
/// `DefaultedPrincipal − RecoveryPrincipal`, so every reinvested recovery came back as a loss.
/// Measured on a live CLO: writedowns 2.7x the economic loss, enough to write down rated notes
/// the reference model never touches.
///
/// The posted pool here defaults 2,000 a month and recovers 1,400 of it (30% severity), and the
/// window reinvests recoveries — so before the fix, those 1,400s were written off as losses too.
/// </summary>
public class ReinvestedRecoveryIsNotLossTests
{
    private static readonly DateTime Proj = new(2026, 6, 1);
    private const double Default = 2_000.0, Recovery = 1_400.0; // 30% severity

    private static WaterfallRequest Request(bool reinvest)
    {
        var deal = new DealDto
        {
            DealName = "REINVESTED_RECOVERY_TEST",
            WaterfallType = "Sequential",
            Tranches = new List<TrancheDto>
            {
                new()
                {
                    TrancheName = "A", OriginalBalance = 1_000_000, TrancheType = "Offered",
                    CashflowType = "PI", CouponType = "Fixed", FixedCoupon = 4.0,
                    SubordinationOrder = 1, FirstPayDate = Proj.AddMonths(1)
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
                    new() { Type = "INTEREST", Structure = Seq("A") },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A") },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A") },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A") },
                    new() { Type = "WRITEDOWN", Structure = Seq("A") }
                }
            }
        };
        if (reinvest)
            deal.Reinvestment = new ReinvestmentDto
            {
                ReinvestStartDate = Proj,
                ReinvestEndDate = Proj.AddMonths(24),
                Target = 1_000_000,
                // Explicit: the engine defaults this to FALSE (`?? false`), and with recoveries
                // ineligible nothing is redirected and every assertion here passes vacuously —
                // which is exactly what the first draft of this file did. Harmony always sends it
                // true, so production takes this path.
                ReinvestRecoveries = true,
                Templates = new List<ReinvestTemplateDto>
                {
                    new()
                    {
                        AllocationPct = 100, Price = 100.0,
                        AmortizationType = GraamFlows.Objects.TypeEnum.AmortizationType.Bullet,
                        InterestRateType = GraamFlows.Objects.TypeEnum.InterestRateType.FRM,
                        CouponRate = 5.0, TermMonths = 60
                    }
                }
            };

        var pool = new List<PeriodCashflowDto>();
        for (var p = 0; p < 60; p++)
        {
            var begin = 1_000_000.0 - 12_000.0 * p;
            pool.Add(new PeriodCashflowDto
            {
                CashflowDate = Proj.AddMonths(p), GroupNum = "1",
                BeginBalance = begin, Balance = begin - 12_000.0,
                ScheduledPrincipal = 10_000.0, UnscheduledPrincipal = 0,
                DefaultedPrincipal = Default, RecoveryPrincipal = Recovery,
                CollateralLoss = Default - Recovery,
                Interest = begin * 0.05 / 12.0
            });
        }

        // Cohorts project on zero assumptions here on purpose: every default and every
        // recovery in the run then comes from the POSTED pool, so the economic loss is known
        // exactly (60 x 600 = 36,000) and cannot be blurred by cohort behaviour.
        return new WaterfallRequest
        {
            Deal = deal, CollateralCashflows = pool, ProjectionDate = Proj,
            IncludeCollateralCashflows = true
        };
    }

    private static PayableStructureDto Seq(params string[] t) => new() { Type = "SEQ", Tranches = t.ToList() };

    private static WaterfallResponse Run(WaterfallRequest request)
    {
        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);
        var action = controller.Execute(request);
        if (action.Result is BadRequestObjectResult bad)
            throw new Xunit.Sdk.XunitException($"Waterfall 400: {bad.Value}");
        return action.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<WaterfallResponse>().Subject;
    }

    private static double Writedown(WaterfallResponse r) =>
        r.TrancheCashflows.Values.SelectMany(c => c).Sum(c => c.Writedown);

    private const double EconomicLoss = 60 * (Default - Recovery); // 36,000

    [Fact]
    public void A_reinvesting_run_writes_down_the_economic_loss_not_the_reinvested_recoveries()
    {
        // Before the fix this was ~120,000: 2,000 of default per month with the 1,400 recovery
        // spent on collateral during the 24-month window and so counted as lost.
        Writedown(Run(Request(reinvest: true))).Should().BeApproximately(EconomicLoss, 1.0);
    }

    [Fact]
    public void Reinvesting_does_not_change_the_loss_only_where_the_recovered_cash_goes()
    {
        Writedown(Run(Request(reinvest: true)))
            .Should().BeApproximately(Writedown(Run(Request(reinvest: false))), 1.0);
    }

    [Fact]
    public void The_returned_stream_carries_the_spent_recovery_and_the_loss_identity_holds()
    {
        var rows = Run(Request(reinvest: true)).CollateralCashflows!;

        rows.Sum(r => r.ReinvestedRecoveryPrincipal).Should().BeGreaterThan(0,
            "the window reinvests recoveries, so some must be recorded as spent");
        foreach (var r in rows)
            r.DefaultedPrincipal.Should().BeApproximately(
                r.CollateralLoss + r.RecoveryPrincipal + r.ReinvestedRecoveryPrincipal, 0.01,
                $"defaulted = loss + recovery distributed + recovery reinvested on {r.CashflowDate:d}");
    }

    [Fact]
    public void A_run_that_does_not_reinvest_records_no_spent_recovery()
    {
        Run(Request(reinvest: false)).CollateralCashflows!
            .Sum(r => r.ReinvestedRecoveryPrincipal).Should().Be(0);
    }
}
