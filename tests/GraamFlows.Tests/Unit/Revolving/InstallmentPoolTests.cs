using FluentAssertions;
using GraamFlows.Revolving;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>
///     A pool of fixed 0% installment contracts, valued at the present value of its remaining payments
///     at a discount rate, against closed forms.
/// </summary>
public class InstallmentPoolTests
{
    private static readonly DateTime Statement = new(2026, 7, 31);
    private static readonly double[] Rates = { 0.0, 0.05, 0.09, 0.12 };

    /// <summary>A level 0% payment's present value: <c>P × (1 − (1 + i)^−n) / i</c>, i = r / 12.</summary>
    private static double Annuity(double payment, int n, double rate)
    {
        var i = rate / 12.0;
        return i == 0.0 ? payment * n : payment * (1.0 - Math.Pow(1.0 + i, -n)) / i;
    }

    private static InstallmentCohort Level(string name, double face, int term, int age)
    {
        return new InstallmentCohort { Name = name, Age = age, Schedule = InstallmentCohort.LevelSchedule(face, term) };
    }

    /// <summary>Two seasoned cohorts with uneven schedules, as a pool is stated: payments, not a term.</summary>
    private static IReadOnlyList<InstallmentCohort> Opening => new[]
    {
        Level("seasoned", 600_000.0, 20, 16),
        new InstallmentCohort
        {
            Name = "uneven",
            Age = 3,
            Schedule = new[] { 41_000.0, 40_500.0, 40_250.0, 39_000.0, 39_000.0, 38_000.0, 12_000.0, 11_000.0, 5_000.0 }
        }
    };

    private static InstallmentDrivers Drivers(double prepay = 0.0, double upgrade = 0.0, double writeOff = 0.0)
    {
        return new InstallmentDrivers
        {
            Prepayment = new InstallmentCurve(new[] { prepay }),
            Upgrade = new InstallmentCurve(new[] { upgrade }),
            WriteOff = new InstallmentCurve(new[] { writeOff })
        };
    }

