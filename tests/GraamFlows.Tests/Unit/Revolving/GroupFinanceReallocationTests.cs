using FluentAssertions;
using GraamFlows.Revolving;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>
///     Reallocation across a group, against the American Express Credit Account Master Trust
///     statements for the August and September 2026 monthly periods: Group I, every series, printed
///     Reallocated Investor Finance Charge Collections and Available Excess.
/// </summary>
public class GroupFinanceReallocationTests
{
    private const double OneCent = 0.01;

    /// <summary>The trust rounds fees per class, and this engine does not round, so a group can differ by cents.</summary>
    private const double TwoCents = 0.02;

    /// <param name="Reallocated">printed Reallocated Investor Finance Charge Collections.</param>
    /// <param name="Excess">printed Available Excess.</param>
    private sealed record Printed(
        string Name,
        double A,
        double B,
        double C,
        double InterestA,
        double InterestB,
        double InterestC,
        double Reallocated,
        double Excess);

    private static readonly Printed[] September =
    {
        new("2023-2", 500_000_000.00, 21_429_000.00, 50_001_000.00, 2_000_000.00, 91_430.40, 283_339.00, 16_187_359.66, 12_042_460.88),
        new("2023-4", 850_000_000.00, 36_430_000.00, 85_003_000.00, 3_647_916.67, 165_756.50, 505_484.51, 27_800_609.49, 20_472_225.64),
        new("2024-1", 1_000_000_000.00, 42_858_000.00, 100_001_000.00, 4_358_333.33, 196_432.50, 593_755.94, 32_773_678.13, 24_084_900.68),
        new("2024-2", 1_000_000_000.00, 42_858_000.00, 100_001_000.00, 4_366_666.67, 196_789.65, 603_756.04, 32_792_368.72, 24_084_900.68),
        new("2024-3", 1_200_000_000.00, 51_430_000.00, 120_003_000.00, 4_650_000.00, 217_720.33, 655_016.38, 38_672_977.53, 28_901_927.18),
        new("2025-1", 1_400_000_000.00, 60_000_000.00, 140_000_000.00, 5_320_000.00, 248_000.00, 701_750.00, 44_944_906.06, 33_718_806.17),
        new("2025-2", 1_500_000_000.00, 64_286_000.00, 150_001_000.00, 5_350_000.00, 245_893.95, 773_255.16, 47_806_847.40, 36_127_319.42),
        new("2025-3", 1_000_000_000.00, 42_858_000.00, 100_001_000.00, 3_758_333.33, 172_503.45, 542_922.10, 32_098_915.24, 24_084_900.68),
        new("2025-4", 1_500_000_000.00, 64_286_000.00, 150_001_000.00, 5_375_000.00, 251_251.12, 780_255.20, 47_844_204.61, 36_127_319.42),
        new("2025-5", 1_000_000_000.00, 42_858_000.00, 100_001_000.00, 3_758_333.33, 175_003.50, 542_672.09, 32_101_165.28, 24_084_900.68),
        new("2026-1", 1_250_000_000.00, 53_572_000.00, 125_001_000.00, 3_985_069.44, 184_481.13, 558_153.77, 39_259_131.67, 30_106_110.05)
    };

