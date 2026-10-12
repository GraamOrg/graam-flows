using FluentAssertions;
using GraamFlows.Util.Calender;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// An indenture's Business Day is a day New York banks are open — the Federal Reserve's holidays,
/// which observe a Sunday holiday on the Monday after but do NOT move a Saturday holiday to the
/// Friday before (the generic settlement calendar does). Measured against a reference model's
/// collection cutoffs: Veterans Day 2028 falls on a Saturday; closing the Friday before moved a
/// cutoff back a business day, and 6 loans' payments on the 5th and 6th into the next Collection
/// Period. With this calendar 13 of the 16 mismatched loan payment rows are gone.
/// </summary>
public class FederalReserveCalendarTests
{
    private static readonly Calendar Fed = CalendarFactory.GetUSCalendar("FederalReserve");
    private static readonly Calendar Settlement = CalendarFactory.GetUSCalendar("Settlement");

    [Fact]
    public void A_saturday_holiday_is_not_moved_to_the_friday_before()
    {
        var friday = new DateTime(2028, 11, 10); // Veterans Day is Saturday the 11th
        Fed.IsBusinessDay(friday).Should().BeTrue();
        Settlement.IsBusinessDay(friday).Should().BeFalse("the settlement calendar closes it");
        Fed.IsBusinessDay(new DateTime(2026, 7, 3)).Should().BeTrue("July 4, 2026 is a Saturday");
    }

    [Fact]
    public void A_sunday_holiday_is_observed_on_the_monday()
    {
        Fed.IsBusinessDay(new DateTime(2029, 11, 12)).Should().BeFalse("Veterans Day 2029 is a Sunday");
        Fed.IsBusinessDay(new DateTime(2028, 12, 25)).Should().BeFalse();
        Fed.IsBusinessDay(new DateTime(2033, 12, 26)).Should().BeFalse("Christmas 2033 is a Sunday");
    }

    [Fact]
    public void The_banks_holidays_are_closed_including_juneteenth()
    {
        foreach (var d in new[]
                 {
                     new DateTime(2027, 1, 1), new DateTime(2027, 1, 18), new DateTime(2027, 2, 15),
                     new DateTime(2027, 5, 31), new DateTime(2027, 6, 18) /* Juneteenth Sat: open */,
                     new DateTime(2026, 6, 19), new DateTime(2027, 7, 5), new DateTime(2027, 9, 6),
                     new DateTime(2027, 10, 11), new DateTime(2027, 11, 11), new DateTime(2027, 11, 25)
                 })
            Fed.IsBusinessDay(d).Should().Be(d == new DateTime(2027, 6, 18), d.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void Ten_business_days_before_a_november_2028_payment_date_is_the_6th()
    {
        DateTime Cutoff(Calendar c, DateTime row)
        {
            var d = row;
            for (var k = 0; k < 10;)
            {
                d = d.AddDays(-1);
                if (c.IsBusinessDay(d)) k++;
            }

            return d;
        }

        Cutoff(Fed, new DateTime(2028, 11, 20)).Should().Be(new DateTime(2028, 11, 6));
        Cutoff(Settlement, new DateTime(2028, 11, 20)).Should().Be(new DateTime(2028, 11, 3));
    }
}