    private static InstallmentPoolResult Run(InstallmentDrivers? drivers, int periods = 1,
        IReadOnlyList<InstallmentActual>? actuals = null, IReadOnlyList<InstallmentPurchase>? purchases = null,
        IReadOnlyList<InstallmentCohort>? cohorts = null)
    {
        return InstallmentPoolLedger.Run(new InstallmentPoolRun
        {
            StatementDate = Statement,
            Periods = periods,
            Cohorts = cohorts ?? Opening,
            DiscountRates = Rates,
            Drivers = drivers,
            ActualPeriods = actuals ?? Array.Empty<InstallmentActual>(),
            Purchases = purchases ?? Array.Empty<InstallmentPurchase>()
        });
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.05)]
    [InlineData(0.0975)]
    [InlineData(0.24)]
    public void ALevelStreamIsWorthTheAnnuity(double rate)
    {
        var schedule = InstallmentCohort.LevelSchedule(36_000.0, 36);

        InstallmentValuationMath.PresentValue(schedule, rate)
            .Should().BeApproximately(Annuity(1_000.0, 36, rate), 1e-8);
    }

    [Fact]
    public void ThePoolOpensAtThePresentValueOfTheStatedSchedules()
    {
        var month = Run(Drivers()).Periods[0];

        foreach (var v in month.Valuations)
            v.Beginning.Should().BeApproximately(
                Opening.Sum(c => InstallmentValuationMath.PresentValue(c.Schedule, v.Rate)), 1e-8);
        month.BeginningFace.Should().BeApproximately(Opening.Sum(c => c.Face), 1e-8);
    }

    [Fact]
    public void APrepaymentAtSurvivalSScalesTheValueAtEveryRateByS()
    {
        const double prepay = 0.07;
        var none = Run(Drivers()).Periods[0];
        var some = Run(Drivers(prepay)).Periods[0];

        for (var r = 0; r < Rates.Length; r++)
        {
            some.Valuations[r].Ending.Should().BeApproximately((1.0 - prepay) * none.Valuations[r].Ending, 1e-8);
            some.Valuations[r].PrepaidCollections.Should().BeApproximately(prepay * none.Valuations[r].Ending, 1e-8);
        }

        // A 0% contract prepaid in full pays its remaining face.
        some.PrepaidCollections.Should().BeApproximately(prepay * none.EndingFace, 1e-8);
        some.EndingFace.Should().BeApproximately((1.0 - prepay) * none.EndingFace, 1e-8);
    }

    [Fact]
    public void AWriteOffTakesItsShareOfTheWholeScheduleAndRecoversNothing()
    {
        const double writeOff = 0.02;
        var none = Run(Drivers()).Periods[0];
        var some = Run(Drivers(writeOff: writeOff)).Periods[0];

        some.WriteOffs.Should().BeApproximately(writeOff * none.BeginningFace, 1e-8);
        some.ScheduledDue.Should().BeApproximately((1.0 - writeOff) * none.ScheduledDue, 1e-8);
        some.Collections.Should().BeApproximately((1.0 - writeOff) * none.Collections, 1e-8);
        for (var r = 0; r < Rates.Length; r++)
        {
            some.Valuations[r].Ending.Should().BeApproximately((1.0 - writeOff) * none.Valuations[r].Ending, 1e-8);
            some.Valuations[r].WriteOffs.Should().BeApproximately(
                writeOff * none.Valuations[r].Beginning * (1.0 + Rates[r] / 12.0), 1e-8);
        }
    }

    [Fact]
    public void CurvesReadEachCohortsAge()
    {
        var drivers = new InstallmentDrivers
        {
            Prepayment = new InstallmentCurve(new[] { 0.0, 0.0, 0.0, 0.10 }),
            WriteOff = new InstallmentCurve(new[] { 0.0 })
        };

        // The uneven cohort is 3, so it prepays 10% of what is left after its payment; the new one is 0 and prepays nothing.
        var month = Run(drivers, cohorts: new[] { Opening[1], Level("new", 36_000.0, 36, 0) }).Periods[0];

        month.PrepaidCollections.Should().BeApproximately(0.10 * (Opening[1].Face - 41_000.0), 1e-8);
    }

    [Fact]
    public void AnAnnualRateConvertsAsCprToSmm()
    {
        new InstallmentCurve(new[] { 0.20 }, true).At(0).Should().BeApproximately(1.0 - Math.Pow(0.80, 1.0 / 12.0), 1e-15);
    }

    /// <summary>
    ///     <c>PV_r(t) = PV_r(t − 1) × (1 + r/12) − cash(t) + PV_r(additions) − PV_r(removals)</c>, where
    ///     cash is what fell due and the ending value is taken afresh from the closed pool.
    /// </summary>
    private static void RequireRollForward(InstallmentPoolResult result)
    {
        InstallmentPoolPeriod? prior = null;
        foreach (var month in result.Periods)
        {
            for (var r = 0; r < Rates.Length; r++)
            {
                var v = month.Valuations[r];
                var rolled = v.Beginning * (1.0 + Rates[r] / 12.0) - month.ScheduledDue - v.WriteOffs - v.Repurchases
                             - v.PrepaidCollections - v.UpgradeCollections - v.ScheduledVariance + v.Additions;
                v.Ending.Should().BeApproximately(rolled, 1e-6, $"month {month.Period} at {Rates[r]}");
                if (prior != null)
                    v.Beginning.Should().BeApproximately(prior.Valuations[r].Ending, 1e-6);
            }

            if (prior != null)
                month.BeginningFace.Should().BeApproximately(prior.EndingFace, 1e-6);
            prior = month;
        }
    }

    [Fact]
    public void AForecastRollsForwardAtEveryRate()
    {
        var drivers = new InstallmentDrivers
        {
            Prepayment = new InstallmentCurve(new[] { 0.01, 0.015, 0.02, 0.03 }),
            Upgrade = new InstallmentCurve(new[] { 0.0, 0.0, 0.01, 0.025 }),
            WriteOff = new InstallmentCurve(new[] { 0.12 }, true),
            RepurchaseAmounts = new[] { 0.0, 5_000.0, 2_500.0 },
            RepurchasePrice = new InstallmentPrice { PvRate = 0.09 },
            Reinvest = new InstallmentReinvest
            {
                Share = 0.8,
                Price = new InstallmentPrice { PvRate = 0.0975 },
                Terms = new[] { new InstallmentTerm(36, 0.75), new InstallmentTerm(24, 0.25) }
            }
        };
        var purchases = new[]
        {
            new InstallmentPurchase
            {
                Cohort = Level("bought", 90_000.0, 36, 0), Price = new InstallmentPrice { PerFace = 0.86 }, Period = 4
            }
        };

        var result = Run(drivers, 30, purchases: purchases);

        RequireRollForward(result);
        result.Periods[1].RepurchaseCash.Should().BeGreaterThan(0.0);
        result.Periods[4].AdditionsCash.Should().BeGreaterThan(0.86 * 90_000.0);
    }

    [Fact]
    public void AdditionsAtThePresentValueAtARateCostExactlyThatValue()
    {
        var purchase = new InstallmentPurchase
        {
            Cohort = Level("bought", 36_000.0, 36, 0), Price = new InstallmentPrice { PvRate = 0.09 }, Period = 0
        };

        var month = Run(Drivers(), purchases: new[] { purchase }).Periods[0];

        month.AdditionsFace.Should().BeApproximately(36_000.0, 1e-8);
        month.AdditionsCash.Should().BeApproximately(Annuity(1_000.0, 36, 0.09), 1e-8);
        month.AdditionsCash.Should().BeApproximately(month.Valuations[2].Additions, 1e-8);
    }

    [Fact]
    public void ReinvestingAllCollectionsAtFaceHoldsThePoolLessWriteOffs()
    {
        var drivers = Drivers(0.02, 0.01, 0.003) with
        {
            Reinvest = new InstallmentReinvest
            {
                Share = 1.0,
                Price = new InstallmentPrice { PerFace = 1.0 },
                Terms = new[] { new InstallmentTerm(36, 1.0) }
            }
        };

        foreach (var month in Run(drivers, 24).Periods)
        {
            month.AdditionsCash.Should().BeApproximately(month.Collections, 1e-6);
            month.EndingFace.Should().BeApproximately(month.BeginningFace - month.WriteOffs, 1e-6);
        }
    }

    private static IReadOnlyList<InstallmentActual> Printed => new[]
    {
        new InstallmentActual
        {
            BeginningFace = 865_750.00,
            ScheduledCollections = 70_210.55,
            PrepaidCollections = 12_345.67,
            UpgradeCollections = 4_321.09,
            WriteOffs = 2_222.22,
            Repurchases = 1_000.00,
            RepurchaseCash = 1_000.00,
            Additions = new[]
            {
                new InstallmentPurchase
                {
                    Cohort = Level("added", 80_000.00, 36, 0), Price = new InstallmentPrice { PvRate = 0.0975 }
                }
            },
            EndingFace = 855_650.47
        },
        new InstallmentActual
        {
            ScheduledCollections = 68_000.00,
            PrepaidCollections = 9_876.54,
            WriteOffs = 3_010.10,
            Repurchases = 400.00,
            RepurchasePrice = new InstallmentPrice { PerFace = 0.95 },
            Additions = new[]
            {
                new InstallmentPurchase { Cohort = Level("added", 50_000.00, 36, 0), Cash = 43_250.00 }
            }
        }
    };

    [Fact]
    public void AReplayReproducesThePrintedFaceRollToTheCent()
    {
        var result = Run(null, 2, Printed);

        foreach (var (month, printed) in result.Periods.Zip(Printed))
        {
            var face = month.BeginningFace - printed.ScheduledCollections - printed.PrepaidCollections
                       - printed.UpgradeCollections - printed.WriteOffs - printed.Repurchases
                       + printed.Additions.Sum(a => a.Cohort.Face);
            month.EndingFace.Should().BeApproximately(face, 0.005);
            month.ScheduledCollections.Should().Be(printed.ScheduledCollections);
            month.PrepaidCollections.Should().Be(printed.PrepaidCollections);
            month.WriteOffs.Should().Be(printed.WriteOffs);
            month.Replayed.Should().BeTrue();
        }

        result.Periods[0].RollBreak.Should().BeApproximately(0.0, 0.005);
        result.Periods[0].LedgerBreak.Should().BeApproximately(0.0, 0.005);
        result.Periods[1].RollBreak.Should().BeNull();
        result.Periods[1].RepurchaseCash.Should().BeApproximately(380.0, 1e-9);
        result.Periods[1].AdditionsCash.Should().Be(43_250.00);
        RequireRollForward(result);
    }

    [Fact]
    public void AReplayedMonthPrintingWhatFellDueIsTheForecastRoll()
    {
        var forecast = Run(Drivers(0.03, 0.01, 0.002)).Periods[0];
        var actual = new InstallmentActual
        {
            ScheduledCollections = forecast.ScheduledCollections,
            PrepaidCollections = forecast.PrepaidCollections,
            UpgradeCollections = forecast.UpgradeCollections,
            WriteOffs = forecast.WriteOffs
        };

        var replayed = Run(null, 1, new[] { actual }).Periods[0];

        replayed.ScheduledDue.Should().BeApproximately(forecast.ScheduledDue, 1e-8);
        for (var r = 0; r < Rates.Length; r++)
        {
            replayed.Valuations[r].Ending.Should().BeApproximately(forecast.Valuations[r].Ending, 1e-8);
            replayed.Valuations[r].ScheduledVariance.Should().BeApproximately(0.0, 1e-8);
        }
    }

    [Fact]
    public void AReplayThenForecastsFromWhereTheReplayClosed()
    {
        var result = Run(Drivers(0.02), 6, Printed);

        result.Periods.Select(p => p.Replayed).Should().Equal(true, true, false, false, false, false);
        RequireRollForward(result);
        result.Closing.Sum(c => c.Face).Should().BeApproximately(result.Periods[^1].EndingFace, 1e-6);
        result.Closing.Single(c => c.Name == "uneven").Age.Should().Be(9);
    }

    [Fact]
    public void ARunCutAtItsClosingMatchesOneLongRun()
    {
        var drivers = Drivers(0.02, 0.005, 0.001);
        var whole = Run(drivers, 8);
        var first = Run(drivers, 3);
        var rest = Run(drivers, 5, cohorts: first.Closing);

        for (var r = 0; r < Rates.Length; r++)
            rest.Periods[^1].Valuations[r].Ending.Should().BeApproximately(whole.Periods[^1].Valuations[r].Ending, 1e-6);
    }

    [Theory]
    [InlineData(1000.0, 900.0, 1.11111)]
    [InlineData(1.234565, 1.0, 1.23457)]
    [InlineData(1.234564999, 1.0, 1.23456)]
    [InlineData(25_000_000_000.0, 21_300_000_000.0, 1.17371)]
    [InlineData(100.0, 100.0, 1.0)]
    public void TheDiscountRatioIsFaceOverValueRoundedToFivePlaces(double face, double value, double ratio)
    {
        InstallmentValuationMath.DiscountRatio(face, value).Should().Be(ratio);
    }

    [Fact]
    public void ThereIsNoDiscountRatioWithoutValue()
    {
        var act = () => InstallmentValuationMath.DiscountRatio(100.0, 0.0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(null, PoolModel.Card)]
    [InlineData("CARD", PoolModel.Card)]
    [InlineData(" installment ", PoolModel.Installment)]
    public void APoolModelIsCardUnlessItSaysOtherwise(string? model, PoolModel parsed)
    {
        PoolModelParser.Parse(model).Should().Be(parsed);
    }

    [Fact]
    public void AnUnknownPoolModelIsRefusedByName()
    {
        var act = () => PoolModelParser.Parse("LEASE");
        act.Should().Throw<RevolvingConfigException>().WithMessage("*'LEASE'*CARD and INSTALLMENT*");
    }

    public static IEnumerable<object[]> BadSchedules => new[]
    {
        new object[] { new[] { 100.0, -1.0 }, "*schedule[1]*" },
        new object[] { Array.Empty<double>(), "*no payments*" },
        new object[] { new[] { 100.0, double.NaN }, "*schedule[1]*" },
        new object[] { new[] { 0.0, 0.0 }, "*pays nothing*" }
    };

    [Theory]
    [MemberData(nameof(BadSchedules))]
    public void ABadScheduleIsRefused(double[] schedule, string message)
    {
        var act = () => Run(Drivers(), cohorts: new[] { new InstallmentCohort { Age = 0, Schedule = schedule } });
        act.Should().Throw<RevolvingConfigException>().WithMessage(message);
    }

    [Fact]
    public void PrepaymentsAndUpgradesCannotTakeMoreThanTheSchedule()
    {
        var act = () => Run(Drivers(0.7, 0.4));
        act.Should().Throw<RevolvingConfigException>().WithMessage("*prepayments and upgrades*");
    }

    [Fact]
    public void ARateOutsideZeroToOneHundredPercentIsRefused()
    {
        var act = () => Run(Drivers(writeOff: 1.5));
        act.Should().Throw<RevolvingConfigException>().WithMessage("*writeOffRate*");
    }

    [Fact]
    public void APurchaseStatesAPriceOrItsCashNotBoth()
    {
        var purchase = new InstallmentPurchase
        {
            Cohort = Level("bought", 36_000.0, 36, 0), Price = new InstallmentPrice { PerFace = 0.9 }, Cash = 1.0,
            Period = 0
        };

        var act = () => Run(Drivers(), purchases: new[] { purchase });
        act.Should().Throw<RevolvingConfigException>().WithMessage("*price or its cash*");
    }

    [Fact]
    public void AForecastPurchaseInAReplayedMonthIsRefused()
    {
        var purchase = new InstallmentPurchase
        {
            Cohort = Level("bought", 36_000.0, 36, 0), Price = new InstallmentPrice { PerFace = 0.9 }, Period = 0
        };

        var act = () => Run(Drivers(), 3, Printed, new[] { purchase });
        act.Should().Throw<RevolvingConfigException>().WithMessage("*not a forecast month*");
    }

    [Fact]
    public void AReplayThatTakesMoreThanTheScheduleIsRefused()
    {
        var actual = new InstallmentActual { ScheduledCollections = 2_000_000.0, PrepaidCollections = 0.0, WriteOffs = 0.0 };

        var act = () => Run(null, 1, new[] { actual });
        act.Should().Throw<RevolvingConfigException>().WithMessage("*cannot lose that much*");
    }

    [Fact]
    public void ADuplicateOrNegativeDiscountRateIsRefused()
    {
        var run = new InstallmentPoolRun
        {
            StatementDate = Statement, Periods = 1, Cohorts = Opening, Drivers = Drivers(),
            DiscountRates = new[] { 0.09, 0.09 }
        };

        var twice = () => InstallmentPoolLedger.Run(run);
        twice.Should().Throw<RevolvingConfigException>().WithMessage("*twice*");
        var negative = () => InstallmentPoolLedger.Run(run with { DiscountRates = new[] { -0.01 } });
        negative.Should().Throw<RevolvingConfigException>().WithMessage("*at least 0%*");
    }

    [Fact]
    public void ForecastMonthsWithoutDriversAreRefused()
    {
        var act = () => Run(null, 3, Printed);
        act.Should().Throw<RevolvingConfigException>().WithMessage("*no drivers*");
    }
}