    private static readonly Printed[] August =
    {
        new("2023-2", 500_000_000.00, 21_429_000.00, 50_001_000.00, 2_000_000.00, 91_430.40, 283_339.00, 16_310_945.18, 12_177_585.54),
        new("2023-3", 850_000_000.00, 36_430_000.00, 85_003_000.00, 3_704_583.33, 167_578.00, 495_142.48, 28_058_851.41, 20_701_938.03),
        new("2023-4", 850_000_000.00, 36_430_000.00, 85_003_000.00, 3_647_916.67, 165_756.50, 505_484.51, 28_010_705.28, 20_701_938.03),
        new("2024-1", 1_000_000_000.00, 42_858_000.00, 100_001_000.00, 4_358_333.33, 196_432.50, 593_755.94, 33_020_848.94, 24_355_149.76),
        new("2024-2", 1_000_000_000.00, 42_858_000.00, 100_001_000.00, 4_366_666.67, 196_789.65, 603_756.04, 33_039_539.53, 24_355_149.76),
        new("2024-3", 1_200_000_000.00, 51_430_000.00, 120_003_000.00, 4_650_000.00, 217_720.33, 655_016.38, 38_969_582.97, 29_226_226.60),
        new("2025-1", 1_400_000_000.00, 60_000_000.00, 140_000_000.00, 5_320_000.00, 248_000.00, 701_750.00, 45_290_944.62, 34_097_154.26),
        new("2025-2", 1_500_000_000.00, 64_286_000.00, 150_001_000.00, 5_350_000.00, 245_893.95, 773_255.16, 48_177_603.28, 36_532_692.68),
        new("2025-3", 1_000_000_000.00, 42_858_000.00, 100_001_000.00, 3_758_333.33, 172_503.45, 542_922.10, 32_346_086.05, 24_355_149.76),
        new("2025-4", 1_500_000_000.00, 64_286_000.00, 150_001_000.00, 5_375_000.00, 251_251.12, 780_255.20, 48_214_960.49, 36_532_692.68),
        new("2025-5", 1_000_000_000.00, 42_858_000.00, 100_001_000.00, 3_758_333.33, 175_003.50, 542_672.09, 32_348_336.09, 24_355_149.76)
    };

    /// <summary>Interest is an input here, so each coupon is what makes the printed interest.</summary>
    private static RevolvingSeries ToSeries(Printed p)
    {
        return CardProgram.Series(p.Name, p.A, p.B, p.C,
            p.InterestA / p.A * 1200.0, p.InterestB / p.B * 1200.0, p.InterestC / p.C * 1200.0);
    }

    private static RevolvingProgram Reallocating(PrincipalFreeze freeze = PrincipalFreeze.FrozenPercent)
    {
        return CardProgram.Program(freeze: freeze) with
        {
            GroupSteps = new WaterfallStep[] { new(WaterfallVerb.ReallocateGroupFinance) }
        };
    }

    private static RevolvingResult RunSeptember(RevolvingProgram program)
    {
        return CardProgram.Execute(CardProgram.Run(program, 24_975_208_451.43, September.Select(ToSeries).ToList(),
            null, 1, new DateTime(2026, 8, 31)) with
        {
            ActualPeriods = new[]
            {
                new ActualPeriod
                {
                    NewReceivables = 12_498_514_556.18,
                    PrincipalCollections = 12_712_741_903.27,
                    FinanceCollections = 687_263_527.44,
                    Recoveries = 15_412_642.37,
                    ChargeOffs = 35_740_822.51
                }
            }
        });
    }

    private static RevolvingResult RunAugust()
    {
        var series = August.Select(ToSeries).ToList();

        // Series 2012-A is the only series in Group 2, so it keeps its own finance.
        series.Add(CardProgram.Series("2012-A", 1_000_000.00, 52_000.00, 98_000.00,
            3_331.02 / 1_000_000.00 * 1200.0, 197.26 / 52_000.00 * 1200.0, 476.50 / 98_000.00 * 1200.0, "2"));

        return CardProgram.Execute(CardProgram.Run(Reallocating(), 24_930_085_370.00, series, null, 1,
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
        });
    }

    private static SeriesPeriod Month(RevolvingResult result, string name)
    {
        return result.Series.Single(s => s.Name == name).Periods[0];
    }

    private static double PrintedStyleExcessPct(SeriesPeriod month)
    {
        return month.ExcessDollars * 12.0 / month.InvestedAmount * 100.0;
    }

    [Fact]
    public void Series2024Dash1ReproducesItsPrintedSeptember()
    {
        var month = Month(RunSeptember(Reallocating()), "2024-1");

        month.FinanceAllocation.Should().BeApproximately(32_154_277.57, OneCent);
        month.ReallocatedFinance!.Value.Should().BeApproximately(32_773_678.13, OneCent);
        month.ExcessDollars.Should().BeApproximately(24_084_900.68, OneCent);
        Math.Round(PrintedStyleExcessPct(month), 4).Should().Be(25.2891);
        Math.Round(month.PortfolioYieldPct, 4).Should().Be(33.1491);
        Math.Round(month.BaseRatePct, 4).Should().Be(7.5088);
    }

