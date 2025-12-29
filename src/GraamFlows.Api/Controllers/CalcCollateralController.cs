using GraamFlows.Api.Models;
using GraamFlows.Assumptions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Microsoft.AspNetCore.Mvc;

namespace GraamFlows.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CalcCollateralController : ControllerBase
{
    [HttpPost]
    public ActionResult<CalcCollateralResponse> Calculate([FromBody] CalcCollateralRequest request)
    {
        try
        {
            // Convert DTOs to IAsset objects
            var assets = request.Assets.Select(ConvertToAsset).ToList();

            // Create assumptions
            var anchorAbsT = DateUtil.CalcAbsT(request.ProjectionDate);
            var assumps = DealLevelAssumptions.CreateConstAssumptions(
                request.ProjectionDate,
                anchorAbsT,
                request.Assumptions.Cpr,
                request.Assumptions.Cdr,
                request.Assumptions.Severity,
                request.Assumptions.Delinquency,
                request.Assumptions.Advancing
            );

            // Create a simple rate provider (for ARMs)
            var rateProvider = new ConstantRateProvider(5.0); // Default 5% rate for ARMs

            // Generate cashflows
            var collateralCashflows = CfCore.GenerateAssetCashflows(
                assets,
                request.ProjectionDate,
                null, // No redemption date function
                assumps.GetAssumptionsForAsset,
                rateProvider
            );

            // Convert to response
            var response = ConvertToResponse(collateralCashflows, assets);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message, stackTrace = ex.StackTrace });
        }
    }

    private static IAsset ConvertToAsset(AssetDto dto)
    {
        var asset = new Asset
        {
            AssetName = dto.AssetName,
            AssetId = dto.AssetId ?? dto.AssetName,
            InterestRateType = Enum.Parse<InterestRateType>(dto.InterestRateType),
            OriginalDate = dto.OriginalDate,
            OriginalBalance = dto.OriginalBalance,
            OriginalInterestRate = dto.OriginalInterestRate,
            CurrentInterestRate = dto.CurrentInterestRate,
            OriginalAmortizationTerm = dto.OriginalAmortizationTerm,
            CurrentBalance = dto.CurrentBalance,
            BalanceAtIssuance = dto.CurrentBalance, // Default to current balance if not specified
            ServiceFee = dto.ServiceFee,
            DebtService = dto.DebtService,
            GroupNum = dto.GroupNum,
            IsIO = dto.IsIO,
            IOTerm = dto.IOTerm,
            ForbearanceAmt = dto.ForbearanceAmt,
            StepDatesList = dto.StepDatesList,
            StepRatesList = dto.StepRatesList
        };

        // ARM-specific fields
        if (asset.InterestRateType == InterestRateType.ARM)
        {
            asset.InitialAdjustmentPeriod = dto.InitialAdjustmentPeriod;
            asset.AdjustmentPeriod = dto.AdjustmentPeriod;
            asset.InitialRate = dto.InitialRate;
            asset.IndexMargin = dto.IndexMargin;
            asset.AdjustmentCap = dto.AdjustmentCap;
            asset.LifeAdjustmentCap = dto.LifeAdjustmentCap;
            asset.LifeAdjustmentFloor = dto.LifeAdjustmentFloor;

            if (!string.IsNullOrEmpty(dto.IndexName)) asset.IndexName = Enum.Parse<MarketDataInstEnum>(dto.IndexName);
        }

        return asset;
    }

    private static CalcCollateralResponse ConvertToResponse(CollateralCashflows cashflows, IList<IAsset> assets)
    {
        var periodCashflows = cashflows.PeriodCashflows;
        var response = new CalcCollateralResponse
        {
            Cashflows = new List<PeriodCashflowDto>()
        };

        var period = 0;
        foreach (var cf in periodCashflows)
        {
            period++;
            response.Cashflows.Add(new PeriodCashflowDto
            {
                Period = period,
                CashflowDate = cf.CashflowDate,
                GroupNum = cf.GroupNum ?? "0",
                BeginBalance = cf.BeginBalance,
                Balance = cf.Balance,
                ScheduledPrincipal = cf.ScheduledPrincipal,
                UnscheduledPrincipal = cf.UnscheduledPrincipal,
                Interest = cf.Interest,
                NetInterest = cf.NetInterest,
                ServiceFee = cf.ServiceFee,
                DefaultedPrincipal = cf.DefaultedPrincipal,
                RecoveryPrincipal = cf.RecoveryPrincipal,
                CollateralLoss = cf.CollateralLoss,
                DelinqBalance = cf.DelinqBalance,
                ForbearanceRecovery = cf.ForbearanceRecovery,
                ForbearanceLiquidated = cf.ForbearanceLiquidated,
                Wac = cf.WAC,
                Wam = cf.WAM,
                Wala = cf.WALA,
                Vpr = cf.VPR,
                Cdr = cf.CDR,
                Sev = cf.SEV,
                Dq = cf.DQ,
                CumDefaultedPrincipal = cf.CumDefaultedPrincipal,
                CumCollateralLoss = cf.CumCollateralLoss
            });
        }

        // Calculate summary
        var firstCf = periodCashflows.FirstOrDefault();
        var lastCf = periodCashflows.LastOrDefault();
        response.Summary = new CollateralSummaryDto
        {
            TotalPeriods = periodCashflows.Count,
            OriginalBalance = firstCf?.BeginBalance ?? 0,
            Wac = firstCf?.WAC ?? 0,
            Wam = firstCf?.WAM ?? 0,
            Wala = firstCf?.WALA ?? 0,
            TotalScheduledPrincipal = periodCashflows.Sum(cf => cf.ScheduledPrincipal),
            TotalUnscheduledPrincipal = periodCashflows.Sum(cf => cf.UnscheduledPrincipal),
            TotalInterest = periodCashflows.Sum(cf => cf.Interest),
            TotalDefaultedPrincipal = periodCashflows.Sum(cf => cf.DefaultedPrincipal),
            TotalRecoveryPrincipal = periodCashflows.Sum(cf => cf.RecoveryPrincipal),
            TotalCollateralLoss = lastCf?.CumCollateralLoss ?? 0
        };

        return response;
    }
}