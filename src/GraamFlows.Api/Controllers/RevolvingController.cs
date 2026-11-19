using System.Diagnostics;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Api.Validation;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Revolving;
using Microsoft.AspNetCore.Mvc;

namespace GraamFlows.Api.Controllers;

/// <summary>
///     Revolving trusts: N series over one pool, forecast from a statement date or replayed from
///     printed monthly figures. It has no fallback onto the waterfall route.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class RevolvingController : ControllerBase
{
    private readonly ILogger<RevolvingController> _logger;

    public RevolvingController(ILogger<RevolvingController> logger)
    {
        _logger = logger;
    }

    [HttpPost]
    public ActionResult<RevolvingResponse> Execute([FromBody] RevolvingRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Revolving: program {Program}, {SeriesCount} series, {Periods} periods",
            request.Program.Name, request.Series.Count, request.Periods);

        try
        {
            var marketRates = MarketRateResolver.Resolve(request.MarketRates);
            if (marketRates.Error != null)
                throw new RevolvingConfigException(marketRates.Error);

            if (OwnerTrustMapper.IsOwnerTrust(request))
                return Ok(OwnerTrust(request, marketRates, stopwatch));

            var run = RevolvingMapper.ToRun(request);
            var result = RevolvingEngine.Run(run, RatesFor(run, marketRates));

            var response = RevolvingMapper.ToResponse(result);
            response.MarketRateResolution = marketRates.UsesAssumedRate ? null : marketRates.Describe();
            response.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return Ok(response);
        }
        catch (ArgumentException refused)
        {
            _logger.LogWarning("Revolving refused: {Reason}", refused.Message);
            return BadRequest(new { error = refused.Message });
        }
    }

    /// <summary>
    ///     An installment pool alone, with no series: its monthly roll and its present value at each
    ///     discount rate. A revolving run needs at least one series and a whole program, and no series
    ///     allocates on an installment pool yet, so the pool runs here until one does.
    /// </summary>
    [HttpPost("pool")]
    public ActionResult<RevolvingPoolResponse> Pool([FromBody] RevolvingPoolRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Revolving pool: {Model}, {Periods} periods", request.Pool?.Model, request.Periods);

        try
        {
            var result = InstallmentPoolLedger.Run(InstallmentPoolMapper.ToRun(request));
            var response = InstallmentPoolMapper.ToResponse(result);
            response.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return Ok(response);
        }
        catch (ArgumentException refused)
        {
            _logger.LogWarning("Revolving pool refused: {Reason}", refused.Message);
            return BadRequest(new { error = refused.Message });
        }
    }

    /// <summary>
    ///     An amortizing owner trust over an installment pool: <c>program.ownerTrust</c>. A program
    ///     without it never comes here, so every card program runs exactly as before.
    /// </summary>
    private static RevolvingResponse OwnerTrust(RevolvingRequest request, MarketRateResolution marketRates,
        Stopwatch stopwatch)
    {
        var run = OwnerTrustMapper.ToRun(request);
        foreach (var index in OwnerTrustMapper.FloatingIndices(run))
            if (!marketRates.Curves.ContainsKey(index))
                throw new RevolvingConfigException(
                    $"A floating class pays on {index} and the request supplied no curve for it.");

        var rates = new CurveRateProvider(run.Pool.StatementDate.AddDays(1), marketRates.Curves);
        var result = OwnerTrustEngine.Run(run, rates);
        var response = OwnerTrustMapper.ToResponse(run, result);
        response.MarketRateResolution = marketRates.UsesAssumedRate ? null : marketRates.Describe();
        response.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return response;
    }

    /// <summary>
    ///     A floating class needs a curve the caller supplied. The engine assumes no rate. Offset 0 is
    ///     the run's first month, as on the other endpoints: the month after the statement date.
    /// </summary>
    private static IRateProvider RatesFor(RevolvingRun run, MarketRateResolution marketRates)
    {
        foreach (var index in RevolvingMapper.FloatingIndices(run))
            if (!marketRates.Curves.ContainsKey(index))
                throw new RevolvingConfigException(
                    $"A floating class pays on {index} and the request supplied no curve for it.");

        return new CurveRateProvider(run.StatementDate.AddDays(1), marketRates.Curves);
    }
}