    [Fact]
    public void EverySeriesReproducesItsPrintedSeptemberReallocationAndExcess()
    {
        var result = RunSeptember(Reallocating());

        foreach (var printed in September)
        {
            var month = Month(result, printed.Name);
            month.ReallocatedFinance!.Value.Should().BeApproximately(printed.Reallocated, TwoCents, printed.Name);
            month.ExcessDollars.Should().BeApproximately(printed.Excess, TwoCents, printed.Name);
            Math.Round(PrintedStyleExcessPct(month), 4).Should().Be(25.2891, printed.Name);
        }
    }

    [Fact]
    public void EverySeriesReproducesItsPrintedAugustReallocationAndExcess()
    {
        var result = RunAugust();

        foreach (var printed in August)
        {
            var month = Month(result, printed.Name);
            month.ReallocatedFinance!.Value.Should().BeApproximately(printed.Reallocated, TwoCents, printed.Name);
            month.ExcessDollars.Should().BeApproximately(printed.Excess, TwoCents, printed.Name);
            Math.Round(PrintedStyleExcessPct(month), 4).Should().Be(25.5729, printed.Name);
        }
    }

    [Fact]
    public void AGroupOfOneKeepsItsOwnFinanceAndItsPrintedExcess()
    {
        var month = Month(RunAugust(), "2012-A");

        month.ReallocatedFinance!.Value.Should().BeApproximately(32_727.66, OneCent);
        month.ExcessDollars.Should().BeApproximately(25_183.72, OneCent);
        Math.Round(PrintedStyleExcessPct(month), 4).Should().Be(26.2787);
    }

    [Theory]
    [InlineData("2023-2", 32.7244, 7.0840)]
    [InlineData("2023-4", 33.0776, 7.4373)]
    [InlineData("2024-2", 33.1690, 7.5287)]
    [InlineData("2024-3", 32.5676, 6.9273)]
    [InlineData("2025-1", 32.4357, 6.7954)]
    [InlineData("2025-2", 32.1884, 6.5481)]
    [InlineData("2025-3", 32.4308, 6.7905)]
    [InlineData("2025-4", 32.2150, 6.5746)]
    [InlineData("2025-5", 32.4332, 6.7929)]
    public void SeriesYieldAndBaseRateReproduceTheirPrintedSeptemberRates(string series, double yieldPct, double basePct)
    {
        var month = Month(RunSeptember(Reallocating()), series);

        Math.Round(month.PortfolioYieldPct, 4).Should().Be(yieldPct);
        Math.Round(month.BaseRatePct, 4).Should().Be(basePct);
    }

    [Fact]
    public void ANewSeriesYieldReproducesItsPrintedFirstMonth()
    {
        var month = Month(RunSeptember(Reallocating()), "2026-1");

        // Its 7.2796% base rate comes from its closing date: see FirstPeriodTests.
        Math.Round(month.PortfolioYieldPct, 4).Should().Be(31.6945);
    }

    [Fact]
    public void WithoutTheStepASeriesKeepsItsOwnFinanceAndMissesThePrintedExcess()
    {
        var month = Month(RunSeptember(CardProgram.Program()), "2024-1");

        month.ReallocatedFinance.Should().BeNull();
        month.ExcessDollars.Should().BeApproximately(32_154_277.57 - 8_688_777.45, 0.02);
        Math.Round(PrintedStyleExcessPct(month), 2).Should().Be(24.64);
    }

