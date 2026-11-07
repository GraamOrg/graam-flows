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

    private static void Floating(RevolvingClassDto dto)
    {
        dto.Index = "Sofr30Avg";
        dto.Margin = 1.0;
        dto.DayCount = "Actual/360";
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

    private static void NoteSeries(RevolvingRequest r)
    {
        r.Program.Delinked = new RevolvingDelinkedDto { ClassOrder = new List<string> { "A" } };
        r.Program.PrincipalSteps = new List<RevolvingStepDto>
        {
            new() { Verb = "DEPOSIT_PFA" },
            new() { Verb = "RELEASE_TRANSFEROR" }
        };
    }

    [Fact]
    public void PayClassOnANoteSeriesIsANamed400NotA500()
    {
        var error = Error(Request(r =>
        {
            NoteSeries(r);
            r.Program.PrincipalSteps.Insert(0, new RevolvingStepDto { Verb = "PAY_CLASS", Class = "A" });
        }));

        error.Should().Contain("PAY_CLASS");
    }

    [Fact]
    public void ANoteSeriesWithNoLastReleaseStepIsANamed400NotA500()
    {
        var error = Error(Request(r =>
        {
            NoteSeries(r);
            r.Program.FinanceSteps.RemoveAt(r.Program.FinanceSteps.Count - 1);
        }));

        error.Should().Contain("finance steps must end");
    }

    [Fact]
    public void ARunCutOnTheResponsesClosingMatchesOneLongRun()
    {
        // A fee finance cannot pay, a test that trips and acts a month later, and a first waterfall
        // still to come, so all of it must carry across the wire.
        var months = Enumerable.Range(0, 6).Select(i => new ActualPeriodDto
        {
            BeginningBalance = 1_000_000.0,
            NewReceivables = 100_000.0,
            PrincipalCollections = 100_000.0,
            FinanceCollections = i < 3 ? 1_000.0 : 60_000.0,
            ChargeOffs = 2_000.0
        }).ToList();
        var camel = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        RevolvingRequest Stressed(Action<RevolvingRequest>? change = null) => Request(r =>
        {
            r.Program.ActionLag = 1;
            r.Program.Triggers![0].Window = 1;
            r.Program.FinanceSteps.Insert(1, new RevolvingStepDto { Verb = "PAY_FEE", Fee = "servicing" });
            r.Drivers = null;
            r.Periods = months.Count;
            r.ActualPeriods = months;
            r.Series[0].Fees = new List<RevolvingFeeDto> { new() { Name = "servicing", Rate = 12.0 } };
            r.Series[0].FirstPayDate = new DateTime(2026, 10, 15);
            r.Series[0].AccumulationStart = null;
            r.Series[0].ExpectedDate = null;
            r.Series[0].ControlledAmount = null;
            change?.Invoke(r);
        });

        var whole = Ok(Stressed());

        var state = (RevolvingSeriesStateDto?)null;
        var classes = new Dictionary<string, RevolvingClassClosingDto>();
        var statement = new DateTime(2026, 7, 31);
        for (var i = 0; i < months.Count; i++)
        {
            var (when, month, open, held) = (statement, months[i], state, classes);
            var one = Ok(Stressed(r =>
            {
                r.StatementDate = when;
                r.ActualPeriods = new List<ActualPeriodDto> { month };
                r.Periods = 1;
                r.Series[0].State = open;
                foreach (var c in r.Series[0].Classes.Where(c => held.ContainsKey(c.Key!)))
                {
                    var closing = held[c.Key!];
                    (c.Balance, c.OriginalBalance, c.WrittenDown) = (closing.Balance, closing.OriginalBalance, closing.WrittenDown);
                    (c.InterestShortfall, c.DefaultDue) = (closing.InterestShortfall, closing.DefaultDue);
                }
            }));

            var want = whole.Series["S"].Periods[i];
            var got = one.Series["S"].Periods[0];
            got.Phase.Should().Be(want.Phase, $"month {i}");
            got.FeesPaid.Should().BeApproximately(want.FeesPaid, 1e-6, $"month {i}");
            got.CarriedFinance.Should().BeApproximately(want.CarriedFinance, 1e-6, $"month {i}");
            one.TrancheCashflows["S/A"][0].Interest.Should().BeApproximately(whole.TrancheCashflows["S/A"][i].Interest, 1e-6, $"month {i}");
            one.TrancheCashflows["S/A"][0].Balance.Should().BeApproximately(whole.TrancheCashflows["S/A"][i].Balance, 1e-6, $"month {i}");
            one.Periods[0].TransferorFinance.Should().BeApproximately(whole.Periods[i].TransferorFinance, 1e-6, $"month {i}");

            var closed = one.Series["S"].Closing;
            closed.State.History!.ExcessSpread.Should().NotBeNull();
            closed.State.History.PortfolioYield.Should().NotBeNull();
            closed.State.History.BaseRate.Should().NotBeNull();
            state = JsonSerializer.Deserialize<RevolvingSeriesStateDto>(JsonSerializer.Serialize(closed.State, camel), Options);
            classes = closed.Classes;
            statement = new DateTime(statement.Year, statement.Month, 1).AddMonths(2).AddDays(-1);
        }

        whole.Series["S"].PhasePath.Select(m => m.Phase).Should().Contain("EARLY_AMORTIZATION");
        whole.Series["S"].Periods.Should().Contain(p => p.CarriedFinance > 0.0);
    }

    [Fact]
    public void AForecastReturnsPeriodsSeriesAndTrancheRows()
    {
        // A rich yield, so the shrinking floating % leaves no default uncovered near the end.
        var response = Ok(Request(r => r.Drivers!.Yield = new RevolvingDriverDto { Values = new[] { 60.0 }, Annual = true }));

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
        Error(Request(r => Floating(r.Series[0].Classes[0]))).Should().Contain("supplied no curve");
    }

    [Fact]
    public void AFloatingClassOnACurveTheRequestDidNotSupplyIsRefused()
    {
        var error = Error(Request(r =>
        {
            Floating(r.Series[0].Classes[0]);
            r.MarketRates = new Dictionary<string, List<double[]>> { ["Sofr90Avg"] = new() { new[] { 0.0, 4.0 } } };
        }));

        error.Should().Contain("Sofr30Avg").And.Contain("supplied no curve");
    }

    [Fact]
    public void AFloatingClassReadsItsCurveByMonth()
    {
        var response = Ok(Request(r =>
        {
            Floating(r.Series[0].Classes[0]);
            r.MarketRates = new Dictionary<string, List<double[]>>
            {
                ["Sofr30Avg"] = new() { new[] { 0.0, 4.0 }, new[] { 12.0, 16.0 } }
            };
        }));

        var rows = response.TrancheCashflows["S/A"];
        rows[1].Coupon.Should().BeApproximately(rows[0].Coupon + 1.0, 1e-9);
        response.MarketRateResolution!.IndexSource.Should().Be(MarketRateSource.Supplied);
    }

    private static readonly Dictionary<string, List<double[]>> SteppedCurve = new()
    {
        ["Sofr30Avg"] = new() { new[] { 0.0, 4.0 }, new[] { 1.0, 5.0 }, new[] { 2.0, 6.0 }, new[] { 3.0, 7.0 } }
    };

    [Fact]
    public void OffsetZeroOfACurveIsTheFirstMonthOfTheRun()
    {
        var rows = Ok(Request(r =>
        {
            Floating(r.Series[0].Classes[0]);
            r.MarketRates = SteppedCurve;
        })).TrancheCashflows["S/A"];

        rows.Take(5).Select(row => row.IndexValue).Should().Equal(4.0, 5.0, 6.0, 7.0, 7.0);
        rows.Take(3).Select(row => row.Coupon).Should().Equal(5.0, 6.0, 7.0);
    }

    [Fact]
    public void OffsetZeroOfACurveIsTheFirstReplayedMonthOfAReplay()
    {
        var rows = Ok(Request(r =>
        {
            Floating(r.Series[0].Classes[0]);
            r.MarketRates = SteppedCurve;
            r.ActualPeriods = new List<ActualPeriodDto> { Printed(), Printed() };
            r.Periods = 3;
        })).TrancheCashflows["S/A"];

        rows.Select(row => row.IndexValue).Should().Equal(4.0, 5.0, 6.0);
    }

    [Fact]
    public void AClassWithNoBalanceHasAFactorOfZeroNotNaN()
    {
        var response = Ok(Request(r =>
        {
            r.Program.FinanceSteps.Insert(1, new RevolvingStepDto { Verb = "PAY_INTEREST", Class = "B" });
            r.Series[0].Classes.Add(new RevolvingClassDto { Key = "B", Balance = 0.0, Coupon = 5.5 });
            r.Series[0].WritedownOrder!.Insert(0, "B");
        }));

        response.TrancheCashflows["S/B"].Should().OnlyContain(row => row.Factor == 0.0);
    }

    private static ActualPeriodDto Printed()
    {
        return new ActualPeriodDto
        {
            NewReceivables = 400_000.0,
            PrincipalCollections = 400_000.0,
            FinanceCollections = 20_000.0,
            ChargeOffs = 2_000.0
        };
    }

    private static RevolvingSeriesStateDto Past(double frozenPct)
    {
        return new RevolvingSeriesStateDto { Phase = "EARLY_AMORTIZATION", FrozenPrincipalPct = frozenPct };
    }

    private static RevolvingDriverDto Monthly(double value)
    {
        return new RevolvingDriverDto { Values = new[] { value } };
    }

    private static void Replaying(RevolvingRequest r, Action<ActualPeriodDto> month)
    {
        var printed = Printed();
        month(printed);
        r.ActualPeriods = new List<ActualPeriodDto> { Printed(), printed };
        r.Periods = 2;
    }

    public static TheoryData<Action<RevolvingRequest>, string> OutOfRange => new()
    {
        { r => r.PoolBalance = -1.0, "poolBalance" },
        { r => r.PoolBalance = double.NaN, "poolBalance" },
        { r => r.PoolBalance = double.PositiveInfinity, "poolBalance" },
        { r => r.Series[0].State = Past(-10.0), "frozenPrincipalPct" },
        { r => r.Series[0].State = Past(500.0), "frozenPrincipalPct" },
        { r => r.Series[0].State = Past(double.NaN), "frozenPrincipalPct" },
        { r => r.Drivers!.Yield = Monthly(-5.0), "yield" },
        { r => r.Drivers!.Yield = Monthly(double.NaN), "yield" },
        { r => r.Drivers!.Yield = new RevolvingDriverDto { Values = new[] { 1_200.0 }, Annual = true }, "yield" },
        { r => r.Drivers!.PaymentRate = Monthly(300.0), "paymentRate" },
        { r => r.Drivers!.ChargeOffRate = Monthly(-1.0), "chargeOffRate" },
        { r => r.Drivers!.RecoveryRate = Monthly(double.NaN), "recoveryRate" },
        { r => r.Drivers!.DilutionRate = Monthly(101.0), "dilutionRate" },
        { r => r.FundingEarningsRate = new[] { -1.0 }, "fundingEarningsRate" },
        { r => r.Series[0].Classes[0].Coupon = -1.0, "coupon" },
        { r => r.Series[0].Classes[0].Coupon = double.NaN, "coupon" },
        { r => r.Series[0].Classes[0].Coupon = 101.0, "coupon" },
        { r => r.Series[0].Classes[0].Margin = double.NaN, "margin" },
        { r => r.Series[0].Classes[0].Balance = -1.0, "balance" },
        { r => r.Series[0].Classes[0].OriginalBalance = double.NaN, "originalBalance" },
        { r => r.Series[0].Classes[0].WrittenDown = -1.0, "writtenDown" },
        { r => r.Series[0].Classes[0].WrittenDown = 400_001.0, "written down" },
        { r => r.Series[0].Classes[0].InterestShortfall = double.NaN, "interestShortfall" },
        { r => r.Series[0].Classes[0].InterestShortfall = -0.01, "interestShortfall" },
        { r => r.Series[0].ControlledAmount = new[] { -1.0 }, "controlledAmount" },
        { r => r.Series[0].RequiredReserve = -1.0, "requiredReserve" },
        { r => r.Series[0].State = new RevolvingSeriesStateDto { FundingAccount = -1.0 }, "fundingAccount" },
        { r => r.Series[0].State = new RevolvingSeriesStateDto { FundingDeficit = double.NaN }, "fundingDeficit" },
        { r => r.Series[0].State = new RevolvingSeriesStateDto { Reserve = -1.0 }, "reserve" },
        { r => r.Series[0].State = new RevolvingSeriesStateDto { AccumulationMonthsRun = -1 }, "accumulationMonthsRun" },
        { r => r.Series[0].State = new RevolvingSeriesStateDto { FrozenNumerator = -1.0 }, "frozenNumerator" },
        { r => r.Series[0].State = new RevolvingSeriesStateDto { FrozenNumerator = double.NaN }, "frozenNumerator" },
        {
            r => r.Series[0].State = new RevolvingSeriesStateDto
            {
                History = new RevolvingHistoryDto { BaseRate = new[] { double.NaN } }
            },
            "history"
        },
        { r => r.Program.RequiredTransferorPct = 101.0, "requiredTransferorPct" },
        { r => r.Program.SeriesRequiredTransferorPct = -1.0, "seriesRequiredTransferorPct" },
        { r => r.Program.Triggers![0] = LevelTest(double.NaN), "level" },
        { r => Replaying(r, m => m.NewReceivables = -1.0), "actualPeriods[1].newReceivables" },
        { r => Replaying(r, m => m.PrincipalCollections = double.NaN), "actualPeriods[1].principalCollections" },
        { r => Replaying(r, m => m.FinanceCollections = -1.0), "actualPeriods[1].financeCollections" },
        { r => Replaying(r, m => m.ChargeOffs = double.NaN), "actualPeriods[1].chargeOffs" },
        { r => Replaying(r, m => m.Recoveries = -1.0), "actualPeriods[1].recoveries" },
        { r => Replaying(r, m => m.Dilution = -1.0), "actualPeriods[1].dilution" },
        { r => Replaying(r, m => m.BeginningBalance = double.NaN), "actualPeriods[1].beginningBalance" },
        { r => Replaying(r, m => m.EndingBalance = -1.0), "actualPeriods[1].endingBalance" },
        { r => Replaying(r, m => m.Days = 0), "actualPeriods[1].days" },
        { r => Replaying(r, m => m.Days = -1), "actualPeriods[1].days" },
        { r => r.Series[0].ExpectedDate = new DateTime(2026, 10, 15), "before it starts accumulating" }
    };

    private static RevolvingTriggerDto LevelTest(double level)
    {
        return new RevolvingTriggerDto
        {
            Name = "LEVEL",
            Metric = "EXCESS_SPREAD",
            Window = 3,
            Comparison = "BELOW",
            Level = level,
            Action = "EARLY_AMORTIZATION"
        };
    }

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void AFigureOutOfRangeIsRefusedByName(Action<RevolvingRequest> change, string named)
    {
        Error(Request(change)).Should().Contain(named);
    }

    public static TheoryData<Action<RevolvingRequest>> AtTheEdge => new()
    {
        { r => r.Series[0].State = Past(0.0) },
        { r => r.Series[0].State = Past(100.0) },
        { r => r.Drivers!.PaymentRate = Monthly(100.0) },
        { r => r.Drivers!.Yield = Monthly(0.0) },
        { r => r.Drivers!.Yield = new RevolvingDriverDto { Values = new[] { 200.0 }, Annual = true } },
        { r => r.Series[0].Classes[0].Coupon = 0.0 },
        { r => r.Series[0].ExpectedDate = r.Series[0].AccumulationStart },
        { r => r.Series[0].Classes[0].WrittenDown = 400_000.0 },
        { r => r.Series[0].Classes[0].InterestShortfall = -5.8e-11 }
    };

    [Theory]
    [MemberData(nameof(AtTheEdge))]
    public void AFigureAtTheEdgeOfItsRangeRuns(Action<RevolvingRequest> change)
    {
        Ok(Request(change)).Periods.Should().NotBeEmpty();
    }

    public static TheoryData<Action<RevolvingRequest>, string> LeftOut => new()
    {
        { r => r.PoolBalance = null, "poolBalance" },
        { r => r.Series[0].Classes[0].Coupon = null, "states no coupon" },
        { r => r.Series[0].Classes[0].Balance = null, "states no balance" },
        { r => Replaying(r, m => m.NewReceivables = null), "actualPeriods[1] states no newReceivables" },
        { r => Replaying(r, m => m.PrincipalCollections = null), "actualPeriods[1] states no principalCollections" },
        { r => Replaying(r, m => m.FinanceCollections = null), "actualPeriods[1] states no financeCollections" },
        { r => Replaying(r, m => m.ChargeOffs = null), "actualPeriods[1] states no chargeOffs" },
        { r => r.Series[0].Fees = new List<RevolvingFeeDto> { new() { Name = "servicing", Rate = 2.0 } }, "no PAY_FEE step" },
        {
            r =>
            {
                r.Program.FinanceSteps.Add(new RevolvingStepDto { Verb = "PAY_FEE", Fee = "servicing" });
                r.Series[0].Fees = new List<RevolvingFeeDto> { new() { Rate = 2.0 } };
            },
            "fee with no name"
        },
        {
            r =>
            {
                r.Program.FinanceSteps.Add(new RevolvingStepDto { Verb = "PAY_FEE", Fee = "servicing" });
                r.Series[0].Fees = new List<RevolvingFeeDto> { new() { Name = "servicing" } };
            },
            "states no rate"
        },
        {
            r =>
            {
                r.Program.FinanceSteps.Add(new RevolvingStepDto { Verb = "PAY_FEE", Fee = "servicing" });
                r.Series[0].Fees = new List<RevolvingFeeDto>
                {
                    new() { Name = "servicing", Rate = 2.0 },
                    new() { Name = "servicing", Rate = 1.0 }
                };
            },
            "twice"
        },
        { r => r.Program.FinanceSteps.RemoveAll(s => s.Verb == "PAY_INTEREST"), "no PAY_INTEREST step" },
        {
            r =>
            {
                r.Series[0].Group = null;
                r.Series[0].PrincipalSharing = true;
            },
            "states no group"
        },
        {
            r =>
            {
                r.Series[0].Group = "";
                r.Series[0].ExcessSharing = true;
            },
            "states no group"
        },
        {
            r =>
            {
                r.Series[0].Group = "";
                r.Program.GroupSteps = new List<RevolvingStepDto> { new() { Verb = "REALLOCATE_GROUP_FINANCE" } };
            },
            "states no group"
        },
        {
            r =>
            {
                Floating(r.Series[0].Classes[0]);
                r.Series[0].Classes[0].Margin = null;
                r.MarketRates = SteppedCurve;
            },
            "states no margin"
        },
        {
            r =>
            {
                Floating(r.Series[0].Classes[0]);
                r.Series[0].Classes[0].DayCount = null;
                r.MarketRates = SteppedCurve;
            },
            "states no dayCount"
        }
    };

    [Theory]
    [MemberData(nameof(LeftOut))]
    public void AFigureTheRunNeedsIsRefusedByNameWhenLeftOut(Action<RevolvingRequest> change, string named)
    {
        Error(Request(change)).Should().Contain(named);
    }

    [Fact]
    public void ASeriesThatSharesNothingNeedsNoGroup()
    {
        Ok(Request(r => r.Series[0].Group = null)).Periods.Should().NotBeEmpty();
    }

    [Fact]
    public void ADefaultedDayCountStaysOnAFixedClass()
    {
        Ok(Request()).TrancheCashflows["S/A"][0].Interest.Should().BeApproximately(400_000.0 * 0.05 / 12.0, 1e-9);
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
