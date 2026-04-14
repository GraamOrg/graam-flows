using System.Diagnostics;
using GraamFlows.Api.Models;
using GraamFlows.Assumptions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Microsoft.AspNetCore.Mvc;

namespace GraamFlows.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CalcCollateralController : ControllerBase
{
    private readonly ILogger<CalcCollateralController> _logger;

    public CalcCollateralController(ILogger<CalcCollateralController> logger)
    {
        _logger = logger;
    }

    [HttpPost]
    public ActionResult<CalcCollateralResponse> Calculate([FromBody] CalcCollateralRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        var totalBalance = request.Assets.Sum(a => a.CurrentBalance);
        _logger.LogInformation("CalcCollateral: {AssetCount} assets, total balance {TotalBalance:N0}, projection date {ProjectionDate:yyyy-MM-dd}",
            request.Assets.Count, totalBalance, request.ProjectionDate);

        try
        {
            // Convert DTOs to IAsset objects
            var assets = request.Assets.Select(ConvertToAsset).ToList();

            // Create assumptions
            var anchorAbsT = DateUtil.CalcAbsT(request.ProjectionDate);
            var assumps = CreateAssumptions(request.ProjectionDate, anchorAbsT, request.Assumptions);

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

            stopwatch.Stop();
            _logger.LogInformation("CalcCollateral completed: {CashflowCount} cashflows, {TotalPeriods} periods, elapsed {ElapsedMs}ms",
                response.Cashflows.Count, response.Summary.TotalPeriods, stopwatch.ElapsedMilliseconds);

            return Ok(response);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "CalcCollateral failed after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            return BadRequest(new { error = ex.Message, stackTrace = ex.StackTrace });
        }
    }