    [Fact]
    public void AProgramThatDoesNotUseTheStepRunsExactlyAsBefore()
    {
        var series = new[]
        {
            CardProgram.Series("High", 300_000.0, 30_000.0, 30_000.0, 12.0),
            CardProgram.Series("Low", 300_000.0, 30_000.0, 30_000.0, 2.0)
        };
        var plain = CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 1_500_000.0, series,
            CardProgram.Drivers(), 6));
        var withEmptyList = CardProgram.Execute(CardProgram.Run(
            CardProgram.Program() with { GroupSteps = Array.Empty<WaterfallStep>() }, 1_500_000.0, series,
            CardProgram.Drivers(), 6));

        plain.Series[0].Periods.Should().BeEquivalentTo(withEmptyList.Series[0].Periods);
        plain.Series[0].Periods.Should().OnlyContain(p => p.ReallocatedFinance == null);
        plain.Series[0].Periods.Should().OnlyContain(p => p.FinanceAllocation > 0);
    }

    private static RevolvingResult RunTwo(RevolvingSeries one, RevolvingSeries two, double yield,
        double chargeOff = 0.0, double pool = 1_000_000.0)
    {
        return CardProgram.Execute(CardProgram.Run(Reallocating(), pool, new[] { one, two },
            CardProgram.Drivers(0.40, 0.40, yield, chargeOff, 0.0), 1));
    }

    private static RevolvingSeries Plain(string name, double a, double coupon, double writtenDown = 0.0,
        string group = "1")
    {
        var series = CardProgram.Series(name, a, 0.0, 0.0, coupon, group: group) with
        {
            Fees = Array.Empty<RevolvingFee>()
        };
        return series with
        {
            Classes = series.Classes.Select(c => c.Key == "A" ? c with { WrittenDown = writtenDown } : c).ToList()
        };
    }

    [Fact]
    public void AFirstPayTwoMonthsOutClaimsItsInterestOnceInTheGroup()
    {
        // Each series is allocated 10,000 a month. Two carries its first month, so 30,000 reaches
        // the group in November against interest of 5,000 and Two's 60 days of 10,000. That leaves
        // 15,000 to split evenly. Counting Two's first month as well would claim 15,000 and move
        // 2,500 between the series.
        var two = Plain("Two", 1_000_000.0, 6.0) with
        {
            ClosingDate = new DateTime(2026, 9, 15),
            FirstPayDate = new DateTime(2026, 11, 15)
        };
        var run = CardProgram.Run(Reallocating(), 4_000_000.0, new[] { Plain("One", 1_000_000.0, 6.0), two },
            CardProgram.Drivers(0.40, 0.40, 0.01, 0.0, 0.0), 2, new DateTime(2026, 8, 31));

        var result = CardProgram.Execute(run);

        var november = result.Series.ToDictionary(s => s.Name, s => s.Periods[1]);
        november["One"].ReallocatedFinance!.Value.Should().BeApproximately(5_000.0 + 7_500.0, 1e-6);
        november["Two"].ReallocatedFinance!.Value.Should().BeApproximately(10_000.0 + 7_500.0, 1e-6);
    }

    [Fact]
    public void TheBalanceGoesProRataToInvestedAmountsNotShortfalls()
    {
        // 6,000 in the pool. Claims are 1,000 and 500 of interest, so 4,500 is left.
        var result = RunTwo(Plain("One", 100_000.0, 12.0), Plain("Two", 200_000.0, 3.0), 0.02);

        Month(result, "One").ReallocatedFinance!.Value.Should().BeApproximately(1_000.0 + 4_500.0 / 3.0, 1e-6);
        Month(result, "Two").ReallocatedFinance!.Value.Should().BeApproximately(500.0 + 4_500.0 * 2.0 / 3.0, 1e-6);
    }

    [Fact]
    public void TheBalanceFollowsInvestedAmountsEvenWhenAFundingAccountCutsASeriesAllocation()
    {
        // One holds 50,000 in its account, so it is allocated half of what Two is. Its invested
        // amount is still 100,000, so the balance splits evenly.
        var one = Plain("One", 100_000.0, 0.0) with
        {
            Phase = SeriesPhase.Accumulating,
            FrozenPrincipalPct = 0.10,
            FundingAccount = 50_000.0,
            FundedClasses = new[] { "A" },
            ExpectedDate = new DateTime(2028, 1, 15),
            ControlledAmounts = new[] { 0.0 }
        };

        var result = RunTwo(one, Plain("Two", 100_000.0, 0.0), 0.02);

        Month(result, "One").FinanceAllocation.Should().BeApproximately(1_000.0, 1e-6);
        Month(result, "Two").FinanceAllocation.Should().BeApproximately(2_000.0, 1e-6);
        Month(result, "One").ReallocatedFinance!.Value.Should().BeApproximately(1_500.0, 1e-6);
        Month(result, "Two").ReallocatedFinance!.Value.Should().BeApproximately(1_500.0, 1e-6);
    }

    [Fact]
    public void ShortInterestIsSharedProRataToTheInterestClaims()
    {
        // 900 in the pool against interest of 2,000 and 1,000, so a claim gets 30 cents on the dollar.
        var result = RunTwo(Plain("One", 100_000.0, 24.0), Plain("Two", 200_000.0, 6.0), 0.003);

        Month(result, "One").ReallocatedFinance!.Value.Should().BeApproximately(600.0, 1e-6);
        Month(result, "Two").ReallocatedFinance!.Value.Should().BeApproximately(300.0, 1e-6);
        Month(result, "One").ExcessDollars.Should().Be(0.0);
    }

    [Fact]
    public void InterestComesBeforeDefaultsWhichAreSharedProRataWhenShort()
    {
        // Interest of 1,000 and 500. Defaults of 1,000 and 2,000. 3,000 in the pool leaves 1,500.
        var result = RunTwo(Plain("One", 100_000.0, 12.0), Plain("Two", 200_000.0, 3.0), 0.01, 0.01);

        Month(result, "One").InvestorDefault.Should().BeApproximately(1_000.0, 1e-6);
        Month(result, "Two").InvestorDefault.Should().BeApproximately(2_000.0, 1e-6);
        Month(result, "One").ReallocatedFinance!.Value.Should().BeApproximately(1_000.0 + 500.0, 1e-6);
        Month(result, "Two").ReallocatedFinance!.Value.Should().BeApproximately(500.0 + 1_000.0, 1e-6);
    }

    [Fact]
    public void FeesComeAfterDefaultsAndBeforeTheBalance()
    {
        var one = Plain("One", 120_000.0, 0.0) with { Fees = new[] { new RevolvingFee("servicing", 12.0) } };
        var two = Plain("Two", 120_000.0, 0.0);

        // Fees of 1,200 for One against 2,400 in the pool, shared after that by equal invested amounts.
        var result = RunTwo(one, two, 0.01, pool: 240_000.0);

        Month(result, "One").ReallocatedFinance!.Value.Should().BeApproximately(1_200.0 + 600.0, 1e-6);
        Month(result, "Two").ReallocatedFinance!.Value.Should().BeApproximately(600.0, 1e-6);
    }

    [Fact]
    public void DefaultsTakeThePoolBeforeFees()
    {
        var one = Plain("One", 120_000.0, 0.0) with { Fees = new[] { new RevolvingFee("servicing", 12.0) } };
        var two = Plain("Two", 120_000.0, 0.0);

        // 2,400 in the pool against 2,400 of defaults, so One's fee of 1,200 gets nothing.
        var result = RunTwo(one, two, 0.01, 0.01, 240_000.0);

        Month(result, "One").ReallocatedFinance!.Value.Should().BeApproximately(1_200.0, 1e-6);
        Month(result, "Two").ReallocatedFinance!.Value.Should().BeApproximately(1_200.0, 1e-6);
    }

    [Fact]
    public void UnreimbursedWriteDownsAreAClaimBeforeTheBalance()
    {
        // One has 1,000 written down. Its invested amount is 99,000, so Two's share of 100,000 is larger.
        var one = Plain("One", 100_000.0, 0.0, 1_000.0);
        var two = Plain("Two", 100_000.0, 0.0);

        var result = RunTwo(one, two, 0.02);
        var first = Month(result, "One");
        var second = Month(result, "Two");

        (first.ReallocatedFinance!.Value + second.ReallocatedFinance!.Value).Should()
            .BeApproximately(first.FinanceAllocation + second.FinanceAllocation, 1e-6);
        var balance = first.FinanceAllocation + second.FinanceAllocation - 1_000.0;
        first.ReallocatedFinance.Value.Should().BeApproximately(1_000.0 + balance * 99_000.0 / 199_000.0, 1e-6);
        result.Series[0].Classes["A"][0].Writedown.Should().BeApproximately(-1_000.0, 1e-6);
    }

    [Fact]
    public void SeriesInDifferentGroupsDoNotReallocateToEachOther()
    {
        var result = RunTwo(Plain("One", 100_000.0, 24.0, group: "1"), Plain("Two", 100_000.0, 1.0, group: "2"), 0.01);

        Month(result, "One").ReallocatedFinance!.Value.Should().BeApproximately(Month(result, "One").FinanceAllocation, 1e-9);
        Month(result, "Two").ReallocatedFinance!.Value.Should().BeApproximately(Month(result, "Two").FinanceAllocation, 1e-9);
    }

    [Fact]
    public void FundingEarningsStayWithTheirOwnSeries()
    {
        var accumulating = Plain("One", 100_000.0, 0.0) with
        {
            Phase = SeriesPhase.Accumulating,
            FrozenPrincipalPct = 0.10,
            FundingAccount = 60_000.0,
            FundedClasses = new[] { "A" },
            ExpectedDate = new DateTime(2028, 1, 15),
            ControlledAmounts = new[] { 0.0 }
        };
        var run = CardProgram.Run(Reallocating(), 1_000_000.0, new[] { accumulating, Plain("Two", 100_000.0, 0.0) },
            CardProgram.Drivers(0.40, 0.40, 0.01, 0.0, 0.0), 1) with
        {
            FundingEarningsRate = new[] { 12.0 }
        };

        var one = Month(CardProgram.Execute(run), "One");

        // One earns 600, which is not pooled. It is part of the excess it leaves behind.
        one.FundingEarnings.Should().BeApproximately(600.0, 1e-6);
        one.ExcessDollars.Should().BeApproximately(one.ReallocatedFinance!.Value + 600.0, 1e-6);
    }

    [Fact]
    public void ASeriesBeforeItsFirstPayDateKeepsCollectingOutsideTheGroup()
    {
        var late = Plain("Late", 100_000.0, 12.0) with { FirstPayDate = new DateTime(2026, 11, 15) };
        var run = CardProgram.Run(Reallocating(), 1_000_000.0, new[] { late, Plain("Now", 100_000.0, 12.0) },
            CardProgram.Drivers(0.40, 0.40, 0.02, 0.0, 0.0), 4);

        var result = CardProgram.Execute(run);
        var series = result.Series.Single(s => s.Name == "Late");

        series.Periods[0].ReallocatedFinance.Should().BeNull();
        series.Periods[0].CarriedFinance.Should().BeApproximately(2_000.0, 1e-6);
        series.Periods[2].ReallocatedFinance.Should().NotBeNull();
        series.Periods[2].CarriedFinance.Should().Be(0.0);
    }

    [Fact]
    public void AGroupVerbInASeriesWaterfallIsRefused()
    {
        var program = CardProgram.Program() with
        {
            FinanceSteps = new WaterfallStep[]
            {
                new(WaterfallVerb.ReallocateGroupFinance), new(WaterfallVerb.ReleaseTransferor)
            }
        };

        var refuse = () => CardProgram.Execute(CardProgram.Run(program, 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, CardProgram.Drivers(), 1));

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*cannot run*");
    }

    [Fact]
    public void ASeriesVerbInTheGroupStepsIsRefused()
    {
        var program = CardProgram.Program() with
        {
            GroupSteps = new WaterfallStep[] { new(WaterfallVerb.ShareExcess) }
        };

        var refuse = () => CardProgram.Execute(CardProgram.Run(program, 1_000_000.0,
            new[] { CardProgram.Series("S", 300_000.0, 50_000.0, 50_000.0) }, CardProgram.Drivers(), 1));

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*group steps*cannot run*");
    }
}
