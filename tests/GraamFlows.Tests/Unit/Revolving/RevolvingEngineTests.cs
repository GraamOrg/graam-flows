using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Revolving;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>
///     The period loop, against closed forms and the American Express Credit Account Master Trust
///     statements for the August and September 2026 monthly periods.
/// </summary>
public class RevolvingEngineTests
{
    private const double OneCent = 0.01;

    private static SeriesResult Only(RevolvingResult result, string name)
    {
        return result.Series.Single(s => s.Name == name);
    }

    [Fact]
    public void AReplayedMonthClosesToThePrintedBalance()
    {
        var run = CardProgram.Run(CardProgram.Program(), 24_975_208_451.43,
            new[] { CardProgram.Series("2024-1", 1_000_000_000.0, 42_858_000.0, 100_001_000.0) }, null, 1,
            new DateTime(2026, 8, 31)) with
        {
            ActualPeriods = new[]
            {
                new ActualPeriod
                {
                    Beginning = 24_975_208_451.43,
                    NewReceivables = 12_498_514_556.18,
                    PrincipalCollections = 12_712_741_903.27,
                    FinanceCollections = 687_263_527.44,
                    Recoveries = 15_412_642.37,
                    ChargeOffs = 35_740_822.51,
                    Ending = 24_725_240_281.83
                }
            }
        };

        var month = CardProgram.Execute(run).Periods[0];

        month.Pool.Ending.Should().BeApproximately(24_725_240_281.83, OneCent);
        month.LedgerBreak!.Value.Should().BeApproximately(0.0, OneCent);
        month.RollBreak!.Value.Should().BeApproximately(0.0, OneCent);
        month.Dates.Days.Should().Be(30);
    }

    [Fact]
    public void ThePrintedMinimumPoolIsReported()
    {
        var run = CardProgram.Run(CardProgram.Program() with { SeriesRequiredTransferorPct = 7.0 }, 24_975_208_451.43,
            new[] { CardProgram.Series("Trust", 13_942_879_000.0, 0.0, 0.0) }, CardProgram.Drivers(), 1);

        var test = CardProgram.Execute(run).Periods[0].ReportingTests.Single();

        test.Name.Should().Be("MINIMUM_POOL");
        test.Required.Should().BeApproximately(14_918_880_530.0, 1.0);
    }

    [Fact]
    public void TheTransferorAmountIsThePoolLessTheInvestedAmounts()
    {
        var run = CardProgram.Run(CardProgram.Program(), 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, CardProgram.Drivers(), 3);

        var result = CardProgram.Execute(run);

        foreach (var month in result.Periods)
            month.TransferorAmount.Should().BeApproximately(month.Pool.Ending - 400_000.0, OneCent);
    }

    [Fact]
    public void WhileRevolvingPrincipalGoesToTheTransferorAndTheSeriesHoldsItsBalance()
    {
        var run = CardProgram.Run(CardProgram.Program(), 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, CardProgram.Drivers(), 6);

        var result = CardProgram.Execute(run);

        var series = Only(result, "S");
        series.Periods.Should().OnlyContain(p => p.Phase == SeriesPhase.Revolving);
        series.Periods.Should().OnlyContain(p => Math.Abs(p.InvestedAmount - 400_000.0) < OneCent);
        result.Periods.Should().OnlyContain(p =>
            p.TransferorPrincipal >= p.Pool.PrincipalCash - OneCent);
    }

    [Fact]
    public void PrincipalPercentEqualsFloatingWhileRevolvingAndThenDoesNotChange()
    {
        var series = CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) with
        {
            AccumulationStart = new DateTime(2027, 1, 1),
            ExpectedDate = new DateTime(2028, 6, 15),
            ControlledAmounts = new[] { 10_000.0 }
        };
        var run = CardProgram.Run(CardProgram.Program(), 1_000_000.0, new[] { series },
            CardProgram.Drivers(newReceivables: 0.41), 18);

        var periods = Only(CardProgram.Execute(run), "S").Periods;

        var revolving = periods.Where(p => p.Phase == SeriesPhase.Revolving).ToList();
        var after = periods.Where(p => p.Phase != SeriesPhase.Revolving).ToList();
        revolving.Should().HaveCount(5);
        revolving.Should().OnlyContain(p => p.PrincipalPct == p.FloatingPct);
        after.Should().HaveCountGreaterThan(5);
        after.Select(p => p.PrincipalPct).Distinct().Should().ContainSingle();