    private static DealLevelAssumptions CreateAssumptions(DateTime projectionDate, int anchorAbsT, AssumptionsDto dto)
    {
        // Priority 1: Per-period arrays (e.g., cdrVector: [10.4, 9.8, 8.2, ...])
        var hasArrays = dto.CprVector != null || dto.CdrVector != null ||
                        dto.SeverityVector != null || dto.DelinquencyVector != null ||
                        dto.AdvancingVector != null;

        if (hasArrays)
        {
            var vpr = dto.CprVector != null
                ? new ArrayVector(anchorAbsT, dto.CprVector)
                : (IAnchorableVector)new ConstVector(anchorAbsT, dto.Cpr);
            var cdr = dto.CdrVector != null
                ? new ArrayVector(anchorAbsT, dto.CdrVector)
                : (IAnchorableVector)new ConstVector(anchorAbsT, dto.Cdr);
            var sev = dto.SeverityVector != null
                ? new ArrayVector(anchorAbsT, dto.SeverityVector)
                : (IAnchorableVector)new ConstVector(anchorAbsT, dto.Severity);
            var delinq = dto.DelinquencyVector != null
                ? new ArrayVector(anchorAbsT, dto.DelinquencyVector)
                : (IAnchorableVector)new ConstVector(anchorAbsT, dto.Delinquency);
            var adv = dto.AdvancingVector != null
                ? new ArrayVector(anchorAbsT, dto.AdvancingVector)
                : (IAnchorableVector)new ConstVector(anchorAbsT, dto.Advancing);

            var prepayType = string.Equals(dto.PrepaymentType, "ABS", StringComparison.OrdinalIgnoreCase)
                ? PrepaymentTypeEnum.ABS
                : PrepaymentTypeEnum.CPR;
            var delinqType = prepayType == PrepaymentTypeEnum.ABS
                ? DelinqRateTypeEnum.PctOrigBal
                : DelinqRateTypeEnum.PctCurrBal;

            var assetAssumps = new AssetAssumptions(prepayType, vpr,
                DefaultTypeEnum.CDR, cdr, sev,
                delinqType, delinq, adv, adv);
            return new DealLevelAssumptions(projectionDate, assetAssumps)
            {
                WeightedAverageRemainingTerm = dto.Wam
            };
        }

        // Priority 2: PolyPaths format strings (legacy)
        var hasVectorStrs = !string.IsNullOrEmpty(dto.CprVectorStr) ||
                            !string.IsNullOrEmpty(dto.CdrVectorStr) ||
                            !string.IsNullOrEmpty(dto.SeverityVectorStr) ||
                            !string.IsNullOrEmpty(dto.DelinquencyVectorStr) ||
                            !string.IsNullOrEmpty(dto.AdvancingVectorStr);

        if (hasVectorStrs)
        {
            var vprStr = dto.CprVectorStr ?? dto.Cpr.ToString();
            var cdrStr = dto.CdrVectorStr ?? dto.Cdr.ToString();
            var sevStr = dto.SeverityVectorStr ?? dto.Severity.ToString();
            var dqStr = dto.DelinquencyVectorStr ?? dto.Delinquency.ToString();
            var advStr = dto.AdvancingVectorStr ?? dto.Advancing.ToString();

            return DealLevelAssumptions.CreateConstAssumptions(
                projectionDate, anchorAbsT, vprStr, cdrStr, sevStr, dqStr, advStr);
        }

        // Priority 3: Scalar values
        if (string.Equals(dto.PrepaymentType, "ABS", StringComparison.OrdinalIgnoreCase))
        {
            return DealLevelAssumptions.CreateAbsAssumptions(
                projectionDate, anchorAbsT,
                dto.Cpr, dto.Cdr, dto.Severity, dto.Delinquency, 0, dto.Wam);
        }

        return DealLevelAssumptions.CreateConstAssumptions(
            projectionDate, anchorAbsT,
            dto.Cpr, dto.Cdr, dto.Severity, dto.Delinquency, dto.Advancing);
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
                ForbearanceUnscheduled = cf.ForbearanceUnscheduled,
                AccumForbearance = cf.AccumForbearance,
                Wac = cf.WAC,
                Wam = cf.WAM,
                Wala = cf.WALA,
                Vpr = cf.VPR,
                Cdr = cf.CDR,
                Sev = cf.SEV,
                Dq = cf.DQ,
                CumDefaultedPrincipal = cf.CumDefaultedPrincipal,
                CumCollateralLoss = cf.CumCollateralLoss,
                UnAdvancedPrincipal = cf.UnAdvancedPrincipal,
                UnAdvancedInterest = cf.UnAdvancedInterest,
                AdvancedPrincipal = cf.AdvancedPrincipal,
                AdvancedInterest = cf.AdvancedInterest,
                Expenses = cf.Expenses
            });
        }

        // Calculate summary
        var firstCf = periodCashflows.FirstOrDefault();
        var lastCf = periodCashflows.LastOrDefault();
        var originalBalance = firstCf?.BeginBalance ?? 0;
        var totalDefaultedPrincipal = periodCashflows.Sum(cf => cf.DefaultedPrincipal);
        var totalCollateralLoss = lastCf?.CumCollateralLoss ?? 0;
        response.Summary = new CollateralSummaryDto
        {
            TotalPeriods = periodCashflows.Count,
            OriginalBalance = originalBalance,
            Wac = firstCf?.WAC ?? 0,
            Wam = firstCf?.WAM ?? 0,
            Wala = firstCf?.WALA ?? 0,
            TotalScheduledPrincipal = periodCashflows.Sum(cf => cf.ScheduledPrincipal),
            TotalUnscheduledPrincipal = periodCashflows.Sum(cf => cf.UnscheduledPrincipal),
            TotalInterest = periodCashflows.Sum(cf => cf.Interest),
            TotalDefaultedPrincipal = totalDefaultedPrincipal,
            TotalRecoveryPrincipal = periodCashflows.Sum(cf => cf.RecoveryPrincipal),
            TotalCollateralLoss = totalCollateralLoss,
            CumDefaultPct = originalBalance > 0 ? totalDefaultedPrincipal / originalBalance : 0,
            CumLossPct = originalBalance > 0 ? totalCollateralLoss / originalBalance : 0
        };

        return response;
    }
}