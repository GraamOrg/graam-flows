using System.Text.Json;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

public class RevolvingRouteTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static RevolvingRequest Request(Action<RevolvingRequest>? change = null)
    {
        var request = new RevolvingRequest
        {
            Program = new RevolvingProgramDto
            {
                Name = "Example card trust",
                RecoveryBucket = "FINANCE",
                PoolMeasure = "PERIOD_START",
                ActionLag = 0,
                PrincipalFreeze = "FROZEN_PERCENT",
                FinanceSteps = new List<RevolvingStepDto>
                {
                    new() { Verb = "PAY_INTEREST", Class = "A" },
                    new() { Verb = "COVER_DEFAULTS", Class = "A" },
                    new() { Verb = "RELEASE_TRANSFEROR" }
                },
                PrincipalSteps = new List<RevolvingStepDto>
                {
                    new() { Verb = "DEPOSIT_PFA", Phases = new List<string> { "ACCUMULATING" } },
                    new() { Verb = "PAY_CLASS", Class = "A", Phases = new List<string> { "AMORTIZING", "EARLY_AMORTIZATION" } },
                    new() { Verb = "SHARE_PRINCIPAL" }
                },
                Triggers = new List<RevolvingTriggerDto>
                {
                    new()
                    {
                        Name = "YIELD_BELOW_BASE_RATE",
                        Metric = "PORTFOLIO_YIELD",
                        Window = 3,
                        Comparison = "BELOW",
                        AgainstMetric = "BASE_RATE",
                        Action = "EARLY_AMORTIZATION"
                    }
                }
            },
            StatementDate = new DateTime(2026, 7, 31),
            Periods = 12,
            PoolBalance = 1_000_000.0,
            Drivers = new RevolvingDriversDto
            {
                PaymentRate = new RevolvingDriverDto { Values = new[] { 40.0 } },
                HoldPool = true,
                Yield = new RevolvingDriverDto { Values = new[] { 24.0 }, Annual = true },
                ChargeOffRate = new RevolvingDriverDto { Values = new[] { 3.0 }, Annual = true }
            },
            Series = new List<RevolvingSeriesDto>
            {
                new()
                {
                    Name = "S",
                    Group = "1",
                    Classes = new List<RevolvingClassDto> { new() { Key = "A", Balance = 400_000.0, Coupon = 5.0 } },
                    WritedownOrder = new List<string> { "A" },
                    FundedClasses = new List<string> { "A" },
                    AccumulationStart = new DateTime(2026, 11, 1),
                    ExpectedDate = new DateTime(2027, 3, 15),
                    ControlledAmount = new[] { 100_000.0 }
                }
            }
        };
        change?.Invoke(request);
        return request;
    }

    private static ActionResult<RevolvingResponse> Post(RevolvingRequest request)
    {
        return new RevolvingController(NullLogger<RevolvingController>.Instance).Execute(request);
    }

    private static RevolvingResponse Ok(RevolvingRequest request)
    {
        var result = Post(request);
        return (RevolvingResponse)((OkObjectResult)result.Result!).Value!;
    }

    private static string Error(RevolvingRequest request)
    {
        var bad = Post(request).Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        return JsonSerializer.Serialize(bad.Value);
    }

    [Fact]
    public void AForecastReturnsPeriodsSeriesAndTrancheRows()
    {
        var response = Ok(Request());

        response.Periods.Should().HaveCount(12);
        var series = response.Series["S"];
        series.Periods.Should().HaveCount(12);
        series.PhasePath.Select(m => m.Phase).Should().Equal("REVOLVING", "ACCUMULATING", "AMORTIZING");
        series.PhasePath[1].DistributionDate.Should().Be(new DateTime(2026, 12, 15));
        series.Periods[0].Triggers[0].Status.Should().Be("NOT_EVALUATED");
        series.Periods[3].Triggers[0].Status.Should().Be("PASS");
        series.Periods[3].Triggers[0].Headroom.Should().BeGreaterThan(0.0);
        response.TrancheCashflows["S/A"][0].Factor.Should().Be(1.0);
    }

    [Fact]
    public void AnAnnualDriverIsSimpleOverTheDaysOfEachMonth()
    {
        var periods = Ok(Request()).Periods;

        // August has 31 days and September 30.
        periods[0].FinanceCollections.Should().BeApproximately(1_000_000.0 * 0.24 * 31 / 365, 1e-6);
        periods[1].ChargeOffs.Should().BeApproximately(periods[0].EndingBalance * 0.03 * 30 / 365, 1e-6);
    }

    [Fact]
    public void PercentsConvertAtTheBoundary()
    {
        var periods = Ok(Request()).Periods;

        periods[0].PrincipalCollections.Should().BeApproximately(400_000.0, 1e-6);
    }

    [Fact]
    public void ADriverIsANumberAVectorOrAnAnnualObject()
    {
        var parsed = JsonSerializer.Deserialize<RevolvingDriversDto>(
            """{"paymentRate": 50.9, "yield": [2.0, 2.1], "chargeOffRate": {"annual": 6.0}, "recoveryRate": {"monthly": [0.1]}}""",
            Options)!;

        parsed.PaymentRate!.Values.Should().Equal(50.9);
        parsed.PaymentRate.Annual.Should().BeFalse();
        parsed.Yield!.Values.Should().Equal(2.0, 2.1);
        parsed.ChargeOffRate!.Values.Should().Equal(6.0);
        parsed.ChargeOffRate.Annual.Should().BeTrue();
        parsed.RecoveryRate!.Annual.Should().BeFalse();
    }

    [Fact]
    public void AnUnknownDriverKeyIsAnError()
    {
        var parse = () => JsonSerializer.Deserialize<RevolvingDriversDto>("""{"yield": {"quarterly": 2.0}}""", Options);

        parse.Should().Throw<JsonException>().WithMessage("*quarterly*");
    }

    [Fact]
    public void AProgramWithNoPrincipalFreezeIsRefusedByName()
    {
        Error(Request(r => r.Program.PrincipalFreeze = null))
            .Should().Contain("Example card trust").And.Contain("principalFreeze");
    }

    [Fact]
    public void AnUnknownPrincipalFreezeIsRefusedByName()
    {
        Error(Request(r => r.Program.PrincipalFreeze = "FROZEN_POOL")).Should().Contain("FROZEN_POOL");
    }

    [Fact]
    public void TheFreezeChangesPrincipalThroughTheRoute()
    {
        void Shrinking(RevolvingRequest r)
        {
            r.Drivers!.HoldPool = false;
            r.Drivers.NewReceivablesRate = new RevolvingDriverDto { Values = new[] { 35.0 } };
        }

        var percent = Ok(Request(Shrinking)).Series["S"].Periods;
        var numerator = Ok(Request(r =>
        {
            Shrinking(r);
            r.Program.PrincipalFreeze = "FROZEN_NUMERATOR";
        })).Series["S"].Periods;

        // Revolving through October, frozen from November, and only the numerator follows the pool.
        percent[4].PrincipalPct.Should().Be(percent[3].PrincipalPct);
        numerator[3].PrincipalPct.Should().Be(percent[3].PrincipalPct);
        numerator[4].PrincipalPct.Should().BeGreaterThan(numerator[3].PrincipalPct);
    }

    [Fact]
    public void GroupReallocationRunsThroughTheRoute()
    {
        var response = Ok(Request(r =>
            r.Program.GroupSteps = new List<RevolvingStepDto> { new() { Verb = "REALLOCATE_GROUP_FINANCE" } }));

        response.Series["S"].Periods[0].ReallocatedFinance.Should().BeApproximately(
            response.Series["S"].Periods[0].FinanceAllocation, 1e-6);
    }

    [Fact]
    public void WithNoGroupStepsTheRouteReportsNoReallocation()
    {
        Ok(Request()).Series["S"].Periods[0].ReallocatedFinance.Should().BeNull();
    }

    [Fact]
    public void AnUnknownGroupVerbIsRefusedByName()
    {
        Error(Request(r => r.Program.GroupSteps = new List<RevolvingStepDto> { new() { Verb = "POOL_EVERYTHING" } }))
            .Should().Contain("POOL_EVERYTHING");
    }

    [Fact]
    public void AnUnknownVerbIsRefusedByName()
    {
        Error(Request(r => r.Program.FinanceSteps.Add(new RevolvingStepDto { Verb = "PAY_SERVICER" })))
            .Should().Contain("PAY_SERVICER");
    }

    [Fact]
    public void AMissingVerbIsRefused()
    {
        Error(Request(r => r.Program.PrincipalSteps.Add(new RevolvingStepDto()))).Should().Contain("Unknown waterfall verb");
    }

    [Theory]
    [InlineData("metric", "PAYMENT_DROP")]
    [InlineData("comparison", "NEAR")]
    [InlineData("action", "NOTIFY")]
    [InlineData("againstMetric", "COUPON")]
    public void AnUnknownTriggerIsRefusedByName(string field, string value)
    {
        var request = Request(r =>
        {
            var test = r.Program.Triggers![0];
            switch (field)
            {
                case "metric":
                    test.Metric = value;
                    break;
                case "comparison":
                    test.Comparison = value;
                    break;
                case "action":
                    test.Action = value;
                    break;
                default:
                    test.AgainstMetric = value;
                    break;
            }
        });

        Error(request).Should().Contain(value).And.Contain("YIELD_BELOW_BASE_RATE");
    }

    [Fact]
    public void AnUnknownPhaseOnAStepIsRefusedByName()
    {
        Error(Request(r => r.Program.PrincipalSteps[0].Phases = new List<string> { "DORMANT" })).Should().Contain("DORMANT");
    }

    [Fact]
    public void AProgramWithNoRecoveryBucketIsRefusedByName()
    {
        Error(Request(r => r.Program.RecoveryBucket = null))
            .Should().Contain("Example card trust").And.Contain("recoveryBucket");
    }

    [Fact]
    public void AFloatingClassWithNoCurveIsRefused()
    {
        Error(Request(r => r.Series[0].Classes[0].Index = "Sofr30Avg")).Should().Contain("supplied no curve");
    }

    [Fact]
    public void AFloatingClassOnACurveTheRequestDidNotSupplyIsRefused()
    {
        var error = Error(Request(r =>
        {
            r.Series[0].Classes[0].Index = "Sofr30Avg";
            r.MarketRates = new Dictionary<string, List<double[]>> { ["Sofr90Avg"] = new() { new[] { 0.0, 4.0 } } };
        }));

        error.Should().Contain("Sofr30Avg").And.Contain("supplied no curve");
    }

    [Fact]
    public void AFloatingClassReadsItsCurveByMonth()
    {
        var response = Ok(Request(r =>
        {
            r.Series[0].Classes[0].Index = "Sofr30Avg";
            r.Series[0].Classes[0].Margin = 1.0;
            r.MarketRates = new Dictionary<string, List<double[]>>
            {
                ["Sofr30Avg"] = new() { new[] { 0.0, 4.0 }, new[] { 12.0, 16.0 } }
            };
        }));

        var rows = response.TrancheCashflows["S/A"];
        rows[1].Coupon.Should().BeApproximately(rows[0].Coupon + 1.0, 1e-9);
        response.MarketRateResolution!.IndexSource.Should().Be(MarketRateSource.Supplied);
    }

    [Fact]
    public void AFixedRunClaimsNoIndex()
    {
        Ok(Request()).MarketRateResolution.Should().BeNull();
    }

    [Fact]
    public void AnUnknownDayCountIsRefusedByName()
    {
        Error(Request(r => r.Series[0].Classes[0].DayCount = "Actual/Actual")).Should().Contain("Actual/Actual");
    }

    private static PricingResponse Price(IEnumerable<CashflowEntryDto> cashflows)
    {
        var pricing = new PricingController(NullLogger<PricingController>.Instance).Stats(new PricingRequest
        {
            Cashflows = cashflows.ToList(),
            Params = new PricingParamsDto
            {
                InputType = "price",
                InputValue = 100.0,
                SettleDate = new DateTime(2026, 8, 15),
                Balance = 400_000.0,
                DayCount = "30/360",
                Compounding = "Monthly"
            }
        });
        return (PricingResponse)((OkObjectResult)pricing.Result!).Value!;
    }

    [Fact]
    public void AClassPricesOnTheExistingSolverLikeTheBulletItIs()
    {
        // A rich yield, so the shrinking floating % leaves no default uncovered near the end.
        var rich = Request(r => r.Drivers!.Yield = new RevolvingDriverDto { Values = new[] { 60.0 }, Annual = true });
        var rows = Ok(rich).TrancheCashflows["S/A"]
            .Where(r => r.Interest > 0 || r.ScheduledPrincipal > 0)
            .ToList();
        rows.Should().HaveCount(7);

        // The class pays seven months of 5%, and its whole balance with the last.
        var stats = Price(rows.Select(r => new CashflowEntryDto
        {
            Date = r.CashflowDate,
            Interest = r.Interest,
            Principal = r.ScheduledPrincipal,
            Balance = r.Balance
        }));
        var bullet = Price(rows.Select((r, i) => new CashflowEntryDto
        {
            Date = r.CashflowDate,
            Interest = 400_000.0 * 0.05 / 12.0,
            Principal = i == rows.Count - 1 ? 400_000.0 : 0.0,
            Balance = i == rows.Count - 1 ? 0.0 : 400_000.0
        }));

        stats.Yield.Should().BeApproximately(bullet.Yield!.Value, 1e-9);
        stats.Yield.Should().BeApproximately(5.0, 0.05);
        stats.Wal.Should().BeApproximately(bullet.Wal!.Value, 1e-9);
    }
}
