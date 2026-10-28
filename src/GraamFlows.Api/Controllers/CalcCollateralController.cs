using System.Diagnostics;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Api.Validation;
using GraamFlows.AssetCashflowEngine;
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

            // Severity in dollars: the stated loss becomes each period's severity fraction, which
            // needs that period's defaulted principal across the pool first. Defaults do not depend
            // on severity, so a first run at zero severity finds them.
            var severityResolution = new SeverityResolutionDto();
            if (AssumptionsFactory.IsLossAmount(request.Assumptions.SeverityType))
            {
                var stated = assumps.Assumptions.Severity;
                var zero = new ConstVector(anchorAbsT, 0.0);
                var firstPass = CfCore.GenerateAssetCashflows(assets, request.ProjectionDate, null,
                    a => WithSeverity(assumpFunc(a), zero), rateProvider,
                    collectionCutoff: CollectionCutoff(request),
                    interestAccrualStart: request.InterestAccrualStartDate,
                    interestReserve: request.InterestReserve);
                var (severity, resolution) = LossAmountSeverity(
                    stated, firstPass.PeriodCashflows, request.ProjectionDate, anchorAbsT);
                severityResolution = resolution;
                var lossAssumpFunc = assumpFunc;
                assumpFunc = a => WithSeverity(lossAssumpFunc(a), severity);
            }

            // Generate cashflows
            var collateralCashflows = CfCore.GenerateAssetCashflows(
                assets,
                request.ProjectionDate,
                null, // No redemption date function
                assumpFunc,
                rateProvider,
                collectionCutoff: CollectionCutoff(request),
                interestAccrualStart: request.InterestAccrualStartDate,
                interestReserve: request.InterestReserve
            );

            // Convert to response
            var response = ConvertToResponse(collateralCashflows, assets);

            // Say what the run priced its floating indices on. Present on every successful
            // response, not only the degraded ones: "the index was assumed" has to be readable
            // from the answer, and a consumer cannot read a field that is only sometimes there.
            response.MarketRateResolution = marketRates.Describe();
            response.PrepaymentResolution = DescribePrepayment(
                AssumptionsFactory.ParsePrepaymentType(request.Assumptions.PrepaymentType), assets,
                request.ProjectionDate);
            response.DelinquencyResolution = new DelinquencyResolutionDto
            {
                DelinquencyType = AssumptionsFactory.ParseDelinquencyType(request.Assumptions.DelinquencyType).ToString()
            };
            response.SeverityResolution = severityResolution;
            response.ScheduledPrincipalAssetIds = assets
                .Where(a => a.ScheduledPrincipal is > 0)
                .Select(a => a.AssetId ?? a.AssetName)
                .ToList();

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
    ///     The prepayment convention the run applied; under CPY which assets it could not hold to their
    ///     call protection (they stated no end date, so they prepaid from the first period), and under
    ///     PSA which assets it could not age (no origination date, so their ramp started at age 1).
    /// </summary>
    internal static PrepaymentResolutionDto DescribePrepayment(PrepaymentTypeEnum type, IList<IAsset> assets,
        DateTime projectionDate)
    {
        var resolution = new PrepaymentResolutionDto { PrepaymentType = type.ToString() };
        if (type == PrepaymentTypeEnum.PSA)
        {
            var startTime = DateUtil.CalcAbsT(projectionDate);
            var unaged = assets.Where(a => !PsaRamp.HasOriginalDate(a, startTime)).ToList();
            resolution.AssetsAgedFromOriginalDate = assets.Count - unaged.Count;
            resolution.AssetsWithoutOriginalDate = unaged.Count;
            resolution.AssetIdsWithoutOriginalDate = unaged
                .Take(PrepaymentResolutionDto.MaxListedAssets)
                .Select(a => a.AssetId ?? a.AssetName)
                .ToList();
            return resolution;
        }
        if (type != PrepaymentTypeEnum.CPY)
            return resolution;
        var without = assets.Where(a => CallProtection.OpenDate(a) == null).ToList();
        resolution.AssetsWithCallProtectionDates = assets.Count - without.Count;
        resolution.AssetsWithoutCallProtectionDates = without.Count;
        resolution.AssetIdsWithoutCallProtectionDates = without
            .Take(PrepaymentResolutionDto.MaxListedAssets)
            .Select(a => a.AssetId ?? a.AssetName)
            .ToList();
        return resolution;
    }

    /// <summary>
    ///     The per-period severity (percent) that books <paramref name="statedLoss" /> dollars each
    ///     period against that period's pool defaulted principal: <c>sev_t = L_t / D_t</c>, the same
    ///     for every asset, so each asset's loss is pro rata to what it defaulted. A loss above
    ///     <c>D_t</c> is capped at it, and the excess - all of <c>L_t</c> in a period with no
    ///     defaults - is reported, not carried forward: carrying it would invent a loss timing the
    ///     caller never stated.
    /// </summary>
    internal static (IAnchorableVector Severity, SeverityResolutionDto Resolution) LossAmountSeverity(
        IAnchorableVector statedLoss, IEnumerable<PeriodCashflows> firstPass, DateTime projectionDate, int anchorAbsT)
    {
        var defaulted = new Dictionary<int, double>();
        foreach (var cf in firstPass)
        {
            var row = DateUtil.CalcAbsT(cf.CashflowDate) - anchorAbsT;
            defaulted[row] = defaulted.GetValueOrDefault(row) + cf.DefaultedPrincipal;
        }

        var rows = defaulted.Count == 0 ? 0 : defaulted.Keys.Max() + 1;
        var severityPct = new double[rows];
        var resolution = new SeverityResolutionDto { SeverityType = "LossAmount", LossRequested = 0, LossBooked = 0 };
        for (var row = 0; row < rows; row++)
        {
            var loss = statedLoss.ValueAt(row, anchorAbsT + row);
            var defaults = defaulted.GetValueOrDefault(row);
            var booked = Math.Min(loss, defaults);
            severityPct[row] = defaults > 0 ? booked / defaults * 100.0 : 0.0;
            resolution.LossRequested += loss;
            resolution.LossBooked += booked;
            if (loss - booked > 0.005)
                resolution.UnbookedByPeriod.Add(new UnbookedLossDto
                {
                    // Numbered from 1, as the response's cashflow periods are.
                    Period = row + 1,
                    CashflowDate = projectionDate.AddMonths(row),
                    Amount = loss - booked,
                });
        }

        return (new ArrayVector(anchorAbsT, severityPct), resolution);
    }

    /// <summary>The same assumptions with <paramref name="severity" /> in place of their own.</summary>
    private static IAssetAssumptions WithSeverity(IAssetAssumptions aa, IAnchorableVector severity) =>
        new AssetAssumptions(
            aa.PrepaymentType, aa.Prepayment,
            aa.DefaultType, aa.DefaultRate, severity,
            aa.DelinqRateType, aa.DelinqRate, aa.DelinqAdvPctPrin, aa.DelinqAdvPctInt,
            aa.ForbearanceRecoveryPrepay, aa.ForbearanceRecoveryDefault, aa.ForbearanceRecoveryMaturity)
        {
            RecoveryLag = aa.RecoveryLag,
        };

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

    /// <summary>
    ///     A row date's collection cutoff: N business days before it on the request's US holiday
    ///     calendar. Null (the row date itself) when the request states no cutoff.
    /// </summary>
    private static Func<DateTime, DateTime>? CollectionCutoff(CalcCollateralRequest request)
    {
        if (request.CollectionCutoffBusinessDays is not { } n || n <= 0)
            return null;
        var calendar = GraamFlows.Util.Calender.CalendarFactory.GetUSCalendar(request.HolidayCalendar ?? "Settlement");
        return date =>
        {
            var d = date.Date;
            for (var k = 0; k < n;)
            {
                d = d.AddDays(-1);
                if (calendar.IsBusinessDay(d)) k++;
            }

            return d;
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
            AccrualBasis = AccrualBasisParser.Parse(dto.DayCount),
            PaymentFrequency = dto.PaymentFrequency ?? 12,
            NextPaymentDate = dto.NextPaymentDate,
            MaturityDate = dto.MaturityDate,
            RemainingTermAsOfDate = dto.RemainingTermAsOfDate,
            LockoutEndDate = dto.LockoutEndDate,
            DefeasanceEndDate = dto.DefeasanceEndDate,
            YieldMaintenanceEndDate = dto.YieldMaintenanceEndDate,
            PrepaymentPremiumEndDate = dto.PrepaymentPremiumEndDate,
            IsIO = dto.IsIO,
            IOTerm = dto.IOTerm,
            BalloonAmortizationTerm = dto.BalloonAmortizationTerm,
            ScheduledPayments = dto.ScheduledPayments,
            ScheduledPrincipal = dto.ScheduledPrincipal,
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