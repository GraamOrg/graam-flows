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
