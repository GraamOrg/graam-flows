using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
/// Regression tests for <see cref="TriggerResultDtoConverter"/>.
///
/// Pins the contract that closed the harmony#905 review: the engine's
/// <c>DealCashflows.TriggerResults</c> is a flat list across N
/// triggers × P periods. The pre-fix converter numbered rows with a
/// flat <c>period++</c> counter, so for a multi-trigger deal it
/// shipped <c>Period</c> values of 1..N×P instead of the cashflow
/// period 1..P. These tests pin the date-based derivation: distinct
/// <c>CashflowDate</c>s sorted chronologically, every trigger tested
/// on the same date shares one period number, and a non-midnight
/// <c>DateTime</c> cannot split same-calendar-date entries.
/// </summary>
public class TriggerResultDtoConverterTests
{
    [Fact]
    public void BuildPeriodByDateIndex_NumbersDistinctDatesChronologically_OneBased()
    {
        var d1 = new DateTime(2026, 6, 25);
        var d2 = new DateTime(2026, 7, 25);
        var d3 = new DateTime(2026, 8, 25);
        // Out of order, with duplicates (e.g. 3 triggers per date).
        var dates = new[] { d2, d2, d2, d1, d1, d1, d3, d3, d3 };

        var index = TriggerResultDtoConverter.BuildPeriodByDateIndex(dates);

        index.Should().HaveCount(3, "duplicates must collapse to one entry per date");
        index[d1].Should().Be(1, "earliest date is period 1");
        index[d2].Should().Be(2);
        index[d3].Should().Be(3);
    }

    [Fact]
    public void ToDto_MultiTriggerDeal_AllTriggersOnSameDateShareOnePeriod()
    {
        // The exact shape the harmony#905 reviewer asked us to pin:
        // 3 triggers tested per period across N periods. Period 1 has
        // three rows in TriggerResults — all three must report Period == 1.
        var p1 = new DateTime(2026, 6, 25);
        var p2 = new DateTime(2026, 7, 25);
        var p3 = new DateTime(2026, 8, 25);
        var triggerNames = new[] { "DelinquencyTest", "CumNetLossTest", "StepDownDate" };
        var periodDates = new[] { p1, p2, p3 };
        var trs = new List<TriggerResult>();
        foreach (var date in periodDates)
        {
            foreach (var name in triggerNames)
            {
                trs.Add(new TriggerResult(date, "G", name, actualValue: 0.04, requiredValue: 0.05, passed: true));
            }
        }
        // Shuffle to prove ordering of the input list does not matter.
        var rng = new Random(42);
        var shuffled = trs.OrderBy(_ => rng.Next()).ToList();

        var index = TriggerResultDtoConverter.BuildPeriodByDateIndex(shuffled.Select(t => t.CashflowDate));
        var dtos = shuffled.Select(t => TriggerResultDtoConverter.ToDto(t, index)).ToList();

        foreach (var date in periodDates)
        {
            var perDate = dtos.Where(d => d.CashflowDate == date).ToList();
            perDate.Should().HaveCount(3, "every period has all three triggers");
            perDate.Select(d => d.Period).Distinct().Should().ContainSingle(
                "all triggers tested on the same date must share one period number");
        }
        dtos.Where(d => d.CashflowDate == p1).First().Period.Should().Be(1);
        dtos.Where(d => d.CashflowDate == p2).First().Period.Should().Be(2);
        dtos.Where(d => d.CashflowDate == p3).First().Period.Should().Be(3);
        // Period count never exceeds the distinct-date count, never the row count.
        dtos.Select(d => d.Period).Max().Should().Be(3, "max Period == distinct date count, NOT row count");
        dtos.Should().HaveCount(9, "sanity: 3 triggers × 3 periods = 9 rows");
    }

    [Fact]
    public void BuildPeriodByDateIndex_NonMidnightDateTime_NormalizedToDateAtMidnight()
    {
        // Belt-and-suspenders against concern #3 in the PR review: same
        // calendar date carrying a non-midnight time (or differing Kind)
        // must collapse to one period, not split into two.
        var d1AtNoon = new DateTime(2026, 6, 25, 12, 0, 0);
        var d1AtMidnight = new DateTime(2026, 6, 25);
        var d2 = new DateTime(2026, 7, 25);

        var index = TriggerResultDtoConverter.BuildPeriodByDateIndex(new[] { d1AtNoon, d1AtMidnight, d2 });

        index.Should().HaveCount(2, "noon and midnight on the same calendar date must collapse to one period");
        index[d1AtMidnight].Should().Be(1);
        index[d2].Should().Be(2);
    }

    [Fact]
    public void ToDto_MapsEngineFieldsCorrectly_Passed_ActualValue_RequiredValue()
    {
        // The harmony#905 reviewer flagged confusing field names in the
        // engine ctor chain. This test pins the END-TO-END mapping the
        // DTO consumer sees, regardless of internal naming: engine
        // Passed → DTO Triggered; engine ActualValue → DTO Value;
        // engine RequiredValue → DTO RequiredValue.
        var d = new DateTime(2026, 6, 25);
        var index = TriggerResultDtoConverter.BuildPeriodByDateIndex(new[] { d });
        var failing = new TriggerResult(d, "G", "DelinquencyTest",
            actualValue: 0.062, requiredValue: 0.05, passed: false);

        var dto = TriggerResultDtoConverter.ToDto(failing, index);

        dto.Period.Should().Be(1);
        dto.TriggerName.Should().Be("DelinquencyTest");
        dto.Triggered.Should().BeFalse("engine `Passed = false` must surface as DTO `Triggered = false`");
        dto.Value.Should().Be(0.062, "engine ActualValue must surface as DTO Value");
        dto.RequiredValue.Should().Be(0.05, "engine RequiredValue must surface as DTO RequiredValue");
    }
}
