using FluentAssertions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Revolving;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>
///     A series' first distribution pays interest from its closing date. Figures are from the American
///     Express Credit Account Master Trust certificates: Series 2026-1 closed Sep 22, 2026 and first
///     paid Oct 15; Series 2025-1 closed Feb 11, 2025 and first paid Mar 17, a rolled date; Series
///     2023-3 closed Sep 19, 2023 and first paid Oct 16, another.
/// </summary>
public class FirstPeriodTests
{
    private const double OneCent = 0.01;

    private static RevolvingSeries Series(string name, double a, double b, double c, double couponA, double couponB,
        double couponC, DateTime? closing, DateTime first)
    {
        return CardProgram.Series(name, a, b, c, couponA, couponB, couponC) with
        {
            ClosingDate = closing,
            FirstPayDate = first
        };
    }

    private static SeriesResult Run(RevolvingSeries series, DateTime statementDate, int periods = 2)
    {
        var run = CardProgram.Run(CardProgram.Program(), 25_000_000_000.0, new[] { series },
            CardProgram.Drivers(), periods, statementDate);
        return CardProgram.Execute(run).Series.Single();
    }

    private static double Accrued(SeriesResult series, string key, int period = 0)
    {
        return series.Classes[key][period].InterestAccrued;
    }

    private static SeriesResult Series2026() =>
        Run(Series("2026-1", 1_250_000_000.0, 53_572_000.0, 125_001_000.0, 4.99, 5.39, 6.989,
            new DateTime(2026, 9, 22), new DateTime(2026, 10, 15)), new DateTime(2026, 8, 31));

    private static RevolvingResult JoinsLater(DateTime closing, DateTime firstPay) =>
        CardProgram.Execute(CardProgram.Run(CardProgram.Program(), 2_000.0,
            new[]
            {
                CardProgram.Series("Old", 500.0, 50.0, 50.0),
                CardProgram.Series("Late", 500.0, 50.0, 50.0) with { ClosingDate = closing, FirstPayDate = firstPay }
            },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 5));

