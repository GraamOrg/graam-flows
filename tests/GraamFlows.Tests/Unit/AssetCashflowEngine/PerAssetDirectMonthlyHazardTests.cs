using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// Per-asset direct-monthly-hazard reachability (harmony #1226, follow-up to
/// graam-flows#13). A loan-level model feeds PER-LOAN curves via the per-asset
/// path: deal-level <c>assumptions = { prepaymentType:"SMM", defaultType:"MDR" }</c>
/// PLUS per-asset overrides <c>assetAssumptions = { assetId: { cdrVector, cprVector, ... } }</c>.
///
/// The hazard: <see cref="GraamFlows.Domain.CfCore"/> resolves
/// PrepaymentType/DefaultType from each asset's <c>IAssetAssumptions</c>. If the
/// per-asset construction in <see cref="CalcCollateralController"/> did NOT
/// propagate the deal-level SMM/MDR mode onto every per-asset
/// <c>IAssetAssumptions</c>, CfCore would silently fall back to CPR/CDR and
/// DE-ANNUALIZE the per-asset vectors — scaling a 1% monthly hazard down to
/// ~0.084%/mo (the ~837 figure), i.e. ~11x too small.
///
/// These tests drive the controller end-to-end with the per-asset path and
/// assert the period-0 cashflow reflects DIRECT monthly hazards, not
/// de-annualized ones. They pin the inheritance contract in
/// <c>CalcCollateralController.BuildAssetAssumptions</c> (which threads
/// <c>dealLevel.PrepaymentType</c> / <c>dealLevel.DefaultType</c> through).
/// </summary>
public class PerAssetDirectMonthlyHazardTests
{
    private const double Tolerance = 1.0;

    private static AssetDto MakeAssetDto(string id)
    {
        return new AssetDto
        {
            AssetName = id,
            AssetId = id,
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

    private static CalcCollateralResponse Run(CalcCollateralRequest request)
    {
        var controller = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance);
        var result = controller.Calculate(request);
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<CalcCollateralResponse>().Subject;
    }

    /// <summary>
    /// Per-asset MDR vector via the per-asset path. Deal-level
    /// <c>defaultType=MDR</c> must propagate onto each per-asset
    /// IAssetAssumptions so the per-asset cdrVector of 1.0 (= 1% monthly) is a
    /// DIRECT hazard: period-0 defaultedPrincipal = 0.01 × 1,000,000 = 10,000
    /// per asset — NOT the de-annualized ~837 you'd get if the mode were lost
    /// and the engine de-annualized the 1% to a monthly fraction.
    /// </summary>
    [Fact]
    public void PerAsset_MdrVector_Inherits_DealLevel_Mode_DirectMonthly()
    {
        var request = new CalcCollateralRequest
        {
            Assets = new List<AssetDto> { MakeAssetDto("loan_A"), MakeAssetDto("loan_B") },
            ProjectionDate = new DateTime(2024, 6, 1),
            Assumptions = new AssumptionsDto
            {
                Cpr = 0.0,
                Cdr = 0.0,
                Severity = 100.0,
                PrepaymentType = "SMM",
                DefaultType = "MDR",
            },
            AssetAssumptions = new Dictionary<string, AssetAssumptionDto>
            {
                ["loan_A"] = new AssetAssumptionDto { CdrVector = new[] { 1.0 } },
                ["loan_B"] = new AssetAssumptionDto { CdrVector = new[] { 1.0 } },
            },
        };

        var response = Run(request);

        // Period-0 cashflows for the two assets (each in group "1").
        var period0 = response.Cashflows.Where(cf => cf.Period == 1).ToList();
        period0.Should().HaveCountGreaterThanOrEqualTo(1);

        // Sum across the group's period-0 cashflows: 10,000 per asset × 2.
        var totalP0Default = period0.Sum(cf => cf.DefaultedPrincipal);
        totalP0Default.Should().BeApproximately(20_000.00, 2.0 * Tolerance,
            "per-asset cdrVector=1.0 with deal-level defaultType=MDR is a direct 1% " +
            "monthly default on each $1MM asset (10,000 each); if the deal-level MDR " +
            "mode were NOT inherited per-asset, CfCore would de-annualize 1% to ~837");

        // And at 100% severity the loss equals the defaulted principal.
        var totalP0Loss = period0.Sum(cf => cf.CollateralLoss);
        totalP0Loss.Should().BeApproximately(20_000.00, 2.0 * Tolerance,
            "100% severity → full defaulted principal is lost");
    }

    /// <summary>
    /// Per-asset SMM prepay via the per-asset path. Deal-level
    /// <c>prepaymentType=SMM</c> must propagate so a per-asset cprVector of 1.0
    /// (= 1% monthly) prepays 0.01 × (balance − scheduledPrincipal) ≈ 9,990
    /// per asset — NOT the de-annualized ~837.
    /// </summary>
    [Fact]
    public void PerAsset_SmmVector_Inherits_DealLevel_Mode_DirectMonthly()
    {
        var request = new CalcCollateralRequest
        {
            Assets = new List<AssetDto> { MakeAssetDto("loan_A"), MakeAssetDto("loan_B") },
            ProjectionDate = new DateTime(2024, 6, 1),
            Assumptions = new AssumptionsDto
            {
                Cpr = 0.0,
                Cdr = 0.0,
                Severity = 100.0,
                PrepaymentType = "SMM",
                DefaultType = "MDR",
            },
            AssetAssumptions = new Dictionary<string, AssetAssumptionDto>
            {
                ["loan_A"] = new AssetAssumptionDto { CprVector = new[] { 1.0 } },
                ["loan_B"] = new AssetAssumptionDto { CprVector = new[] { 1.0 } },
            },
        };

        var response = Run(request);

        var period0 = response.Cashflows.Where(cf => cf.Period == 1).ToList();
        period0.Should().HaveCountGreaterThanOrEqualTo(1);

        // 1% direct monthly prepay of (balance − scheduledPrincipal). Scheduled
        // principal on a $1MM / 6% / 360mo FRM is tiny (~833 first period), so
        // unscheduled ≈ 0.01 × (1,000,000 − ~833) ≈ 9,991 per asset.
        var totalP0Unscheduled = period0.Sum(cf => cf.UnscheduledPrincipal);
        totalP0Unscheduled.Should().BeApproximately(19_983.0, 20.0,
            "per-asset cprVector=1.0 with deal-level prepaymentType=SMM is a direct 1% " +
            "monthly prepay (~9,990 each); if the deal-level SMM mode were NOT inherited " +
            "per-asset, CfCore would de-annualize 1% to ~837");
    }
}
