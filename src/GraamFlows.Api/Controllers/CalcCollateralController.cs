using System.Diagnostics;
using GraamFlows.Api.Models;
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

        try
        {
            // Convert DTOs to IAsset objects
            var assets = request.Assets.Select(ConvertToAsset).ToList();

            // Create assumptions
            var anchorAbsT = DateUtil.CalcAbsT(request.ProjectionDate);
            var assumps = CreateAssumptions(request.ProjectionDate, anchorAbsT, request.Assumptions);

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
            var rateProvider = BuildRateProvider(request.MarketRates, request.ProjectionDate);

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

            var prepayType = ParsePrepaymentType(dto.PrepaymentType);
            var defaultType = ParseDefaultType(dto.DefaultType);
            var delinqType = prepayType == PrepaymentTypeEnum.ABS
                ? DelinqRateTypeEnum.PctOrigBal
                : DelinqRateTypeEnum.PctCurrBal;

            var assetAssumps = new AssetAssumptions(prepayType, vpr,
                defaultType, cdr, sev,
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
        if (string.Equals(dto.PrepaymentType?.Trim(), "ABS", StringComparison.OrdinalIgnoreCase))
        {
            return DealLevelAssumptions.CreateAbsAssumptions(
                projectionDate, anchorAbsT,
                dto.Cpr, dto.Cdr, dto.Severity, dto.Delinquency, 0, dto.Wam);
        }

        var scalarPrepayType = ParsePrepaymentType(dto.PrepaymentType);
        var scalarDefaultType = ParseDefaultType(dto.DefaultType);

        // Direct-monthly hazards (SMM/MDR) can't flow through the CPR/CDR
        // CreateConstAssumptions helper without being de-annualized, so build
        // the AssetAssumptions explicitly when either mode is requested.
        if (scalarPrepayType == PrepaymentTypeEnum.SMM ||
            scalarDefaultType == DefaultTypeEnum.MDR ||
            scalarDefaultType == DefaultTypeEnum.ORIGMDR)
        {
            var assetAssumps = new AssetAssumptions(
                scalarPrepayType, new ConstVector(anchorAbsT, dto.Cpr),
                scalarDefaultType, new ConstVector(anchorAbsT, dto.Cdr),
                new ConstVector(anchorAbsT, dto.Severity),
                DelinqRateTypeEnum.PctCurrBal, new ConstVector(anchorAbsT, dto.Delinquency),
                new ConstVector(anchorAbsT, dto.Advancing), new ConstVector(anchorAbsT, dto.Advancing));
            return new DealLevelAssumptions(projectionDate, assetAssumps);
        }

        return DealLevelAssumptions.CreateConstAssumptions(
            projectionDate, anchorAbsT,
            dto.Cpr, dto.Cdr, dto.Severity, dto.Delinquency, dto.Advancing);
    }

    /// <summary>
    /// Build the ARM/hybrid reset rate provider (graam-flows#37). When the request
    /// supplies <paramref name="marketRates"/>, resets project off a forward curve
    /// per index (month-offset keyed, interpolated); otherwise fall back to the
    /// legacy flat 5% so fixed-rate and curve-less requests are unchanged.
    /// </summary>
    private static IRateProvider BuildRateProvider(
        Dictionary<string, List<double[]>>? marketRates, DateTime projectionDate)
    {
        if (marketRates == null || marketRates.Count == 0)
            return new ConstantRateProvider(5.0);

        var curves = new Dictionary<MarketDataInstEnum, List<double[]>>();
        foreach (var (instName, points) in marketRates)
        {
            if (points == null || points.Count == 0)
                continue;
            if (Enum.TryParse<MarketDataInstEnum>(instName, ignoreCase: true, out var inst))
                curves[inst] = points;
        }

        if (curves.Count == 0)
            return new ConstantRateProvider(5.0);

        return new CurveRateProvider(projectionDate, curves);
    }

    // The Trim() here is paired with the one in AssumptionValidation: the validator
    // accepts " SMM " as SMM, so this must resolve it to SMM too. Trimming in only one
    // of the two would be worse than trimming in neither — a padded string would pass
    // validation and then be silently modelled as CPR (graam-harmony #4476).
    private static PrepaymentTypeEnum ParsePrepaymentType(string? prepaymentType)
    {
        var value = prepaymentType?.Trim();
        if (string.Equals(value, "ABS", StringComparison.OrdinalIgnoreCase))
            return PrepaymentTypeEnum.ABS;
        if (string.Equals(value, "SMM", StringComparison.OrdinalIgnoreCase))
            return PrepaymentTypeEnum.SMM;
        return PrepaymentTypeEnum.CPR;
    }

    /// <summary>See <see cref="ParsePrepaymentType"/> for why the trim is paired.</summary>
    private static DefaultTypeEnum ParseDefaultType(string? defaultType)
    {
        var value = defaultType?.Trim();
        if (string.Equals(value, "MDR", StringComparison.OrdinalIgnoreCase))
            return DefaultTypeEnum.MDR;
        if (string.Equals(value, "ORIGMDR", StringComparison.OrdinalIgnoreCase))
            return DefaultTypeEnum.ORIGMDR;
        return DefaultTypeEnum.CDR;
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
                LiquidationPipelineBalance = cf.LiquidationPipelineBalance,
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