    [Fact]
    public void ASeriesTakesNothingBeforeTheMonthItCloses()
    {
        // It closes Nov 20, so August, September and October are not its months. November is.
        var result = JoinsLater(new DateTime(2026, 11, 20), new DateTime(2026, 12, 15));

        var late = result.Series.Single(s => s.Name == "Late");
        late.Periods.Take(3).Should().AllSatisfy(p =>
        {
            p.FinanceAllocation.Should().Be(0.0);
            p.PrincipalAllocation.Should().Be(0.0);
            p.CarriedFinance.Should().Be(0.0);
            p.FeesAccrued.Should().Be(0.0);
            p.FloatingPct.Should().Be(0.0);
        });
        late.Periods[3].FinanceAllocation.Should().BeGreaterThan(0.0);
        late.Periods[3].FeesAccrued.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void TheTransferorHoldsAmountsOfASeriesNotYetIssued()
    {
        var result = JoinsLater(new DateTime(2026, 11, 20), new DateTime(2026, 12, 15));

        result.Periods[0].TransferorAmount.Should().BeApproximately(2_000.0 - 600.0, 1e-6);
        result.Periods[3].TransferorAmount.Should().BeApproximately(2_000.0 - 1_200.0, 1e-6);
    }

    [Fact]
    public void ASeriesNotYetIssuedLeavesTheOthersTheirShare()
    {
        var result = JoinsLater(new DateTime(2026, 11, 20), new DateTime(2026, 12, 15));

        result.Series.Single(s => s.Name == "Old").Periods[0].FloatingPct.Should().BeApproximately(30.0, 1e-9);
    }

    [Fact]
    public void ASeriesPayingBeforeItsClosingMonthEndsStillHasCash()
    {
        // Closed Nov 2 and paying Nov 16, on October's collections: its first waterfall is not empty.
        var result = JoinsLater(new DateTime(2026, 11, 2), new DateTime(2026, 11, 15));

        var late = result.Series.Single(s => s.Name == "Late");
        late.Periods[1].FinanceAllocation.Should().Be(0.0);
        late.Periods[2].FinanceAllocation.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void ASeriesClosingInsideTheFirstMonthCountsFromIt()
    {
        var result = JoinsLater(new DateTime(2026, 8, 30), new DateTime(2026, 10, 15));

        result.Series.Single(s => s.Name == "Late").Periods[0].FinanceAllocation.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void AShortFirstPeriodPaysFromClosing()
    {
        var series = Series2026();

        Accrued(series, "A").Should().BeApproximately(3_985_069.44, OneCent);
        Accrued(series, "B").Should().BeApproximately(184_481.13, OneCent);
        Accrued(series, "C").Should().BeApproximately(558_153.77, OneCent);
        series.Classes["A"][0].AccrualDays.Should().Be(23);
    }

    [Fact]
    public void ALongFirstPeriodPaysFromClosingAndCountsToTheStatedDay()
    {
        // Feb 11 to Mar 15 is 34 days on 30/360, and the distribution rolled to Mar 17.
        var series = Run(Series("2025-1", 1_400_000_000.0, 60_000_000.0, 140_000_000.0, 4.56, 4.96, 6.015,
            new DateTime(2025, 2, 11), new DateTime(2025, 3, 15)), new DateTime(2025, 1, 31));

        series.Periods[0].DistributionDate.Should().Be(new DateTime(2025, 3, 17));
        Accrued(series, "A").Should().BeApproximately(1_400_000_000.0 * 0.0456 * 34.0 / 360.0, OneCent);
    }

    [Fact]
    public void TheBaseRateAnnualizesInterestOnItsActualDaysAndFeesOnTheInterestPeriod()
    {
        Math.Round(Series2026().Periods[0].BaseRatePct, 4).Should().Be(7.2796);

        // The 30/360 count is 26 days and the actual count is 27, with a 31-day interest period.
        var rolled = Run(Series("2023-3", 850_000_000.0, 36_430_000.0, 85_003_000.0, 5.23, 5.52, 6.99,
            new DateTime(2023, 9, 19), new DateTime(2023, 10, 15)), new DateTime(2023, 8, 31));
        Accrued(rolled, "A").Should().BeApproximately(3_210_638.89, OneCent);
        Math.Round(rolled.Periods[0].BaseRatePct, 4).Should().Be(7.2296);
    }

    [Fact]
    public void TheNextMonthIsAWholeMonthAgain()
    {
        var series = Series2026();

        Accrued(series, "A", 1).Should().BeApproximately(1_250_000_000.0 * 0.0499 / 12.0, OneCent);
        series.Classes["A"][1].AccrualDays.Should()
            .Be((series.Periods[1].DistributionDate - series.Periods[0].DistributionDate).Days);
    }

    [Fact]
    public void ActualThreeSixtyCountsToTheRolledDate()
    {
        var plain = CardProgram.Series("Floater", 1_000_000.0, 0.0, 0.0) with
        {
            Classes = new[]
            {
                new RevolvingClass { Key = "A", Balance = 1_000_000.0, Coupon = 3.6, Basis = AccrualBasis.Actual360 },
                new RevolvingClass { Key = "B", Balance = 0.0, Coupon = 0.0 },
                new RevolvingClass { Key = "C", Balance = 0.0, Coupon = 0.0 }
            },
            ClosingDate = new DateTime(2023, 9, 19),
            FirstPayDate = new DateTime(2023, 10, 15)
        };

        var series = Run(plain, new DateTime(2023, 8, 31), 1);

        Accrued(series, "A").Should().BeApproximately(1_000_000.0 * 0.036 * 27.0 / 360.0, OneCent);
    }

    /// <summary>$1mm at 6%, closing Sep 15, first paying Nov 15, on a statement of Aug 31.</summary>
    private static SeriesResult TwoMonthsOut(AccrualBasis basis = AccrualBasis.Thirty360, int periods = 2,
        IReadOnlyList<TriggerTest>? triggers = null)
    {
        var plain = CardProgram.Series("Stub", 1_000_000.0, 0.0, 0.0, 6.0);
        var series = plain with
        {
            Classes = plain.Classes.Select(c => c with { Basis = basis }).ToList(),
            Fees = Array.Empty<RevolvingFee>(),
            ClosingDate = new DateTime(2026, 9, 15),
            FirstPayDate = new DateTime(2026, 11, 15)
        };
        var run = CardProgram.Run(CardProgram.Program(triggers: triggers), 25_000_000.0, new[] { series },
            CardProgram.Drivers(), periods, new DateTime(2026, 8, 31));
        return CardProgram.Execute(run).Series.Single();
    }

    [Fact]
    public void AFirstPayTwoMonthsOutPaysItsDaysOnce()
    {
        var series = TwoMonthsOut();

        Accrued(series, "A", 0).Should().Be(0.0);
        Accrued(series, "A", 1).Should().BeApproximately(10_000.0, OneCent);
        series.Classes["A"].Sum(row => row.InterestPaid).Should().BeApproximately(10_000.0, OneCent);
        series.Classes["A"][1].InterestDueUnpaid.Should().BeApproximately(0.0, OneCent);
        series.Classes["A"][1].AccrualDays.Should().Be(62);
    }

    [Fact]
    public void AFirstPayTwoMonthsOutOnActualThreeSixtyPaysItsDaysOnce()
    {
        // Sep 15 to Nov 16, the rolled date, is 62 days.
        var series = TwoMonthsOut(AccrualBasis.Actual360);

        series.Classes["A"].Sum(row => row.InterestPaid)
            .Should().BeApproximately(1_000_000.0 * 0.06 * 62.0 / 360.0, OneCent);
    }

    [Fact]
    public void AMonthBeforeTheFirstPeriodReadsItsRateAndReportsNoInterestDays()
    {
        var row = TwoMonthsOut().Classes["A"][0];

        row.CouponPct.Should().Be(6.0);
        row.AccrualDays.Should().Be(0);
    }

    [Fact]
    public void AMonthBeforeTheFirstPeriodHasNoRate()
    {
        // No interest days: this must not divide by zero.
        TwoMonthsOut().Periods[0].BaseRatePct.Should().Be(0.0);
    }

    [Fact]
    public void AMonthBeforeTheFirstPeriodIsNotInTheTriggerHistory()
    {
        var test = new TriggerTest
        {
            Name = "BASE_RATE",
            Metric = TriggerMetric.BaseRate,
            Window = 1,
            Comparison = TriggerComparison.Above,
            Level = 50.0,
            Action = TriggerAction.EarlyAmortization
        };

        var series = TwoMonthsOut(periods: 3, triggers: new[] { test });

        series.Periods[1].Triggers[0].Evaluated.Should().BeFalse();
        series.Periods[2].Triggers[0].Evaluated.Should().BeTrue();
    }

    [Fact]
    public void WithoutAClosingDateTheFirstMonthIsWhole()
    {
        var series = Run(Series("Old", 1_250_000_000.0, 53_572_000.0, 125_001_000.0, 4.99, 5.39, 6.989, null,
            new DateTime(2026, 10, 15)), new DateTime(2026, 8, 31));

        Accrued(series, "A").Should().BeApproximately(1_250_000_000.0 * 0.0499 / 12.0, OneCent);
    }

    [Fact]
    public void AClosingAfterTheFirstPayDateIsRefused()
    {
        var late = Series("Late", 1_000_000.0, 0.0, 0.0, 5.0, 5.0, 5.0, new DateTime(2026, 10, 20),
            new DateTime(2026, 10, 15));
        var run = CardProgram.Run(CardProgram.Program(), 25_000_000.0, new[] { late }, CardProgram.Drivers(), 1,
            new DateTime(2026, 8, 31));

        FluentActions.Invoking(() => CardProgram.Execute(run))
            .Should().Throw<RevolvingConfigException>().WithMessage("*on or after its first pay date*");
    }

    [Fact]
    public void AClosingWithNoFirstPayDateIsRefused()
    {
        var open = CardProgram.Series("Open", 1_000_000.0, 0.0, 0.0) with { ClosingDate = new DateTime(2026, 9, 22) };
        var run = CardProgram.Run(CardProgram.Program(), 25_000_000.0, new[] { open }, CardProgram.Drivers(), 1,
            new DateTime(2026, 8, 31));

        FluentActions.Invoking(() => CardProgram.Execute(run))
            .Should().Throw<RevolvingConfigException>().WithMessage("*no firstPayDate*");
    }
}
