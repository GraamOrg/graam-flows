using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// End-to-end reachability of the recovery-lag assumption (graam-harmony #3449)
/// through the API: a deal-level <c>recoveryLag</c> on the request must thread
/// AssumptionsDto → CalcCollateralController → AssetAssumptions → CfCore →
/// Amortizer and shift the recovery curve forward by that many months.
/// </summary>
public class AmortizerRecoveryLagApiTests
{
    private static AssetDto MakeAssetDto()
    {
        return new AssetDto
        {
            AssetName = "loan_A",
            AssetId = "loan_A",
            InterestRateType = "FRM",
            OriginalDate = new DateTime(2024, 1, 1),
            OriginalBalance = 1_000_000,
            CurrentBalance = 1_000_000,
            OriginalInterestRate = 6.0,
            CurrentInterestRate = 6.0,
            OriginalAmortizationTerm = 360,
            ServiceFee = 0.0,
            GroupNum = "1",
            IsIO = false,
        };
    }

    private static List<PeriodCashflowDto> Run(int recoveryLag)
    {
        var request = new CalcCollateralRequest
        {
            Assets = new List<AssetDto> { MakeAssetDto() },
            ProjectionDate = new DateTime(2024, 6, 1),
            Assumptions = new AssumptionsDto
            {
                Cpr = 0.0,
                Cdr = 1.0,          // 1% monthly default (MDR)
                Severity = 40.0,
                PrepaymentType = "SMM",
                DefaultType = "MDR",
                RecoveryLag = recoveryLag,
            },
        };

        var controller = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance);
        var result = controller.Calculate(request);
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<CalcCollateralResponse>().Subject;
        return response.Cashflows.OrderBy(cf => cf.Period).ToList();
    }

    [Fact]
    public void DealLevelRecoveryLag_ShiftsRecovery_ThroughTheApi()
    {
        const int lag = 2;
        var noLag = Run(0);
        var lagged = Run(lag);

        noLag.Count.Should().BeGreaterThan(lag + 6);

        // No recovery arrives within the lag window, though defaults occur.
        for (var i = 0; i < lag; i++)
        {
            lagged[i].DefaultedPrincipal.Should().BeGreaterThan(0);
            lagged[i].RecoveryPrincipal.Should().BeApproximately(0, 0.01,
                $"period {i}: recovery is delayed by the {lag}-month lag");
        }

        // Recovery at period p (no lag) reappears at p + lag (lagged run).
        //
        // Defaults are untouched only while the default can still liquidate inside
        // the remaining term: a default at p liquidates at p + lag, and the last
        // index is Count - 1, so the first suppressed period is Count - lag
        // (graam-harmony #4481 §2 — the standard books no default it cannot
        // liquidate, rather than booking the loss and dropping the recovery).
        var firstSuppressedPeriod = noLag.Count - lag;

        for (var i = 0; i < firstSuppressedPeriod; i++)
        {
            lagged[i].DefaultedPrincipal.Should().BeApproximately(noLag[i].DefaultedPrincipal, 1.0,
                $"period {i}: defaults unaffected by recovery lag while liquidation fits in term");
            lagged[i + lag].RecoveryPrincipal.Should().BeApproximately(noLag[i].RecoveryPrincipal, 1.0,
                $"period {i}: recovery shifted forward {lag} months");
        }

        for (var i = firstSuppressedPeriod; i < noLag.Count; i++)
            lagged[i].DefaultedPrincipal.Should().BeApproximately(0.0, 1e-9,
                $"period {i}: no default is booked once it could not liquidate within the term");
    }
}