        // The value frozen is the numerator on the last revolving day over the pool that day.
        var lastDay = CardProgram.Execute(run).Periods[4].Pool.Ending;
        after[0].PrincipalPct.Should().BeApproximately(400_000.0 / lastDay * 100.0, 1e-9);
        after[^1].FloatingPct.Should().BeLessThan(after[0].PrincipalPct);
    }

    private static RevolvingSeries Seeded(RevolvingSeries series, double yieldPct, double basePct)
    {
        return series with
        {
            History = new Dictionary<TriggerMetric, IReadOnlyList<double>>
            {
                [TriggerMetric.PortfolioYield] = new[] { yieldPct, yieldPct, yieldPct },
                [TriggerMetric.BaseRate] = new[] { basePct, basePct, basePct }
            }
        };
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void ATestTrippedOnSeededHistoryActsAtTheProgramsLag(int lag, int firstEarlyMonth)
    {
        var series = Seeded(CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0), 2.0, 9.0);
        var program = CardProgram.Program(actionLag: lag, triggers: new[] { CardProgram.YieldBelowBaseRate });

        var result = Only(CardProgram.Execute(CardProgram.Run(program, 1_000_000.0, new[] { series },
            CardProgram.Drivers(), 4)), "S");

        result.Periods.First(p => p.Phase == SeriesPhase.EarlyAmortization).Period.Should().Be(firstEarlyMonth);
        result.FirstTrip!.Test.Should().Be("YIELD_BELOW_BASE_RATE");
        result.FirstTrip.Period.Should().Be(0);
        result.Periods[0].Triggers[0].Headroom.Should().BeApproximately(-7.0, 1e-9);
    }

    [Fact]
    public void AProgramWithATriggerAndNoActionLagIsRefusedByName()
    {
        var program = CardProgram.Program(actionLag: null, triggers: new[] { CardProgram.YieldBelowBaseRate });

        var refuse = () => CardProgram.Execute(CardProgram.Run(program, 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, CardProgram.Drivers(), 1));

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*Example card trust*actionLag*");
    }

    [Fact]
    public void ATestWithoutAFullWindowIsReportedAsNotEvaluatedUntilItHasOne()
    {
        var program = CardProgram.Program(triggers: new[] { CardProgram.YieldBelowBaseRate });

        var periods = Only(CardProgram.Execute(CardProgram.Run(program, 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, CardProgram.Drivers(), 5)), "S").Periods;

        periods.Take(3).Should().OnlyContain(p => !p.Triggers[0].Evaluated);
        periods.Skip(3).Should().OnlyContain(p => p.Triggers[0].Evaluated);
    }

    [Fact]
    public void Series2023Dash3MakesItsSingleDepositWithSharedPrincipal()
    {
        // American Express Series 2023-3, August 2026 monthly period, paid on the September 2026
        // distribution date. Its accumulation was delayed to the last month, so the controlled
        // amount covers the whole of Class A and Class B.
        const double trustInvested = 13_486_889_000.00;
        const double subjectInvested = 971_433_000.00;
        var subject = CardProgram.Series("2023-3", 850_000_000.00, 36_430_000.00, 85_003_000.00,
            5.23, 5.52, 6.99) with
        {
            AccumulationStart = new DateTime(2026, 8, 1),
            ExpectedDate = new DateTime(2026, 9, 15),
            ControlledAmounts = new[] { 886_430_000.00 }
        };
        var rest = CardProgram.Series("Rest of trust", trustInvested - subjectInvested, 0.0, 0.0);
        var run = CardProgram.Run(CardProgram.Program(), 24_930_085_370.00, new[] { subject, rest }, null, 1,
            new DateTime(2026, 7, 31)) with
        {
            ActualPeriods = new[]
            {
                new ActualPeriod
                {
                    NewReceivables = 12_972_421_911.28,
                    PrincipalCollections = 12_892_126_005.47,
                    FinanceCollections = 694_497_597.08,
                    Recoveries = 14_983_557.22,
                    ChargeOffs = 35_172_824.38
                }
            }
        };

        var result = CardProgram.Execute(run);
        var series = Only(result, "2023-3");
        var month = series.Periods[0];

        month.FloatingPct.Should().BeApproximately(subjectInvested / 24_930_085_370.00 * 100.0, 1e-9);
        month.PrincipalAllocation.Should().BeApproximately(502_358_353.61, OneCent);
        month.InvestorDefault.Should().BeApproximately(1_370_554.56, OneCent);
        month.SharedPrincipalIn.Should().BeApproximately(467_704_091.83, OneCent);
        month.FundingDeposit.Should().BeApproximately(886_430_000.00, OneCent);
        month.FundingRelease.Should().BeApproximately(886_430_000.00, OneCent);
        month.InvestedAmount.Should().BeApproximately(0.0, OneCent);
        month.DistributionDate.Should().Be(new DateTime(2026, 9, 15));

        series.Classes["A"][0].PrincipalPaid.Should().BeApproximately(850_000_000.00, OneCent);
        series.Classes["B"][0].PrincipalPaid.Should().BeApproximately(36_430_000.00, OneCent);
        series.Classes["C"][0].PrincipalPaid.Should().BeApproximately(85_003_000.00, OneCent);
        series.Classes["A"][0].InterestPaid.Should().BeApproximately(3_704_583.33, OneCent);
        series.Classes["B"][0].InterestPaid.Should().BeApproximately(167_578.00, OneCent);
        series.Classes["C"][0].InterestPaid.Should().BeApproximately(495_142.48, OneCent);
    }

    private static RevolvingSeries Accumulating(double controlled, DateTime? expected = null)
    {
        return CardProgram.Series("S", 90_000.0, 10_000.0, 10_000.0) with
        {
            PrincipalSharing = false,
            AccumulationStart = new DateTime(2026, 8, 1),
            ExpectedDate = expected ?? new DateTime(2027, 9, 15),
            ControlledAmounts = new[] { controlled }
        };
    }

    [Fact]
    public void ASlowPaymentRateLeavesADepositShortfallThatCarriesIntoTheNextMonth()
    {
        // A $500,000 pool with a 22% share at 2% a month allocates $2,200 of principal a month
        // against a controlled amount of $10,000.
        var run = CardProgram.Run(CardProgram.Program(), 500_000.0, new[] { Accumulating(10_000.0) },
            CardProgram.Drivers(0.02, 0.02, 0.05, 0.0, 0.0), 4);

        var periods = Only(CardProgram.Execute(run), "S").Periods;

        periods[0].FundingDeposit.Should().BeApproximately(2_200.0, 1.0);
        periods[0].FundingDeficit.Should().BeApproximately(10_000.0 - periods[0].FundingDeposit, OneCent);
        periods[1].FundingDeficit.Should().BeApproximately(
            10_000.0 + periods[0].FundingDeficit - periods[1].FundingDeposit, OneCent);
        periods[3].FundingDeficit.Should().BeGreaterThan(30_000.0);
    }

    [Fact]
    public void ADepositCatchesUpAPriorShortfallAllAtOnce()
    {
        var drivers = new PoolDriverPath
        {
            PaymentRate = new PoolDriver(new[] { 0.01, 0.40 }),
            NewReceivablesRate = new PoolDriver(new[] { 0.01, 0.40 }),
            Yield = CardProgram.Flat(0.05),
            ChargeOffRate = CardProgram.Flat(0.0)
        };
        var run = CardProgram.Run(CardProgram.Program(), 500_000.0, new[] { Accumulating(10_000.0) }, drivers, 3);

        var periods = Only(CardProgram.Execute(run), "S").Periods;

        var first = periods[0].FundingDeposit;
        first.Should().BeApproximately(1_100.0, 1.0);
        periods[1].FundingDeposit.Should().BeApproximately(10_000.0 + (10_000.0 - first), OneCent);
        periods[1].FundingDeficit.Should().BeApproximately(0.0, OneCent);

        // Nothing was re-spread: the third month is back to the plain controlled amount.
        periods[2].FundingDeposit.Should().BeApproximately(10_000.0, OneCent);
    }

    [Fact]
    public void WhatTheAccountCannotPayOnTheExpectedDateMovesTheSeriesToEarlyAmortization()
    {
        var run = CardProgram.Run(CardProgram.Program(), 500_000.0,
            new[] { Accumulating(5_000.0, new DateTime(2026, 11, 15)) }, CardProgram.Drivers(), 6);

        var series = Only(CardProgram.Execute(run), "S");

        series.PhasePath.Select(m => m.Phase).Should().Equal(SeriesPhase.Accumulating, SeriesPhase.EarlyAmortization);
        series.Classes["A"].Take(2).Should().OnlyContain(c => c.PrincipalPaid == 0.0);
        series.Classes["A"][2].PrincipalPaid.Should().BeApproximately(15_000.0, OneCent);
    }

    [Fact]
    public void EarlyAmortizationReleasesTheAccountAtOnce()
    {
        var series = Seeded(Accumulating(5_000.0), 2.0, 1.0) with
        {
            Phase = SeriesPhase.Accumulating,
            FrozenPrincipalPct = 0.22,
            FundingAccount = 30_000.0
        };
        var cure = CardProgram.YieldBelowBaseRate with { Metric = TriggerMetric.PortfolioYield, Level = 99.0, AgainstMetric = null };
        var run = CardProgram.Run(CardProgram.Program(triggers: new[] { cure }), 500_000.0, new[] { series },
            CardProgram.Drivers(), 2);

        var result = Only(CardProgram.Execute(run), "S");

        result.Periods[0].Phase.Should().Be(SeriesPhase.EarlyAmortization);
        result.Periods[0].FundingAccount.Should().Be(0.0);
        result.Periods[0].FundingRelease.Should().Be(30_000.0);
        result.Classes["A"][0].PrincipalPaid.Should().BeGreaterThan(30_000.0);
    }

    [Fact]
    public void ASeriesThatFailsTheYieldTestAmortizesAtTheFrozenPercent()
    {
        // Series 2024-1, seeded with its printed three months. Yield then collapses below the base
        // rate, and three months later the average has fallen through it.
        var series = CardProgram.Series("2024-1", 1_000_000_000.0, 42_858_000.0, 100_001_000.0, 5.23, 5.5, 7.125) with
        {
            History = new Dictionary<TriggerMetric, IReadOnlyList<double>>
            {
                [TriggerMetric.PortfolioYield] = new[] { 32.2522, 32.3582, 33.1491 },
                [TriggerMetric.BaseRate] = new[] { 6.8262, 7.7677, 7.5088 }
            }
        };
        var stress = CardProgram.Drivers(0.509, 0.509, 0.0001, 0.0, 0.0);
        var program = CardProgram.Program(actionLag: 0, triggers: new[] { CardProgram.YieldBelowBaseRate });
        var run = CardProgram.Run(program, 24_725_240_281.83, new[] { series }, stress, 12, new DateTime(2026, 9, 30));

        var result = Only(CardProgram.Execute(run), "2024-1");

        var early = result.Periods.Where(p => p.Phase == SeriesPhase.EarlyAmortization).ToList();
        result.Periods.TakeWhile(p => p.Phase == SeriesPhase.Revolving).Count().Should().BeGreaterThan(0);
        early.Should().NotBeEmpty();
        early.Where(p => p.PrincipalAllocation > 0).Select(p => p.PrincipalPct).Distinct().Should().ContainSingle();

        var firstEarly = early[0];
        firstEarly.PrincipalAllocation.Should().BeApproximately(
            24_725_240_281.83 * 0.509 * firstEarly.PrincipalPct / 100.0, 1.0);

        // Paid in order: A first, and B only once A has gone.
        var a = result.Classes["A"];
        var b = result.Classes["B"];
        foreach (var month in a.Zip(b))
            if (month.First.EndBalance > OneCent)
                month.Second.PrincipalPaid.Should().Be(0.0);
        result.Periods[^1].InvestedAmount.Should().BeLessThan(result.Periods[0].InvestedAmount);
    }

    [Fact]
    public void RecoveriesInThePrincipalBucketRaiseSeriesPrincipalAndLeaveTheLedgerAlone()
    {
        var series = new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) };
        var drivers = CardProgram.Drivers(0.40, 0.40, 0.02, 0.004, 0.001);

        var finance = CardProgram.Execute(CardProgram.Run(CardProgram.Program(RecoveryBucket.Finance),
            1_000_000.0, series, drivers, 1));
        var principal = CardProgram.Execute(CardProgram.Run(CardProgram.Program(RecoveryBucket.PrincipalUpToNewDefaults),
            1_000_000.0, series, drivers, 1));

        finance.Periods[0].Pool.Ending.Should().Be(principal.Periods[0].Pool.Ending);
        finance.Periods[0].Pool.RecoveriesToFinance.Should().Be(1_000.0);
        principal.Periods[0].Pool.RecoveriesToPrincipal.Should().Be(1_000.0);

        var f = finance.Series[0].Periods[0];
        var p = principal.Series[0].Periods[0];
        (p.PrincipalAllocation - f.PrincipalAllocation).Should().BeApproximately(1_000.0 * 0.40, 1e-6);
        (f.FinanceAllocation - p.FinanceAllocation).Should().BeApproximately(1_000.0 * 0.40, 1e-6);
    }

    [Fact]
    public void HoldingThePoolLetsOnlyLossesShrinkIt()
    {
        var run = CardProgram.Run(CardProgram.Program(), 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) },
            CardProgram.Drivers(0.40, null, 0.02, 0.003, 0.0, 0.001), 12);

        var periods = CardProgram.Execute(run).Periods;

        periods.Should().OnlyContain(p => p.Pool.NewReceivables == p.Pool.PrincipalCollections);
        periods[^1].Pool.Ending.Should().BeApproximately(1_000_000.0 * Math.Pow(0.996, 12), 1e-6);
    }

    private static TriggerTest ReserveTest(int cure)
    {
        return new TriggerTest
        {
            Name = "RESERVE_STEP_UP",
            Metric = TriggerMetric.ExcessSpread,
            Window = 1,
            Comparison = TriggerComparison.Below,
            Level = 4.0,
            Action = TriggerAction.FundReserve,
            CurePeriods = cure,
            ReserveAmount = 5_000.0
        };
    }

    private static PoolDriverPath YieldPath(params double[] monthlyYield)
    {
        return new PoolDriverPath
        {
            PaymentRate = CardProgram.Flat(0.40),
            NewReceivablesRate = CardProgram.Flat(0.40),
            Yield = new PoolDriver(monthlyYield),
            ChargeOffRate = CardProgram.Flat(0.0)
        };
    }

    [Fact]
    public void AFundReserveTestCuresAfterItsStatedPeriodsAndReleasesTheReserve()
    {
        var series = CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) with
        {
            History = new Dictionary<TriggerMetric, IReadOnlyList<double>>
            {
                [TriggerMetric.ExcessSpread] = new[] { 3.0 }
            }
        };
        var program = CardProgram.Program(triggers: new[] { ReserveTest(3) });
        var run = CardProgram.Run(program, 1_000_000.0, new[] { series }, YieldPath(0.02), 8);

        var periods = Only(CardProgram.Execute(run), "S").Periods;

        periods[0].Triggers[0].Latched.Should().BeTrue();
        periods[0].ReserveRequired.Should().Be(5_000.0);
        periods[0].Reserve.Should().Be(5_000.0);
        periods[2].Reserve.Should().Be(5_000.0);
        periods[2].Triggers[0].Latched.Should().BeTrue();

        // Three good months after the seeded failure read months 1, 2 and 3, so it cures in month 3.
        periods[3].Triggers[0].Latched.Should().BeFalse();
        periods[3].ReserveRequired.Should().Be(0.0);
        periods[3].ReserveRelease.Should().Be(5_000.0);
        periods[3].Reserve.Should().Be(0.0);
    }

    [Fact]
    public void AFailedMonthRestartsTheCure()
    {
        var series = CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) with
        {
            History = new Dictionary<TriggerMetric, IReadOnlyList<double>>
            {
                [TriggerMetric.ExcessSpread] = new[] { 3.0 }
            }
        };
        var program = CardProgram.Program(triggers: new[] { ReserveTest(2) });

        // A good month, then a thin one, then good months.
        var run = CardProgram.Run(program, 1_000_000.0, new[] { series }, YieldPath(0.02, 0.0005, 0.02), 6);

        var periods = Only(CardProgram.Execute(run), "S").Periods;

        periods[2].Triggers[0].Latched.Should().BeTrue();
        periods[3].Triggers[0].Latched.Should().BeTrue();
        periods[4].Triggers[0].Latched.Should().BeFalse();
    }

    [Fact]
    public void AnEarlyAmortizationTestNeverCures()
    {
        var series = Seeded(CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0), 2.0, 9.0);
        var program = CardProgram.Program(triggers: new[] { CardProgram.YieldBelowBaseRate });
        var run = CardProgram.Run(program, 1_000_000.0, new[] { series }, CardProgram.Drivers(yield: 0.05), 8);

        var result = Only(CardProgram.Execute(run), "S");

        var healthy = result.Periods.Where(p => p.Triggers.Count > 0 && p.Triggers[0].Headroom > 0).ToList();
        healthy.Should().NotBeEmpty();
        healthy.Should().OnlyContain(p => p.Triggers[0].Latched);
        result.Periods.Should().OnlyContain(p => p.Phase == SeriesPhase.EarlyAmortization);
    }

    [Theory]
    [InlineData(TriggerAction.EarlyAmortization, 3, null, "*always sticks*")]
    [InlineData(TriggerAction.FundReserve, null, 100.0, "*curePeriods*")]
    [InlineData(TriggerAction.FundReserve, 3, null, "*reserveAmount*")]
    public void ATriggerThatStatesTheWrongCureIsRefused(TriggerAction action, int? cure, double? reserve,
        string message)
    {
        var test = CardProgram.YieldBelowBaseRate with { Action = action, CurePeriods = cure, ReserveAmount = reserve };

        var refuse = () => CardProgram.Execute(CardProgram.Run(CardProgram.Program(triggers: new[] { test }), 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, CardProgram.Drivers(), 1));

        refuse.Should().Throw<RevolvingConfigException>().WithMessage(message);
    }

    [Fact]
    public void ForecastingFromBeforeAFirstPayDateLosesNoCashAndKeepsThePool()
    {
        // The first waterfall runs three distributions after the statement date. The pool pays 30%
        // a month and is held flat, so the pool would be gone if new receivables stopped.
        var late = CardProgram.Series("New", 300_000.0, 50_000.0, 50_000.0) with
        {
            FirstPayDate = new DateTime(2026, 11, 15)
        };
        var run = CardProgram.Run(CardProgram.Program(), 1_000_000.0, new[] { late },
            CardProgram.Drivers(0.30, null, 0.02, 0.0, 0.0), 5);

        var result = CardProgram.Execute(run);
        var series = Only(result, "New");

        result.Periods.Should().OnlyContain(p => Math.Abs(p.Pool.Ending - 1_000_000.0) < OneCent);

        // September and October only collect: no interest paid, and principal keeps funding new
        // receivables instead of piling up for the first pay date.
        series.Classes["A"].Take(2).Should().OnlyContain(c => c.InterestPaid == 0.0);
        series.Periods[1].CarriedFinance.Should().BeApproximately(2 * 8_000.0, OneCent);
        result.Periods[0].TransferorPrincipal.Should().BeApproximately(result.Periods[0].Pool.PrincipalCash, OneCent);

        // The first waterfall spends everything carried and pays the interest that accrued.
        series.Periods[2].CarriedFinance.Should().Be(0.0);
        series.Classes["A"][2].InterestPaid.Should().BeApproximately(3 * 300_000.0 * 0.05 / 12.0, OneCent);
        series.Classes["A"][2].InterestDueUnpaid.Should().BeApproximately(0.0, OneCent);
        result.Periods[2].TransferorPrincipal.Should().BeApproximately(result.Periods[2].Pool.PrincipalCash, OneCent);
    }

    [Fact]
    public void ExcessFinanceGoesToTheSeriesWithAShortfallInTheSameGroup()
    {
        // Equal shares of a thin yield. High pays 20% and cannot cover itself; Low pays 1%.
        var high = CardProgram.Series("High", 100_000.0, 0.0, 0.0, 20.0);
        var low = CardProgram.Series("Low", 100_000.0, 0.0, 0.0, 1.0);
        var run = CardProgram.Run(CardProgram.Program(), 400_000.0, new[] { high, low },
            CardProgram.Drivers(0.40, 0.40, 0.005, 0.0, 0.0), 1);

        var result = CardProgram.Execute(run);
        var h = Only(result, "High");
        var l = Only(result, "Low");

        // Each collects 500 of finance. High owes 1,666.67 interest + 166.67 servicing.
        h.Periods[0].SharedExcessIn.Should().BeApproximately(500.0 - 83.33 - 166.67, 0.01);
        l.Periods[0].SharedExcessOut.Should().BeApproximately(500.0 - 83.33 - 166.67, 0.01);
        h.Classes["A"][0].InterestPaid.Should().BeApproximately(500.0 + 250.0, 0.01);
        h.Classes["A"][0].InterestDueUnpaid.Should().BeApproximately(1_666.67 - 750.0, 0.01);
    }

    [Fact]
    public void ExcessFinanceDoesNotCrossAGroup()
    {
        var high = CardProgram.Series("High", 100_000.0, 0.0, 0.0, 20.0, group: "1");
        var low = CardProgram.Series("Low", 100_000.0, 0.0, 0.0, 1.0, group: "2");
        var run = CardProgram.Run(CardProgram.Program(), 400_000.0, new[] { high, low },
            CardProgram.Drivers(0.40, 0.40, 0.005, 0.0, 0.0), 1);

        var result = CardProgram.Execute(run);

        Only(result, "High").Periods[0].SharedExcessIn.Should().Be(0.0);
        result.Periods[0].TransferorFinance.Should().BeApproximately(500.0 * 2 + 250.0, 0.01);
    }

    [Fact]
    public void SharedPrincipalIsSplitByShortfallAndTheRestGoesToTheTransferor()
    {
        RevolvingSeries Paying(string name, double controlled)
        {
            return CardProgram.Series(name, 100_000.0, 0.0, 0.0) with
            {
                AccumulationStart = new DateTime(2026, 8, 1),
                ExpectedDate = new DateTime(2027, 9, 15),
                ControlledAmounts = new[] { controlled }
            };
        }

        var one = Paying("One", 4_000.0);
        var two = Paying("Two", 8_000.0);
        var rest = CardProgram.Series("Rest", 400_000.0, 0.0, 0.0);
        var run = CardProgram.Run(CardProgram.Program(), 1_000_000.0, new[] { one, two, rest },
            CardProgram.Drivers(0.02, 0.02, 0.05, 0.0, 0.0), 1);

        var result = CardProgram.Execute(run);

        // Each paying series is allocated 2,000 of principal, and Rest shares its 8,000.
        var first = Only(result, "One").Periods[0];
        var second = Only(result, "Two").Periods[0];
        first.SharedPrincipalIn.Should().BeApproximately(2_000.0, OneCent);
        second.SharedPrincipalIn.Should().BeApproximately(6_000.0, OneCent);
        result.Periods[0].TransferorPrincipal.Should().BeApproximately(
            result.Periods[0].Pool.PrincipalCash - 12_000.0, OneCent);
    }

    [Fact]
    public void ShortSharedPrincipalIsSplitInProportionToTheShortfalls()
    {
        RevolvingSeries Paying(string name, double controlled)
        {
            return CardProgram.Series(name, 100_000.0, 0.0, 0.0) with
            {
                AccumulationStart = new DateTime(2026, 8, 1),
                ExpectedDate = new DateTime(2027, 9, 15),
                ControlledAmounts = new[] { controlled }
            };
        }

        var one = Paying("One", 3_000.0);
        var two = Paying("Two", 5_000.0);
        var rest = CardProgram.Series("Rest", 50_000.0, 0.0, 0.0);
        var run = CardProgram.Run(CardProgram.Program(), 1_000_000.0, new[] { one, two, rest },
            CardProgram.Drivers(0.02, 0.02, 0.05, 0.0, 0.0), 1);

        var result = CardProgram.Execute(run);

        // 20,000 collected: 2,000 each, and 1,000 from Rest against shortfalls of 1,000 and 3,000.
        Only(result, "One").Periods[0].SharedPrincipalIn.Should().BeApproximately(250.0, OneCent);
        Only(result, "Two").Periods[0].SharedPrincipalIn.Should().BeApproximately(750.0, OneCent);
        Only(result, "One").Periods[0].FundingDeficit.Should().BeApproximately(750.0, OneCent);
        Only(result, "Two").Periods[0].FundingDeficit.Should().BeApproximately(2_250.0, OneCent);
    }

    [Fact]
    public void UncoveredDefaultsWriteDownTheStatedOrderAndLaterFinanceReimbursesThem()
    {
        var drivers = new PoolDriverPath
        {
            PaymentRate = CardProgram.Flat(0.0),
            NewReceivablesRate = new PoolDriver(new[] { 0.10, 0.0 }),
            Yield = new PoolDriver(new[] { 0.0, 0.25 }),
            ChargeOffRate = new PoolDriver(new[] { 0.10, 0.0 })
        };
        var series = CardProgram.Series("S", 80_000.0, 10_000.0, 10_000.0) with { Fees = Array.Empty<RevolvingFee>() };
        var run = CardProgram.Run(CardProgram.Program(), 200_000.0, new[] { series }, drivers, 2);

        var result = Only(CardProgram.Execute(run), "S");

        // A 50% share of a 20,000 default with no finance and no principal to reallocate.
        result.Classes["C"][0].Writedown.Should().BeApproximately(10_000.0, OneCent);
        result.Classes["B"][0].Writedown.Should().BeApproximately(0.0, OneCent);
        result.Classes["A"][0].Writedown.Should().BeApproximately(0.0, OneCent);
        result.Periods[0].InvestedAmount.Should().BeApproximately(90_000.0, OneCent);

        // Next month's finance pays interest, then reimburses Class C back to whole.
        result.Classes["C"][1].Writedown.Should().BeApproximately(-10_000.0, OneCent);
        result.Periods[1].InvestedAmount.Should().BeApproximately(100_000.0, OneCent);
    }

    [Fact]
    public void ReallocatedPrincipalPaysSeniorInterestAndTheJuniorClassBearsIt()
    {
        var series = CardProgram.Series("S", 90_000.0, 0.0, 10_000.0, 12.0) with
        {
            Fees = Array.Empty<RevolvingFee>()
        };
        var run = CardProgram.Run(CardProgram.Program(), 100_000.0, new[] { series },
            CardProgram.Drivers(0.50, 0.50, 0.0, 0.0, 0.0), 1);

        var result = Only(CardProgram.Execute(run), "S");

        result.Classes["A"][0].InterestPaid.Should().BeApproximately(900.0, OneCent);
        result.Classes["C"][0].Writedown.Should().BeApproximately(900.0, OneCent);
        result.Periods[0].PrincipalAllocation.Should().BeApproximately(50_000.0, OneCent);
    }

    [Fact]
    public void ExcessFromOtherSeriesComesBeforeReallocatedPrincipal()
    {
        var high = CardProgram.Series("High", 90_000.0, 0.0, 10_000.0, 12.0) with { Fees = Array.Empty<RevolvingFee>() };
        var low = CardProgram.Series("Low", 90_000.0, 0.0, 10_000.0, 1.0) with { Fees = Array.Empty<RevolvingFee>() };

        // Without reimbursement, so a reallocation made too early would stay on Class C.
        var program = CardProgram.Program() with
        {
            FinanceSteps = CardProgram.FinanceSteps.Where(s => s.Verb != WaterfallVerb.ReimburseChargeoff).ToList()
        };
        var run = CardProgram.Run(program, 200_000.0, new[] { high, low },
            CardProgram.Drivers(0.50, 0.50, 0.006, 0.0, 0.0), 1);

        var series = Only(CardProgram.Execute(run), "High");

        // Each collects 600. High owes 958 and Low's surplus covers it, so Class C bears nothing.
        series.Periods[0].SharedExcessIn.Should().BeApproximately(358.33, OneCent);
        series.Classes["C"][0].Writedown.Should().Be(0.0);
    }

    [Fact]
    public void ExcessPercentAndExcessDollarsAreSeparateMeasures()
    {
        var run = CardProgram.Run(CardProgram.Program(), 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, CardProgram.Drivers(), 1);

        var month = Only(CardProgram.Execute(run), "S").Periods[0];

        month.ExcessSpreadPct.Should().BeApproximately(month.PortfolioYieldPct - month.BaseRatePct, 1e-9);
        var fromDollars = month.ExcessDollars / month.InvestedAmount * 12.0 * 100.0;
        fromDollars.Should().NotBeApproximately(month.ExcessSpreadPct, 0.01);
        month.ExcessDollars.Should().BeGreaterThan(0.0);
    }

    [Theory]
    [InlineData(31_138_187.45, 1_142_859_000.00, 30, 33.1491)]
    [InlineData(26_688_296.85, 971_433_000.00, 31, 32.3474)]
    public void SeriesYieldReproducesThePrintedRate(double netFinance, double invested, int days, double printed)
    {
        RevolvingMetrics.PortfolioYieldPct(netFinance, invested, days).Should().BeApproximately(printed, 0.00005);
    }

    [Theory]
    [InlineData(7_053_286.77, 1_142_859_000.00, 30, 7.5088)]
    [InlineData(5_986_358.82, 971_433_000.00, 29, 7.7561)]
    public void BaseRateReproducesThePrintedRate(double interestAndFees, double invested, int days, double printed)
    {
        RevolvingMetrics.BaseRatePct(interestAndFees, invested, days).Should().BeApproximately(printed, 0.00005);
    }

    [Fact]
    public void AMonthsYieldAndBaseRateEachUseTheirOwnDays()
    {
        var series = CardProgram.Series("S", 400_000.0, 0.0, 0.0, 6.0) with { Fees = Array.Empty<RevolvingFee>() };
        var run = CardProgram.Run(CardProgram.Program(), 1_000_000.0, new[] { series },
            CardProgram.Drivers(0.40, 0.40, 0.02, 0.0, 0.0), 1);

        var month = Only(CardProgram.Execute(run), "S").Periods[0];

        // August 2026 has 31 days, and its coupon period 29.
        month.PortfolioYieldPct.Should().BeApproximately(8_000.0 / 400_000.0 * 365.0 / 31.0 * 100.0, 1e-9);
        month.BaseRatePct.Should().BeApproximately(2_000.0 / 400_000.0 * 365.0 / 29.0 * 100.0, 1e-9);
    }

    [Fact]
    public void InterestPeriodsRunBetweenRolledDistributionDates()
    {
        var dates = RevolvingCalendar.Build(new DateTime(2026, 7, 31), 15, 2);

        // August 15, 2026 is a Saturday, so the coupon period is August 17 to September 14.
        dates[0].DistributionDate.Should().Be(new DateTime(2026, 9, 15));
        dates[0].Days.Should().Be(31);
        dates[0].AccrualDays.Should().Be(29);
        dates[1].DistributionDate.Should().Be(new DateTime(2026, 10, 15));
        dates[1].Days.Should().Be(30);
        dates[1].AccrualDays.Should().Be(30);
    }

    [Fact]
    public void AFundingAccountEarns30Over360OnItsOpeningBalanceAndSweepsItToCash()
    {
        var series = Accumulating(0.0) with
        {
            Phase = SeriesPhase.Accumulating,
            FrozenPrincipalPct = 0.22,
            FundingAccount = 60_000.0
        };
        var run = CardProgram.Run(CardProgram.Program(), 500_000.0, new[] { series }, CardProgram.Drivers(), 2) with
        {
            FundingEarningsRate = new[] { 3.0 }
        };

        var result = CardProgram.Execute(run);
        var month = Only(result, "S").Periods[0];

        month.FundingEarnings.Should().BeApproximately(60_000.0 * 0.03 / 12.0, 1e-9);
        month.FundingAccount.Should().Be(60_000.0);
        result.Disclosures.Should().NotContain(d => d.Contains("earnings rate"));
    }

    [Fact]
    public void WithNoEarningsRateAccountsEarnNothingAndTheRunSaysSo()
    {
        var series = Accumulating(0.0) with
        {
            Phase = SeriesPhase.Accumulating,
            FrozenPrincipalPct = 0.22,
            FundingAccount = 60_000.0
        };
        var run = CardProgram.Run(CardProgram.Program(), 500_000.0, new[] { series }, CardProgram.Drivers(), 1) with
        {
            FundingEarningsRate = null
        };

        var result = CardProgram.Execute(run);

        Only(result, "S").Periods[0].FundingEarnings.Should().Be(0.0);
        result.Disclosures.Should().Contain(d => d.Contains("earnings rate"));
    }

    [Fact]
    public void AnUnstatedFeeIsZeroAndDisclosed()
    {
        var series = CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) with { Fees = Array.Empty<RevolvingFee>() };

        var result = CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 1_000_000.0, new[] { series },
            CardProgram.Drivers(), 1));

        Only(result, "S").Periods[0].FeesPaid.Should().Be(0.0);
        result.Disclosures.Should().Contain(d => d.Contains("servicing"));
    }

    [Fact]
    public void AFloatingClassPaysTheIndexPlusItsMarginOnActualDays()
    {
        var series = new RevolvingSeries
        {
            Name = "Floater",
            Classes = new[]
            {
                new RevolvingClass
                {
                    Key = "A",
                    Balance = 360_000.0,
                    Index = GraamFlows.Objects.TypeEnum.MarketDataInstEnum.Sofr30Avg,
                    Margin = 1.0,
                    Basis = GraamFlows.Objects.TypeEnum.AccrualBasis.Actual360
                }
            },
            WritedownOrder = new[] { "A" }
        };
        var program = CardProgram.Program() with
        {
            FinanceSteps = new WaterfallStep[]
            {
                new(WaterfallVerb.PayInterest, "A"), new(WaterfallVerb.ReleaseTransferor)
            },
            PrincipalSteps = new WaterfallStep[] { new(WaterfallVerb.ReleaseTransferor) }
        };

        var month = RevolvingEngine.Run(CardProgram.Run(program, 1_000_000.0, new[] { series }, CardProgram.Drivers(), 1),
            new ConstantRateProvider(4.0)).Series[0].Classes["A"][0];

        month.CouponPct.Should().Be(5.0);
        month.AccrualDays.Should().Be(29);
        month.InterestPaid.Should().BeApproximately(360_000.0 * 0.05 * 29.0 / 360.0, 1e-6);
    }

    [Fact]
    public void ASubjectSeriesPlusARestOfTrustBlockMatchesTheFullProgram()
    {
        var subject = CardProgram.Series("Subject", 300_000.0, 30_000.0, 30_000.0);
        var other1 = CardProgram.Series("Other1", 100_000.0, 10_000.0, 10_000.0);
        var other2 = CardProgram.Series("Other2", 200_000.0, 20_000.0, 20_000.0);
        var rest = CardProgram.Series("Rest", 300_000.0, 30_000.0, 30_000.0);
        var drivers = CardProgram.Drivers(0.45, 0.45, 0.02, 0.004, 0.001);

        var full = Only(CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 2_000_000.0,
            new[] { subject, other1, other2 }, drivers, 24)), "Subject");
        var blocked = Only(CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 2_000_000.0,
            new[] { subject, rest }, drivers, 24)), "Subject");

        foreach (var (a, b) in full.Periods.Zip(blocked.Periods))
        {
            a.FloatingPct.Should().BeApproximately(b.FloatingPct, 1e-12);
            a.ExcessDollars.Should().BeApproximately(b.ExcessDollars, 1e-6);
        }
    }

    [Fact]
    public void AWaterfallThatLeavesCashBehindIsRefusedByName()
    {
        var program = CardProgram.Program() with
        {
            FinanceSteps = new WaterfallStep[]
            {
                new(WaterfallVerb.PayInterest, "A"),
                new(WaterfallVerb.PayInterest, "B"),
                new(WaterfallVerb.PayInterest, "C"),
                new(WaterfallVerb.PayFee, Fee: "servicing")
            },
            PrincipalSteps = new WaterfallStep[] { new(WaterfallVerb.ReleaseTransferor) }
        };

        var refuse = () => CardProgram.Execute(CardProgram.Run(program, 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, CardProgram.Drivers(), 1));

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*left*finance*'S'*");
    }

    [Theory]
    [InlineData(WaterfallVerb.DepositPfa, true)]
    [InlineData(WaterfallVerb.PayInterest, false)]
    public void AVerbOnTheWrongCollectionsIsRefused(WaterfallVerb verb, bool onFinance)
    {
        var steps = new WaterfallStep[] { new(verb, "A"), new(WaterfallVerb.ReleaseTransferor) };
        var program = onFinance
            ? CardProgram.Program() with { FinanceSteps = steps }
            : CardProgram.Program() with { PrincipalSteps = steps };

        var refuse = () => CardProgram.Execute(CardProgram.Run(program, 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, CardProgram.Drivers(), 1));

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*cannot run*");
    }

    [Fact]
    public void AStepNamingAClassTheSeriesLacksIsRefused()
    {
        var series = CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) with
        {
            Classes = new[] { new RevolvingClass { Key = "A", Balance = 400_000.0 } },
            WritedownOrder = new[] { "A" },
            FundedClasses = Array.Empty<string>()
        };

        var refuse = () => CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 1_000_000.0, new[] { series },
            CardProgram.Drivers(), 1));

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*class 'B'*'S'*");
    }

    [Fact]
    public void ASeriesThatStartsPastRevolvingMustStateItsFrozenPercent()
    {
        var series = Accumulating(1_000.0) with { Phase = SeriesPhase.Accumulating };

        var refuse = () => CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 1_000_000.0, new[] { series },
            CardProgram.Drivers(), 1));

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*'S'*frozenPrincipalPct*");
    }

    [Theory]
    [InlineData("*not a month end*", 2026, 7, 15)]
    public void AStatementDateMustCloseAMonth(string message, int year, int month, int day)
    {
        var refuse = () => CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, CardProgram.Drivers(), 1,
            new DateTime(year, month, day)));

        refuse.Should().Throw<RevolvingConfigException>().WithMessage(message);
    }

    [Fact]
    public void ForecastMonthsBeyondTheActualsNeedDrivers()
    {
        var refuse = () => CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, null, 2));

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*no drivers*");
    }

    private static IReadOnlyList<SeriesPeriod> FallingPool(PrincipalFreeze freeze)
    {
        var series = CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) with
        {
            AccumulationStart = new DateTime(2026, 10, 1),
            ExpectedDate = new DateTime(2028, 6, 15),
            ControlledAmounts = new[] { 10_000.0 }
        };
        var run = CardProgram.Run(CardProgram.Program(freeze: freeze), 1_000_000.0, new[] { series },
            CardProgram.Drivers(0.40, 0.36, 0.02, 0.0, 0.0), 8);

        return Only(CardProgram.Execute(run), "S").Periods;
    }

    [Fact]
    public void AFrozenPercentHoldsWhileThePoolFalls()
    {
        var after = FallingPool(PrincipalFreeze.FrozenPercent).Where(p => p.Phase != SeriesPhase.Revolving).ToList();

        after.Select(p => p.PrincipalPct).Distinct().Should().ContainSingle();
    }

    [Fact]
    public void AFrozenNumeratorRisesWhileThePoolFalls()
    {
        var periods = FallingPool(PrincipalFreeze.FrozenNumerator);
        var after = periods.Where(p => p.Phase != SeriesPhase.Revolving).ToList();

        // The first month is the same under both. After that the numerator holds at 400,000 and the
        // pool falls 4% a month, so the share rises by 1 / 0.96 a month.
        after[0].PrincipalPct.Should().BeApproximately(FallingPool(PrincipalFreeze.FrozenPercent)[2].PrincipalPct, 1e-12);
        foreach (var (earlier, later) in after.Zip(after.Skip(1)))
            later.PrincipalPct.Should().BeApproximately(earlier.PrincipalPct / 0.96, 1e-9);
        periods.Where(p => p.Phase == SeriesPhase.Revolving).Should().OnlyContain(p => p.PrincipalPct == p.FloatingPct);
    }

    [Fact]
    public void ASeriesThatStartsPastRevolvingStatesTheValueItsProgramFreezes()
    {
        var series = Accumulating(1_000.0) with { Phase = SeriesPhase.Accumulating, FrozenPrincipalPct = 0.22 };
        var program = CardProgram.Program(freeze: PrincipalFreeze.FrozenNumerator);

        var refuse = () => CardProgram.Execute(CardProgram.Run(program, 500_000.0, new[] { series },
            CardProgram.Drivers(), 1));

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*'S'*frozenNumerator*");
    }

    [Fact]
    public void AFrozenNumeratorFromTheStatementApplies()
    {
        var series = Accumulating(1_000.0) with { Phase = SeriesPhase.Accumulating, FrozenNumerator = 110_000.0 };
        var program = CardProgram.Program(freeze: PrincipalFreeze.FrozenNumerator);

        var month = Only(CardProgram.Execute(CardProgram.Run(program, 500_000.0, new[] { series },
            CardProgram.Drivers(), 1)), "S").Periods[0];

        month.PrincipalPct.Should().BeApproximately(22.0, 1e-9);
    }

    [Fact]
    public void AProgramThatLeavesTheFreezeUnstatedIsRefusedByName()
    {
        var program = CardProgram.Program() with { PrincipalFreeze = null };

        var refuse = () => CardProgram.Execute(CardProgram.Run(program, 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, CardProgram.Drivers(), 1));

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*Example card trust*principalFreeze*");
    }

    [Fact]
    public void ReplayContinuesIntoForecastFromThePrintedEndingBalance()
    {
        var run = CardProgram.Run(CardProgram.Program(), 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) },
            CardProgram.Drivers(0.40, 0.40, 0.02, 0.0, 0.0), 2) with
        {
            ActualPeriods = new[]
            {
                new ActualPeriod
                {
                    NewReceivables = 500.0,
                    PrincipalCollections = 400.0,
                    FinanceCollections = 20.0,
                    ChargeOffs = 100.0
                }
            }
        };

        var periods = CardProgram.Execute(run).Periods;

        periods[0].Pool.Ending.Should().Be(1_000_000.0);
        periods[1].Pool.PrincipalCollections.Should().BeApproximately(400_000.0, 1e-6);
    }

    private static RevolvingRun ReplayThenForecast(int actuals, int forecast, params double[] paymentRates)
    {
        var quiet = new ActualPeriod
        {
            NewReceivables = 0.0,
            PrincipalCollections = 0.0,
            FinanceCollections = 0.0,
            ChargeOffs = 0.0
        };
        var drivers = CardProgram.Drivers(0.40, null, 0.02, 0.0, 0.0) with { PaymentRate = new PoolDriver(paymentRates) };
        return CardProgram.Run(CardProgram.Program(), 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, drivers, actuals + forecast) with
        {
            ActualPeriods = Enumerable.Repeat(quiet, actuals).ToList()
        };
    }

    [Fact]
    public void AForecastVectorStartsOnTheFirstMonthAfterTheReplay()
    {
        var periods = CardProgram.Execute(ReplayThenForecast(2, 2, 0.10, 0.20, 0.30, 0.40)).Periods;

        periods[2].Pool.PrincipalCollections.Should().BeApproximately(100_000.0, 1e-6);
        periods[3].Pool.PrincipalCollections.Should().BeApproximately(200_000.0, 1e-6);
    }

    [Fact]
    public void AForecastVectorIsNotUsedUpByTheMonthsReplayed()
    {
        var rates = Enumerable.Range(1, 12).Select(month => month / 100.0).ToArray();

        var periods = CardProgram.Execute(ReplayThenForecast(12, 2, rates)).Periods;

        periods[12].Pool.PrincipalCollections.Should().BeApproximately(10_000.0, 1e-6);
        periods[13].Pool.PrincipalCollections.Should().BeApproximately(20_000.0, 1e-6);
    }

    [Fact]
    public void AShortForecastVectorHoldsItsLastValueAfterTheReplay()
    {
        var periods = CardProgram.Execute(ReplayThenForecast(1, 3, 0.10, 0.20)).Periods;

        periods[3].Pool.PrincipalCollections.Should().BeApproximately(200_000.0, 1e-6);
    }

    /// <summary>
    ///     Twelve months of deposits that round to just short of the funded classes, with too little
    ///     finance to cover the defaults, so Class C ends written down and dust stays invested.
    /// </summary>
    private static SeriesResult AccumulatesAndWritesOff()
    {
        const double a = 333_333.33;
        const double b = 50_000.0;
        const double c = 50_000.0;
        var series = CardProgram.Series("S", a, b, c) with
        {
            AccumulationStart = new DateTime(2026, 8, 1),
            ExpectedDate = new DateTime(2027, 8, 15),
            ControlledAmounts = new[] { (a + b) / 12.0 }
        };

        return Only(CardProgram.Execute(CardProgram.Run(CardProgram.Program(), (a + b + c) * 2.0, new[] { series },
            CardProgram.Drivers(0.40, null, 0.01, 0.002, 0.0), 18)), "S");
    }

    [Fact]
    public void ARateOnDustIsZero()
    {
        RevolvingMetrics.BaseRatePct(1.0, 1e-10, 30).Should().Be(0.0);
        RevolvingMetrics.PortfolioYieldPct(1.0, 1e-10, 30).Should().Be(0.0);
    }

    [Fact]
    public void DustLeftInAPaidOffSeriesReportsNoRate()
    {
        var series = AccumulatesAndWritesOff();

        series.Periods.Should().Contain(p => p.InvestedAmount > 0.0 && p.InvestedAmount < OneCent);
        series.Periods.Should().OnlyContain(p => Math.Abs(p.BaseRatePct) < 100.0 && Math.Abs(p.ExcessSpreadPct) < 100.0);
    }

    [Fact]
    public void AWrittenDownClassIsNotPaidInFullOnTheExpectedDate()
    {
        var series = AccumulatesAndWritesOff();

        series.Classes["C"][^1].CumWritedown.Should().BeGreaterThan(1_000.0);
        series.PhasePath.Select(m => m.Phase).Should().Equal(SeriesPhase.Accumulating, SeriesPhase.EarlyAmortization);
    }

    [Fact]
    public void AnExhaustedSeriesStopsAccruingOnWhatWasWrittenOff()
    {
        var rows = AccumulatesAndWritesOff().Classes["C"];

        rows[11].InterestAccrued.Should().BeGreaterThan(0.0);
        rows.Skip(12).Should().OnlyContain(row => row.InterestAccrued == 0.0);
        rows[^1].InterestDueUnpaid.Should().Be(rows[11].InterestDueUnpaid);
    }

    [Fact]
    public void AWrittenDownClassInALiveSeriesAccruesOnItsOutstandingBalance()
    {
        // The filing charges interest on the outstanding principal balance, which a write-down leaves.
        var seeded = CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0);
        var series = seeded with
        {
            Classes = seeded.Classes.Select(c => c.Key == "B" ? c with { WrittenDown = 20_000.0 } : c).ToList()
        };

        var rows = Only(CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 1_000_000.0, new[] { series },
            CardProgram.Drivers(), 1)), "S").Classes["B"];

        rows[0].InterestAccrued.Should().BeApproximately(50_000.0 * 0.055 / 12.0, 1e-9);
    }

    [Fact]
    public void ACashProofThatComesToNaNFails()
    {
        // Every figure is legal and finite, and their sums overflow: infinity less infinity is NaN,
        // and any comparison with NaN is false.
        var run = CardProgram.Run(CardProgram.Program(), 1.5e308,
            new[] { CardProgram.Series("S", 3e307, 3e307, 3e307) }, CardProgram.Drivers(0.90, null, 0.90, 0.0, 0.0), 1);

        var prove = () => CardProgram.Execute(run);

        prove.Should().Throw<InvalidOperationException>().WithMessage("*collected*placed*");
    }

    [Theory]
    [InlineData(100.0, 101.0)]
    [InlineData(100.0, 100.002)]
    [InlineData(101.0, 100.0)]
    public void TheCashProofCatchesARealGap(double collected, double placed)
    {
        var prove = () => RevolvingCashProof.Require("Example", 3, collected, placed);

        prove.Should().Throw<InvalidOperationException>().WithMessage("Period 3 of 'Example' collected*");
    }

    [Theory]
    [InlineData(100.0, 100.0)]
    [InlineData(1e11, 1e11 + 2e-4)]
    public void TheCashProofLeavesRoomForFloatingPointError(double collected, double placed)
    {
        var prove = () => RevolvingCashProof.Require("Example", 3, collected, placed);

        prove.Should().NotThrow();
    }

    private static readonly TriggerTest ReserveOnThinSpread = new()
    {
        Name = "RESERVE_ON_THIN_SPREAD",
        Metric = TriggerMetric.ExcessSpread,
        Window = 1,
        Comparison = TriggerComparison.Below,
        Level = 2.0,
        Action = TriggerAction.FundReserve,
        CurePeriods = 2,
        ReserveAmount = 5.0
    };

    private static RevolvingRun StressedRun(int lag, params TriggerTest[] tests)
    {
        // One series is short of its fee, then fails its test. The other has yet to pay, so its
        // finance waits and its interest, fee and default accrue. A recovery lets a reserve test cure.
        var paying = CardProgram.Series("Paying", 400.0, 50.0, 50.0) with
        {
            AccumulationStart = new DateTime(2026, 8, 1),
            ExpectedDate = new DateTime(2027, 6, 15),
            ControlledAmounts = new[] { 10.0, 20.0, 30.0, 40.0 }
        };
        var waiting = CardProgram.Series("Waiting", 400.0, 50.0, 50.0) with
        {
            ClosingDate = new DateTime(2026, 7, 20),
            FirstPayDate = new DateTime(2026, 12, 15)
        };
        var drivers = CardProgram.Drivers(0.4, 0.4, 0.0005, 0.002, 0.0) with
        {
            Yield = new PoolDriver(new[] { 0.0005, 0.0005, 0.05, 0.05, 0.05, 0.0005 })
        };
        return CardProgram.Run(CardProgram.Program(actionLag: lag, triggers: tests), 2_000.0,
            new[] { paying, waiting }, drivers, 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ARunCutIntoMonthsMatchesOneLongRun(int lag)
    {
        var yieldBelow = CardProgram.YieldBelowBaseRate with { Window = 1 };
        var run = StressedRun(lag, yieldBelow, ReserveOnThinSpread);
        var whole = CardProgram.Execute(run);

        var months = CardProgram.MonthByMonth(run, whole);

        CardProgram.RequireSameMonths(whole, months);
        months.Should().Contain(m => m.Series.Any(s => s.Closing.FeesDue.Values.Sum() > 1.0));
        months.Should().Contain(m => m.Series.Any(s => s.Closing.CarriedFinance > 1.0));
        months.Should().Contain(m => m.Series.Any(s => s.Closing.Classes.Any(c => c.DefaultDue > 0.1)));
        whole.Series.Should().Contain(s => s.PhasePath.Any(p => p.Phase == SeriesPhase.EarlyAmortization));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASeriesWrittenOffBeforeItsAccumulationCanBeCut(bool sharing)
    {
        // It reaches accumulation with nothing invested, so nothing froze. With sharing, another
        // series' excess reinstates it and it takes its first allocation after the cut.
        var wiped = CardProgram.Series("Wiped", 1_000.0, 100.0, 100.0) with
        {
            ExcessSharing = sharing,
            AccumulationStart = new DateTime(2026, 9, 1),
            ExpectedDate = new DateTime(2027, 6, 15),
            ControlledAmounts = new[] { 100.0 }
        };
        wiped = wiped with { Classes = wiped.Classes.Select(c => c with { WrittenDown = c.Balance }).ToList() };
        foreach (var freeze in new[] { PrincipalFreeze.FrozenPercent, PrincipalFreeze.FrozenNumerator })
        {
            var run = CardProgram.Run(CardProgram.Program(freeze: freeze), 6_000.0,
                new[] { wiped, CardProgram.Series("Live", 3_000.0, 300.0, 300.0) }, CardProgram.Drivers(), 6);
            var whole = CardProgram.Execute(run);

            var months = CardProgram.MonthByMonth(run, whole);

            CardProgram.RequireSameMonths(whole, months);
            months[2].Series[0].Closing.Phase.Should().Be(SeriesPhase.Accumulating);
        }
    }

    [Theory]
    [InlineData(PrincipalFreeze.FrozenPercent)]
    [InlineData(PrincipalFreeze.FrozenNumerator)]
    public void ASeriesReinstatedAfterLeavingRevolvingCanBeCutEveryMonth(PrincipalFreeze freeze)
    {
        // No yield for three months, so it reaches accumulation with nothing frozen, and is reinstated
        // late in a month: it closes with an amount and no frozen value, which its next month sets.
        var wiped = CardProgram.Series("Wiped", 1_000.0, 100.0, 100.0) with
        {
            AccumulationStart = new DateTime(2026, 9, 1),
            ExpectedDate = new DateTime(2027, 6, 15),
            ControlledAmounts = new[] { 100.0 }
        };
        wiped = wiped with { Classes = wiped.Classes.Select(c => c with { WrittenDown = c.Balance }).ToList() };
        var drivers = CardProgram.Drivers(0.4, 0.4, 0.0, 0.0, 0.0) with
        {
            Yield = new PoolDriver(new[] { 0.0, 0.0, 0.0, 0.25 })
        };
        var run = CardProgram.Run(CardProgram.Program(freeze: freeze), 6_000.0,
            new[] { wiped, CardProgram.Series("Live", 3_000.0, 300.0, 300.0) }, drivers, 8);
        var whole = CardProgram.Execute(run);

        var months = CardProgram.MonthByMonth(run, whole);

        CardProgram.RequireSameMonths(whole, months);
        var reinstated = months.Select(m => m.Series[0].Closing).First(c => c.Classes[0].WrittenDown < c.Classes[0].Balance);
        reinstated.FreezePending.Should().BeTrue();
        reinstated.FrozenPrincipalPct.Should().BeNull();
        reinstated.FrozenNumerator.Should().BeNull();
        months[^1].Series[0].Closing.FreezePending.Should().BeFalse();
    }

    [Fact]
    public void ASeriesThatLeftRevolvingWithAnAmountMustStateItsFrozenValue()
    {
        var series = CardProgram.Series("S", 400.0, 50.0, 50.0) with { Phase = SeriesPhase.EarlyAmortization };
        var run = CardProgram.Run(CardProgram.Program(), 2_000.0, new[] { series }, CardProgram.Drivers(), 1);

        ((Action)(() => CardProgram.Execute(run))).Should().Throw<RevolvingConfigException>().WithMessage("*frozen*");
    }

    [Fact]
    public void ARunCutIntoMonthsKeepsAFrozenNumeratorAndGroupReallocation()
    {
        var stressed = StressedRun(1, CardProgram.YieldBelowBaseRate with { Window = 1 });
        var run = stressed with
        {
            Program = stressed.Program with
            {
                PrincipalFreeze = PrincipalFreeze.FrozenNumerator,
                GroupSteps = new[] { new WaterfallStep(WaterfallVerb.ReallocateGroupFinance) }
            }
        };
        var whole = CardProgram.Execute(run);

        var months = CardProgram.MonthByMonth(run, whole);

        CardProgram.RequireSameMonths(whole, months);
        months[^1].Series.Should().Contain(s => s.Closing.FrozenNumerator != null);
    }

    [Fact]
    public void ATestTrippedInOneRunActsAtItsLagInTheNext()
    {
        var yieldBelow = CardProgram.YieldBelowBaseRate with { Window = 1 };
        var run = StressedRun(2, yieldBelow);
        var months = CardProgram.MonthByMonth(run, CardProgram.Execute(run));

        // The first month's rates are read at the second month's open, and it acts two months on.
        var trip = months.First(m => m.Series.Any(s => s.Closing.TriggerStates.Count > 0));
        trip.Series.Single(s => s.Name == "Paying").Closing.TriggerStates[yieldBelow.Name].Should()
            .Be(new TriggerState(true, 1, 0));
    }

    [Fact]
    public void AReserveTestCuresAcrossACut()
    {
        var run = StressedRun(0, ReserveOnThinSpread);
        var whole = CardProgram.Execute(run);

        var months = CardProgram.MonthByMonth(run, whole);

        CardProgram.RequireSameMonths(whole, months);
        months.Should().Contain(m => m.Series.Any(s => s.Closing.TriggerStates.Values.Any(t => t.GoodMonths > 0)));
        whole.Series[0].Periods.Should().Contain(p => p.ReserveRequired > 0.0);
    }

    [Fact]
    public void AClosingKeepsTheOriginalBalanceAFactorIsMeasuredFrom()
    {
        var run = CardProgram.Run(CardProgram.Program(), 2_000.0,
            new[] { CardProgram.Series("S", 400.0, 50.0, 50.0) with { Phase = SeriesPhase.EarlyAmortization, FrozenPrincipalPct = 0.25 } },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);

        var closing = CardProgram.Execute(run).Series[0].Closing;

        closing.Classes[0].Balance.Should().BeLessThan(400.0);
        closing.Classes[0].OriginalBalance.Should().Be(400.0);
    }

    private static RevolvingRun WithClassD(RevolvingClass d, double? fee = null)
    {
        var series = CardProgram.Series("S", 400.0, 50.0, 50.0);
        return CardProgram.Run(CardProgram.Program(), 2_000.0,
            new[]
            {
                series with
                {
                    Classes = series.Classes.Append(d).ToList(),
                    WritedownOrder = new[] { "D", "C", "B", "A" },
                    Fees = fee == null ? series.Fees : series.Fees.Append(new RevolvingFee("extra", fee.Value)).ToList()
                }
            }, CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);
    }

    [Fact]
    public void AClassWithNoCouponNeedsNoInterestStep()
    {
        var run = () => CardProgram.Execute(WithClassD(new RevolvingClass { Key = "D", Balance = 50.0, Coupon = 0.0 }));

        run.Should().NotThrow();
    }

    [Fact]
    public void AClassWithNoBalanceNeedsNoInterestStep()
    {
        var run = () => CardProgram.Execute(WithClassD(new RevolvingClass { Key = "D", Balance = 0.0, Coupon = 5.0 }));

        run.Should().NotThrow();
    }

    [Theory]
    [InlineData(50.0, 5.0, 0.0)]
    [InlineData(50.0, 0.0, 1.0)]
    public void AClassThatOwesInterestStillNeedsItsStep(double balance, double coupon, double shortfall)
    {
        var run = () => CardProgram.Execute(WithClassD(
            new RevolvingClass { Key = "D", Balance = balance, Coupon = coupon, InterestShortfall = shortfall }));

        run.Should().Throw<RevolvingConfigException>().WithMessage("*no PAY_INTEREST step*");
    }

    [Fact]
    public void AFeeOfZeroNeedsNoFeeStep()
    {
        var series = CardProgram.Series("S", 400.0, 50.0, 50.0);
        var run = CardProgram.Run(CardProgram.Program(), 2_000.0,
            new[] { series with { Fees = series.Fees.Append(new RevolvingFee("nil", 0.0)).ToList() } },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);

        ((Action)(() => CardProgram.Execute(run))).Should().NotThrow();
    }

    [Fact]
    public void AFeeOfZeroWithSomethingStillDueNeedsItsStep()
    {
        var series = CardProgram.Series("S", 400.0, 50.0, 50.0);
        var run = CardProgram.Run(CardProgram.Program(), 2_000.0,
            new[]
            {
                series with
                {
                    Fees = series.Fees.Append(new RevolvingFee("nil", 0.0)).ToList(),
                    FeesDue = new Dictionary<string, double> { ["nil"] = 5.0 }
                }
            },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);

        ((Action)(() => CardProgram.Execute(run))).Should().Throw<RevolvingConfigException>().WithMessage("*no PAY_FEE step*");
    }

    [Fact]
    public void NewReceivablesCanPassTheBeginningBalance()
    {
        var run = CardProgram.Run(CardProgram.Program(), 2_000.0, new[] { CardProgram.Series("S", 400.0, 50.0, 50.0) },
            CardProgram.Drivers(0.3, 1.5, 0.05, 0.0, 0.0), 1);

        CardProgram.Execute(run).Periods[0].Pool.Ending.Should().BeApproximately(2_000.0 + 3_000.0 - 600.0, 1e-9);
    }

    [Fact]
    public void ACollectionsDriverOverTheBalanceIsStillRefused()
    {
        var run = CardProgram.Run(CardProgram.Program(), 2_000.0, new[] { CardProgram.Series("S", 400.0, 50.0, 50.0) },
            CardProgram.Drivers(1.5, 0.4, 0.05, 0.0, 0.0), 1);

        ((Action)(() => CardProgram.Execute(run))).Should().Throw<RevolvingConfigException>().WithMessage("*paymentRate*at most 100%*");
    }

    [Fact]
    public void ANegativeNewReceivablesRateIsRefused()
    {
        var run = CardProgram.Run(CardProgram.Program(), 2_000.0, new[] { CardProgram.Series("S", 400.0, 50.0, 50.0) },
            CardProgram.Drivers(0.3, -0.1, 0.05, 0.0, 0.0), 1);

        ((Action)(() => CardProgram.Execute(run))).Should().Throw<RevolvingConfigException>().WithMessage("*newReceivablesRate*at least 0%*");
    }

    [Theory]
    [InlineData("carried")]
    [InlineData("feeName")]
    [InlineData("feeAmount")]
    [InlineData("testName")]
    [InlineData("months")]
    [InlineData("defaultDue")]
    public void ClosingStateIsCheckedLikeAnyOtherFigure(string field)
    {
        var series = CardProgram.Series("S", 400.0, 50.0, 50.0);
        var bad = field switch
        {
            "carried" => series with { CarriedFinance = double.NaN },
            "feeName" => series with { FeesDue = new Dictionary<string, double> { ["nope"] = 1.0 } },
            "feeAmount" => series with { FeesDue = new Dictionary<string, double> { ["servicing"] = -5.0 } },
            "testName" => series with { TriggerStates = new Dictionary<string, TriggerState> { ["nope"] = new(true, 0, 0) } },
            "months" => series with { TriggerStates = new Dictionary<string, TriggerState> { [CardProgram.YieldBelowBaseRate.Name] = new(true, -1, 0) } },
            _ => series with { Classes = series.Classes.Select(c => c with { DefaultDue = -5.0 }).ToList() }
        };
        var run = CardProgram.Run(CardProgram.Program(triggers: new[] { CardProgram.YieldBelowBaseRate }), 2_000.0, new[] { bad },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);

        ((Action)(() => CardProgram.Execute(run))).Should().Throw<RevolvingConfigException>();
    }

    [Fact]
    public void ASeriesWrittenOffButHoldingItsAccountStillAccruesInterest()
    {
        // Nothing is invested, and the account still holds cash that can pay the class.
        var series = CardProgram.Series("S", 400.0, 50.0, 50.0) with
        {
            Phase = SeriesPhase.Accumulating,
            FrozenPrincipalPct = 0.25,
            FundingAccount = 100.0,
            AccumulationStart = new DateTime(2026, 7, 1),
            ExpectedDate = new DateTime(2027, 7, 15),
            ControlledAmounts = new[] { 10.0 }
        };
        series = series with { Classes = series.Classes.Select(c => c with { WrittenDown = c.Balance }).ToList() };
        var result = CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 2_000.0, new[] { series },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1));

        result.Series[0].Classes["A"][0].InterestAccrued.Should().BeApproximately(400.0 * 0.05 / 12.0, 1e-9);
    }

    private static RevolvingSeries WrittenOff(string name, bool sharing, double interest = 0.0, double reserve = 0.0)
    {
        var series = CardProgram.Series(name, 100.0, 10.0, 10.0) with { ExcessSharing = sharing, Reserve = reserve };
        return series with
        {
            Classes = series.Classes.Select(c => c with { WrittenDown = c.Balance, InterestShortfall = c.Key == "A" ? interest : 0.0 }).ToList()
        };
    }

    private static RevolvingResult OwedAndWrittenOff(params RevolvingSeries[] series) =>
        CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 2_000.0, series, CardProgram.Drivers(0.4, 0.4, 0.0, 0.0, 0.0), 1));

    [Fact]
    public void ASeriesWrittenOffIsNotRetired()
    {
        OwedAndWrittenOff(WrittenOff("Owed", true), CardProgram.Series("Live", 400.0, 50.0, 50.0)).Series[0].Retired.Should().BeFalse();
    }

    [Fact]
    public void ASeriesOwedInterestIsNotRetired()
    {
        var paid = CardProgram.Series("Owed", 0.0, 0.0, 0.0);
        paid = paid with { Classes = paid.Classes.Select(c => c with { InterestShortfall = 5.0 }).ToList() };

        OwedAndWrittenOff(paid).Series[0].Retired.Should().BeFalse();
    }

    [Fact]
    public void ASeriesPaidAndHoldingNothingIsRetired()
    {
        OwedAndWrittenOff(CardProgram.Series("Paid", 0.0, 0.0, 0.0)).Series[0].Retired.Should().BeTrue();
    }

    private static RevolvingSeries PaidOff(string name = "Paid") => CardProgram.Series(name, 0.0, 0.0, 0.0);

    private static RevolvingSeries AwaitingFirstPay(RevolvingSeries series) => series with
    {
        ClosingDate = new DateTime(2026, 7, 10),
        FirstPayDate = new DateTime(2026, 12, 15)
    };

    [Fact]
    public void ASeriesHoldingAFundingAccountIsNotRetired()
    {
        var holding = PaidOff() with
        {
            Phase = SeriesPhase.Accumulating,
            FrozenPrincipalPct = 0.25,
            FundingAccount = 50.0,
            AccumulationStart = new DateTime(2026, 7, 1),
            ExpectedDate = new DateTime(2027, 7, 15),
            ControlledAmounts = new[] { 10.0 }
        };

        OwedAndWrittenOff(holding).Series[0].Retired.Should().BeFalse();
    }

    [Fact]
    public void ASeriesHoldingCarriedFinanceIsNotRetired()
    {
        var waiting = AwaitingFirstPay(PaidOff()) with { CarriedFinance = 5.0 };

        OwedAndWrittenOff(waiting).Series[0].Retired.Should().BeFalse();
    }

    [Fact]
    public void ASeriesWithADefaultStillDueIsNotRetired()
    {
        var waiting = AwaitingFirstPay(PaidOff());
        waiting = waiting with { Classes = waiting.Classes.Select(c => c with { DefaultDue = 5.0 }).ToList() };

        OwedAndWrittenOff(waiting).Series[0].Retired.Should().BeFalse();
    }

    [Fact]
    public void ASeriesHoldingFundingMustStateItsFrozenValueEvenWrittenOff()
    {
        var series = WrittenOff("Held", false) with { Phase = SeriesPhase.EarlyAmortization, FundingAccount = 50.0 };

        ((Action)(() => OwedAndWrittenOff(series))).Should().Throw<RevolvingConfigException>().WithMessage("*frozen*");
    }

    [Fact]
    public void ALiveSeriesIsNotStranded()
    {
        OwedAndWrittenOff(CardProgram.Series("Live", 400.0, 50.0, 50.0)).Series[0].Stranded.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void AWrittenOffSeriesIsStrandedOnlyWhenNoExcessCanReachIt(bool sharing, bool liveInGroup, bool stranded)
    {
        var other = CardProgram.Series("Live", 400.0, 50.0, 50.0, group: liveInGroup ? "1" : "2");

        OwedAndWrittenOff(WrittenOff("Owed", sharing, 5.0), other).Series[0].Stranded.Should().Be(stranded);
    }

    [Fact]
    public void AWrittenOffSeriesIsReachedByReallocationWithoutSharing()
    {
        var series = new[] { WrittenOff("Owed", false, 5.0), CardProgram.Series("Live", 400.0, 50.0, 50.0) };
        var run = CardProgram.Run(CardProgram.Program(), 2_000.0, series, CardProgram.Drivers(0.4, 0.4, 0.0, 0.0, 0.0), 1);
        var reallocating = run with
        {
            Program = run.Program with { GroupSteps = new[] { new WaterfallStep(WaterfallVerb.ReallocateGroupFinance) } }
        };

        CardProgram.Execute(reallocating).Series[0].Stranded.Should().BeFalse();
    }

    [Fact]
    public void AReserveReleasedPaysWhatItCanAndTheRestIsStranded()
    {
        var result = CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 2_000.0, new[] { WrittenOff("Owed", false, 5.0, 3.0) },
            CardProgram.Drivers(0.4, 0.4, 0.0, 0.0, 0.0), 1));

        result.Series[0].Classes["A"][0].InterestPaid.Should().BeApproximately(3.0, 1e-9);
        result.Series[0].Stranded.Should().BeTrue();
    }

    [Fact]
    public void AStackReleasesAWrittenOffSeriesReserveThroughItsSteps()
    {
        // Only a note tranche draws its reserve for principal. Here it runs through the finance steps.
        var result = CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 2_000.0, new[] { WrittenOff("Owed", false, 5.0, 50.0) },
            CardProgram.Drivers(0.4, 0.4, 0.0, 0.0, 0.0), 1));

        result.Series[0].Classes["A"][0].PrincipalPaid.Should().Be(0.0);
    }

    [Fact]
    public void EveryCollectedDollarLandsAcrossStressedRuns()
    {
        var random = new Random(6122);
        for (var scenario = 0; scenario < 100; scenario++)
        {
            var seriesCount = random.Next(1, 7);
            var series = new List<RevolvingSeries>();
            for (var i = 0; i < seriesCount; i++)
            {
                var size = random.Next(50_000, 400_000);
                var start = new DateTime(2026, 8, 1).AddMonths(random.Next(0, 10));
                var candidate = CardProgram.Series($"S{i}", size * 0.8, size * 0.1, size * 0.1,
                    random.Next(1, 12), random.Next(1, 14), random.Next(1, 16), $"{random.Next(0, 2)}") with
                {
                    PrincipalSharing = random.Next(0, 3) > 0,
                    ExcessSharing = random.Next(0, 3) > 0,
                    RequiredReserve = random.Next(0, 2) * 1_000.0,
                    FirstPayDate = random.Next(0, 3) == 0 ? new DateTime(2026, 11, 15) : null
                };
                series.Add(random.Next(0, 2) == 0
                    ? candidate
                    : candidate with
                    {
                        AccumulationStart = start,
                        ExpectedDate = start.AddMonths(random.Next(1, 8)).AddDays(14),
                        ControlledAmounts = new[] { size * random.NextDouble() / 3.0 }
                    });
            }

            var pool = series.Sum(s => s.Classes.Sum(c => c.Balance)) * (1.0 + random.NextDouble());
            var program = CardProgram.Program(
                random.Next(0, 2) == 0 ? RecoveryBucket.Finance : RecoveryBucket.PrincipalUpToNewDefaults,
                random.Next(0, 2),
                freeze: random.Next(0, 2) == 0 ? PrincipalFreeze.FrozenPercent : PrincipalFreeze.FrozenNumerator,
                triggers: new[]
                {
                    CardProgram.YieldBelowBaseRate,
                    new TriggerTest
                    {
                        Name = "RESERVE",
                        Metric = TriggerMetric.ExcessSpread,
                        Window = 2,
                        Comparison = TriggerComparison.Below,
                        Level = 6.0,
                        Action = TriggerAction.FundReserve,
                        CurePeriods = 2,
                        ReserveAmount = 3_000.0
                    }
                });
            var payment = 0.02 + random.NextDouble() * 0.5;
            var drivers = new PoolDriverPath
            {
                PaymentRate = CardProgram.Flat(payment),
                NewReceivablesRate = CardProgram.Flat(payment * (0.8 + random.NextDouble() * 0.3)),
                Yield = new PoolDriver(new[] { 0.002 + random.NextDouble() * 0.03, 0.001 + random.NextDouble() * 0.02 }),
                ChargeOffRate = CardProgram.Flat(random.NextDouble() * 0.02),
                RecoveryRate = CardProgram.Flat(random.NextDouble() * 0.01),
                DilutionRate = CardProgram.Flat(random.NextDouble() * 0.002)
            };

            if (random.Next(0, 2) == 0)
                program = program with { GroupSteps = new WaterfallStep[] { new(WaterfallVerb.ReallocateGroupFinance) } };

            var run = CardProgram.Run(program, pool, series, drivers, 24) with { FundingEarningsRate = new[] { 2.5 } };
            var result = CardProgram.Execute(run);

            AssertEveryDollarLands(result, series, $"scenario {scenario}");
            AssertPhasesOnlyMoveForward(result, $"scenario {scenario}");
        }
    }

    private static void AssertEveryDollarLands(RevolvingResult result, IReadOnlyList<RevolvingSeries> series,
        string because)
    {
        var accounts = series.Sum(s => s.FundingAccount + s.Reserve);
        foreach (var month in result.Periods)
        {
            var t = month.Dates.Period;
            var rows = result.Series.Select(s => s.Periods[t]).ToList();
            var classRows = result.Series.SelectMany(s => s.Classes.Values.Select(c => c[t])).ToList();

            var closing = rows.Sum(r => r.FundingAccount + r.Reserve + r.CarriedFinance);
            var collected = month.Pool.FinanceCollections + month.Pool.PrincipalCollections + month.Pool.Recoveries
                            + rows.Sum(r => r.FundingEarnings);
            var placed = classRows.Sum(c => c.InterestPaid + c.PrincipalPaid) + rows.Sum(r => r.FeesPaid)
                                                                              + month.TransferorFinance
                                                                              + month.TransferorPrincipal
                                                                              + closing - accounts;

            placed.Should().BeApproximately(collected, OneCent, because);

            // The transferor's figures are what the engine left over, so the sum above cannot see a
            // series taking more than its share. These can.
            month.TransferorFinance.Should().BeGreaterThanOrEqualTo(-OneCent, because);
            month.TransferorPrincipal.Should().BeGreaterThanOrEqualTo(-OneCent, because);
            rows.Sum(r => r.FinanceAllocation).Should().BeLessThanOrEqualTo(month.Pool.FinanceCash + OneCent, because);
            rows.Sum(r => r.PrincipalAllocation).Should().BeLessThanOrEqualTo(month.Pool.PrincipalCash + OneCent, because);
            foreach (var r in rows)
            {
                r.FinanceAllocation.Should().BeApproximately(month.Pool.FinanceCash * r.FloatingPct / 100.0, OneCent, because);
                r.PrincipalAllocation.Should().BeApproximately(month.Pool.PrincipalCash * r.PrincipalPct / 100.0, OneCent, because);
            }

            rows.Sum(r => r.SharedPrincipalIn).Should().BeLessThanOrEqualTo(
                rows.Sum(r => r.SharedPrincipalOut) + OneCent, because);
            rows.Sum(r => r.SharedExcessIn).Should().BeLessThanOrEqualTo(
                rows.Sum(r => r.SharedExcessOut) + OneCent, because);
            accounts = closing;
        }
    }

    private static void AssertPhasesOnlyMoveForward(RevolvingResult result, string because)
    {
        foreach (var series in result.Series)
            foreach (var (from, to) in series.PhasePath.Zip(series.PhasePath.Skip(1)))
                SeriesPhaseMachine.CanMove(from.Phase, to.Phase).Should().BeTrue(because);
    }
}
