using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Util.Calender;
using GraamFlows.Util.Calender.DayCounters;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
///     "30/360 SIA": 30/360 as US securities count it, where the last day of February is day 30.
///     Exeter Automobile Receivables Trust 2023-1 closed on February 28, 2023; its 30/360 notes
///     first paid March 15 and the trustee accrued 15 days (Class A-2: 135,000,000 x 5.61% x
///     15/360 = 315,562.50). Bond Basis ("30/360") counts 17, so every 30/360 class broke in the
///     first month by two days of interest. Opt-in: "30/360" stays Bond Basis.
/// </summary>
public class Thirty360SiaTests
{
    private static readonly DayCounter Sia = CalendarFactory.GetDayCounter("30/360 SIA");
    private static readonly DayCounter BondBasis = CalendarFactory.GetDayCounter("30/360");

    [Theory]
    [InlineData("30/360 SIA")]
    [InlineData("30/360sia")]
    [InlineData("thirty360sia")]
    [InlineData("30360SIA")]
    public void The_spellings_resolve_to_the_sia_counter(string spelling)
    {
        CalendarFactory.GetDayCounter(spelling).Should().BeOfType<Thirty360Sia>();
    }

    [Fact]
    public void It_is_still_30_360_to_the_waterfall()
    {
        // DynamicTranche normalises a 30/360 period to the pay day by asking `is Thirty360Us`.
        Sia.Should().BeAssignableTo<Thirty360Us>();
        BondBasis.Should().BeOfType<Thirty360Us>("plain 30/360 stays Bond Basis");
    }

    [Fact]
    public void A_first_period_from_the_last_day_of_february_is_15_days_to_the_15th()
    {
        Sia.DayCount(new DateTime(2023, 2, 28), new DateTime(2023, 3, 15)).Should().Be(15);
        Sia.DayCount(new DateTime(2024, 2, 29), new DateTime(2024, 3, 15)).Should().Be(15, "leap year");
        BondBasis.DayCount(new DateTime(2023, 2, 28), new DateTime(2023, 3, 15)).Should().Be(17);
    }

    [Fact]
    public void February_28_in_a_leap_year_is_not_the_end_of_the_month()
    {
        Sia.DayCount(new DateTime(2024, 2, 28), new DateTime(2024, 3, 15)).Should().Be(17);
    }

    [Theory]
    [InlineData(2023, 1, 15, 2023, 2, 15, 30)]
    [InlineData(2023, 3, 15, 2023, 4, 15, 30)]
    [InlineData(2023, 1, 1, 2023, 1, 31, 30)] // D2 = 31 with D1 < 30 stays 31: 31 - 1
    [InlineData(2023, 1, 30, 2023, 3, 31, 60)]
    [InlineData(2023, 1, 31, 2023, 2, 15, 15)]
    [InlineData(2023, 1, 25, 2024, 1, 25, 360)]
    public void A_period_not_touching_the_end_of_february_counts_as_30_360(
        int y1, int m1, int d1, int y2, int m2, int d2, int expected)
    {
        Sia.DayCount(new DateTime(y1, m1, d1), new DateTime(y2, m2, d2)).Should().Be(expected);
    }

    [Fact]
    public void From_the_end_of_february_to_the_end_of_february_is_a_year()
    {
        Sia.DayCount(new DateTime(2023, 2, 28), new DateTime(2024, 2, 29)).Should().Be(360);
    }

    [Fact]
    public void A_note_closing_on_february_28_accrues_15_days_to_march_15()
    {
        static TrancheCashflow FirstRow(string dayCount)
        {
            var projection = new DateTime(2023, 2, 15); // the builder's first pay is a month on
            var collateral = new TestCollateralBuilder()
                .WithGroupNum("1")
                .WithConstantCashflows(projection, 60, 150_000_000, cpr: 0.0, cdr: 0.0, wac: 21.0)
                .Build();
            var (_, cf) = new TestDealBuilder(projectionDate: projection)
                .WithFirstSettleDate(new DateTime(2023, 2, 28))
                .WithTranche("A2", 135_000_000, 5.61)
                .WithDayCount(dayCount)
                .WithSequentialWaterfall("A2")
                .BuildAndRun(collateral);
            var rows = cf.TrancheCashflows.First(t => t.Key.TrancheName == "A2").Value.Cashflows;
            return rows.OrderBy(kv => kv.Key).First().Value;
        }

        var sia = FirstRow("30/360 SIA");
        sia.AccrualDays.Should().Be(15);
        sia.Interest.Should().BeApproximately(315_562.50, 0.01, "the trustee's first A-2 interest");

        var bond = FirstRow("30/360");
        bond.AccrualDays.Should().Be(17, "Bond Basis is unchanged");
        bond.Interest.Should().BeApproximately(357_637.50, 0.01);
    }
}
