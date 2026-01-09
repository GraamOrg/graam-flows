using System.Diagnostics;
using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Util.Calender.DayCounters;
using GraamFlows.Util.TermStructure;
using Microsoft.AspNetCore.Mvc;
using CashflowCalculator = GraamFlows.Util.Finance.Cashflow;

namespace GraamFlows.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PricingController : ControllerBase
{
    private readonly ILogger<PricingController> _logger;

    public PricingController(ILogger<PricingController> logger)
    {
        _logger = logger;
    }

    [HttpPost]
    public ActionResult<PricingResponse> Stats([FromBody] PricingRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Pricing: {CashflowCount} cashflows, input type {InputType}, value {InputValue}, balance {Balance:N0}",
            request.Cashflows.Count, request.Params.InputType, request.Params.InputValue, request.Params.Balance);

        try
        {
            // 1. Build cashflow stream from DTOs
            var cashflowStream = BuildCashflowStream(request.Cashflows, request.Params);

            // 2. Build term structure if rates provided
            ITermStructure? curve = null;
            IMarketRates marketRates;

            if (request.Rates != null && request.Rates.Count > 0)
            {
                curve = BuildTermStructure(request.Rates, cashflowStream.SettleDate, cashflowStream.DayCounter,
                    cashflowStream.Compounding, cashflowStream.Frequency);
                marketRates = new MarketRates
                {
                    ParCurve = curve,
                    ImpliedSpotCurve = curve
                };
            }
            else
            {
                // Create empty market rates - only yield calculations will work
                marketRates = new MarketRates();
            }

            // 3. Create Cashflow calculator
            var cf = new CashflowCalculator(cashflowStream, marketRates);

            // 4. Calculate based on input type
            double price;
            double? yield = null;
            double? spread = null;
            double? dm = null;
            double? duration = null;

            var inputType = request.Params.InputType.ToLower();

            switch (inputType)
            {
                case "price":
                    price = request.Params.InputValue;
                    yield = cf.YieldFromPrice(price);
                    if (curve != null)
                    {
                        spread = cf.SpreadFromPrice(CurveType.InterpolatedYieldCurve, price);
                        dm = CalculateDm(cf, cashflowStream, price);
                        duration = cf.ModifiedDuration(CurveType.InterpolatedYieldCurve, price, spread.Value, 1.0);
                    }
                    break;

                case "yield":
                    yield = request.Params.InputValue;
                    price = cf.PriceFromYield(yield.Value);
                    if (curve != null)
                    {
                        spread = cf.SpreadFromPrice(CurveType.InterpolatedYieldCurve, price);
                        dm = CalculateDm(cf, cashflowStream, price);
                        duration = cf.ModifiedDuration(CurveType.InterpolatedYieldCurve, price, spread.Value, 1.0);
                    }
                    break;

                case "spread":
                    if (curve == null)
                        return BadRequest(new { error = "Rates must be provided when input_type is 'spread'" });

                    spread = request.Params.InputValue;
                    price = cf.PriceFromSpread(CurveType.InterpolatedYieldCurve, spread.Value);
                    yield = cf.YieldFromPrice(price);
                    dm = CalculateDm(cf, cashflowStream, price);
                    duration = cf.ModifiedDuration(CurveType.InterpolatedYieldCurve, price, spread.Value, 1.0);
                    break;

                default:
                    return BadRequest(new { error = $"Unknown input_type: {inputType}. Must be 'price', 'yield', or 'spread'" });
            }

            // 5. Always calculate these
            var wal = cf.WeightedAverageLife();
            var accrued = cf.AccruedAmount();
            var dirtyPrice = price + accrued;

            stopwatch.Stop();
            _logger.LogInformation("Pricing completed: price {Price:F4}, yield {Yield:F4}, WAL {Wal:F2}, elapsed {ElapsedMs}ms",
                price, yield, wal, stopwatch.ElapsedMilliseconds);

            return Ok(new PricingResponse
            {
                Price = price,
                Yield = yield,
                Spread = spread,
                Dm = dm,
                ModifiedDuration = duration,
                Wal = wal,
                AccruedInterest = accrued,
                DirtyPrice = dirtyPrice
            });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Pricing failed after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            return BadRequest(new { error = ex.Message, stackTrace = ex.StackTrace });
        }
    }

    private static double? CalculateDm(CashflowCalculator cf, ICashflowStream cashflowStream, double price)
    {
        // DM requires index values on cashflows
        if (!cashflowStream.Cashflows.Any(c => c.IndexValue > 0))
            return null;

        return cf.SpreadFromPrice(CurveType.DiscountMargin, price);
    }

    private static ICashflowStream BuildCashflowStream(List<CashflowEntryDto> cashflows, PricingParamsDto parms)
    {
        var dayCounter = GetDayCounter(parms.DayCount);
        var compounding = GetCompounding(parms.Compounding);
        var frequency = GetFrequency(parms.Compounding);

        var cfList = new List<ICashflow>();
        double prevBalance = parms.Balance;

        foreach (var dto in cashflows.OrderBy(c => c.Date))
        {
            cfList.Add(new CashflowImpl
            {
                CashflowDate = dto.Date,
                Interest = dto.Interest,
                Principal = dto.Principal,
                Balance = dto.Balance,
                PrevBalance = prevBalance,
                Cashflow = dto.Interest + dto.Principal,
                IndexValue = dto.IndexValue ?? 0
            });
            prevBalance = dto.Balance;
        }

        return new CashflowStreamImpl
        {
            Cashflows = cfList,
            SettleDate = parms.SettleDate,
            Balance = parms.Balance,
            DayCounter = dayCounter,
            Compounding = compounding,
            Frequency = frequency,
            StartAccrualPeriod = parms.StartAccrualPeriod ?? DateTime.MinValue,
            PayDelay = parms.PayDelay,
            IsIo = false
        };
    }

    private static ITermStructure BuildTermStructure(List<double[]> rates, DateTime settleDate,
        IDayCounter dayCounter, CompoundingTypeEnum compounding, FrequencyTypeEnum frequency)
    {
        var interestRates = rates
            .OrderBy(r => r[0])
            .Select(r => new InterestRate(r[0], r[1] / 100.0, dayCounter, compounding, frequency))
            .Cast<IInterestRate>()
            .ToList();

        return new TermStructure(settleDate, interestRates);
    }

    private static IDayCounter GetDayCounter(string dayCount)
    {
        return dayCount.ToLower().Replace("/", "").Replace(" ", "") switch
        {
            "actual360" or "act360" => new Actual360(),
            "actual365" or "act365" => new Actual365(),
            "30360" or "thirty360" => new Thirty360Us(),
            "30360us" => new Thirty360Us(),
            "30360e" or "30e360" => new Thirty360E(),
            "actualactual" or "actact" or "actualactualisda" => new ActualActualISDA(),
            _ => new Actual360()
        };
    }

    private static CompoundingTypeEnum GetCompounding(string compounding)
    {
        return compounding.ToLower() switch
        {
            "simple" => CompoundingTypeEnum.Simple,
            "continuous" => CompoundingTypeEnum.Continuous,
            "compounded" or "semiannual" or "annual" or "quarterly" or "monthly" => CompoundingTypeEnum.Compounded,
            _ => CompoundingTypeEnum.Compounded
        };
    }

    private static FrequencyTypeEnum GetFrequency(string compounding)
    {
        return compounding.ToLower() switch
        {
            "annual" or "annually" => FrequencyTypeEnum.Annual,
            "semiannual" or "semiannually" => FrequencyTypeEnum.Semiannual,
            "quarterly" => FrequencyTypeEnum.Quarterly,
            "monthly" => FrequencyTypeEnum.Monthly,
            _ => FrequencyTypeEnum.Semiannual
        };
    }
}
