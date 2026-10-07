using System.Diagnostics;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Api.Validation;
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

        // Reject out-of-range assumptions HERE, where they enter the system
        // (graam-harmony #4476). Before this guard, cpr=1000 de-annualized to NaN,
        // the amortizer's Math.Clamp(smm, 0, 1) failed to clamp it (NaN compares
        // false against both bounds), and this endpoint returned 200 OK with NaN in
        // every row — the mistake only surfaced one service call later, as a
        // non-finite-cashflow rejection from /api/waterfall that named neither the
        // field nor the value the user got wrong.
        var validationError = AssumptionValidation.Validate(request);
        if (validationError != null)
        {
            _logger.LogWarning("CalcCollateral rejected: {ValidationError}", validationError);
            return BadRequest(new { error = validationError });
        }

        // Resolve the caller's market-rate curves HERE, where the request is still a request
        // (graam-flows#102). A `marketRates` that resolved to nothing used to fall through to the
        // same flat 5% as a request that sent no curves at all, and answered 200 — so a run
        // priced on an index the caller never gave was indistinguishable from a correct one.
        var marketRates = MarketRateResolver.Resolve(request.MarketRates);
        if (marketRates.Error != null)
        {
            _logger.LogWarning("CalcCollateral rejected: {ValidationError}", marketRates.Error);
            return BadRequest(new { error = marketRates.Error });
        }

        try
        {
            // Convert DTOs to IAsset objects
            var assets = request.Assets.Select(ConvertToAsset).ToList();

            // Create assumptions
            var anchorAbsT = DateUtil.CalcAbsT(request.ProjectionDate);
            var assumps = AssumptionsFactory.CreateAssumptions(request.ProjectionDate, anchorAbsT, request.Assumptions);

            // Deal-level recovery lag (graam-harmony #3449). Every CreateAssumptions
            // path wraps a concrete AssetAssumptions in .Assumptions, so set the lag
            // there — it applies to any asset without a per-asset override.
            if (assumps.Assumptions is AssetAssumptions dealAssumps)
                dealAssumps.RecoveryLag = request.Assumptions.RecoveryLag;

            // Per-asset override (graam-flows#5). When request.AssetAssumptions
            // is non-empty, replace the assumption mill with a function that
            // resolves per asset: dictionary entry → per-asset IAssetAssumptions
            // (with any null field falling through to deal-level); absent
            // entry → deal-level. Engine layer is per-asset capable as of this
            // PR — see CfCore.GenerateAssetCashflows.
            Func<IAsset, IAssetAssumptions> assumpFunc;
            if (request.AssetAssumptions is { Count: > 0 } perAsset)
            {
                // The uniform constructor of DealLevelAssumptions sets
                // .Assumptions directly. CreateAssumptions above always uses
                // that constructor, so direct field access avoids the
                // null-dereference hazard of GetAssumptionsForAsset(null).
                var dealLevel = assumps.Assumptions;
                var perAssetResolved = new Dictionary<string, IAssetAssumptions>(perAsset.Count);
                foreach (var (assetId, dto) in perAsset)
                    perAssetResolved[assetId] = BuildAssetAssumptions(anchorAbsT, dealLevel, request.Assumptions, dto);
                _logger.LogInformation("CalcCollateral: per-asset assumptions for {Count} of {Total} assets",
                    perAssetResolved.Count, assets.Count);
                assumpFunc = asset => perAssetResolved.TryGetValue(asset.AssetId ?? asset.AssetName, out var aa)
                    ? aa
                    : dealLevel;
            }
            else
            {
                assumpFunc = assumps.GetAssumptionsForAsset;
            }

            // Create a simple rate provider (for ARMs)
            // ARM/hybrid resets project off a forward curve when the request
            // supplies one (graam-flows#37); otherwise fall back to the legacy
            // flat rate. Fixed-rate loans ignore the provider entirely.
            var rateProvider = BuildRateProvider(marketRates, request.ProjectionDate);

            // Generate cashflows
            var collateralCashflows = CfCore.GenerateAssetCashflows(
                assets,
                request.ProjectionDate,
                null, // No redemption date function
                assumpFunc,
                rateProvider
            );

            // Convert to response
            var response = ConvertToResponse(collateralCashflows, assets);

            // Say what the run priced its floating indices on. Present on every successful
            // response, not only the degraded ones: "the index was assumed" has to be readable
            // from the answer, and a consumer cannot read a field that is only sometimes there.
            response.MarketRateResolution = marketRates.Describe();

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

    /// <summary>
    /// Build the ARM/hybrid reset rate provider (graam-flows#37). When the request
    /// supplies curves, resets project off a forward curve per index (month-offset
    /// keyed, interpolated); a request that supplies NO curves falls back to the
    /// legacy flat 5% so fixed-rate and curve-less requests are unchanged.
    ///
    /// A request that supplied curves and landed none never reaches here — it is
    /// rejected in <see cref="Calculate"/> (graam-flows#102), because answering it
    /// with the flat rate means answering with a number the caller never gave.
    /// </summary>
    private static IRateProvider BuildRateProvider(
        MarketRateResolution marketRates, DateTime projectionDate)
    {
        if (marketRates.Error != null)
            throw new InvalidOperationException(marketRates.Error);

        if (marketRates.UsesAssumedRate)
            return new ConstantRateProvider(MarketRateResolver.AssumedFlatRate);

        return new CurveRateProvider(projectionDate, marketRates.Curves);
    }

    /// <summary>
    /// Build an <see cref="IAssetAssumptions"/> for one asset by merging the
    /// per-asset DTO over the deal-level fallback. Any field the per-asset DTO
    /// leaves null inherits from the deal-level <paramref name="dealLevel"/>.
    /// PrepaymentType is always inherited from the deal-level (it's a mode,
    /// not a per-asset toggle — see CfCore.GenerateAssetCashflows).
    /// </summary>
    private static IAssetAssumptions BuildAssetAssumptions(
        int anchorAbsT,
        IAssetAssumptions dealLevel,
        AssumptionsDto dealDto,
        AssetAssumptionDto perAsset)
    {
        // Resolve each rate as: per-asset vector > per-asset scalar >
        // deal-level vector (already in dealLevel.* if dealDto carried it) >
        // deal-level scalar (also in dealLevel.*). For the fallthrough cases
        // we just reuse the deal-level IAnchorableVector directly — same
        // object the engine would have built without an override.
        IAnchorableVector ResolveRate(
            double[]? overrideVector,
            double? overrideScalar,
            IAnchorableVector dealLevelVector,
            double divisor = 1.0)
        {
            if (overrideVector is { Length: > 0 })
                return new ArrayVector(anchorAbsT, overrideVector);
            if (overrideScalar.HasValue)
                return new ConstVector(anchorAbsT, overrideScalar.Value);
            return dealLevelVector;
        }

        var vpr = ResolveRate(perAsset.CprVector, perAsset.Cpr, dealLevel.Prepayment);
        var cdr = ResolveRate(perAsset.CdrVector, perAsset.Cdr, dealLevel.DefaultRate);
        var sev = ResolveRate(perAsset.SeverityVector, perAsset.Severity, dealLevel.Severity);
        var delinq = ResolveRate(perAsset.DelinquencyVector, perAsset.Delinquency, dealLevel.DelinqRate);
        var adv = ResolveRate(perAsset.AdvancingVector, perAsset.Advancing, dealLevel.DelinqAdvPctInt);

        // Inherit PrepaymentType / DefaultType / DelinqRateType from deal-level —
        // these are modes, not per-asset overrides. ForbearanceRecovery* also
        // pass through unchanged (no per-asset override field today).
        return new AssetAssumptions(
            dealLevel.PrepaymentType, vpr,
            dealLevel.DefaultType, cdr, sev,
            dealLevel.DelinqRateType, delinq, adv, adv,
            dealLevel.ForbearanceRecoveryPrepay, dealLevel.ForbearanceRecoveryDefault, dealLevel.ForbearanceRecoveryMaturity)
        {
            // Recovery lag (graam-harmony #3449): per-asset override falls through
            // to the deal-level value.
            RecoveryLag = perAsset.RecoveryLag ?? dealLevel.RecoveryLag,
        };
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

        response.Cashflows = CollateralCashflowMapper.ToDtos(periodCashflows);

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