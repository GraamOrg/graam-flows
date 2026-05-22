using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Api.Models;

/// <summary>
/// Builds <see cref="TriggerResultDto"/> instances from engine
/// <see cref="TriggerResult"/>s with a correctly numbered
/// <see cref="TriggerResultDto.Period"/>.
///
/// Why this exists: the engine's <c>DealCashflows.TriggerResults</c>
/// is a flat list across triggers AND periods — for an N-trigger
/// deal it has N entries per cashflow date. A flat row counter
/// (the pre-fix behavior in both <c>WaterfallController</c> and
/// <c>WaterfallRunner</c>) therefore numbered periods 1..N×P
/// instead of 1..P, breaking any downstream consumer that read
/// <c>Period</c> as a cashflow-month index.
///
/// Fix: derive <c>Period</c> from <c>CashflowDate</c>. Distinct
/// dates that actually appear in <c>TriggerResults</c> are sorted
/// chronologically and assigned 1-based indices; every trigger
/// tested on the same date receives the same period number.
///
/// <c>CashflowDate</c> is normalized via <c>.Date</c> so a
/// non-midnight time-of-day or differing <c>DateTimeKind</c> cannot
/// split same-calendar-date entries into separate periods.
///
/// NOTE: "Period" is an index over the dates that appear in
/// <c>TriggerResults</c>, not necessarily an index over projection
/// cashflow periods. For a deal with deferred trigger testing
/// (triggers tested only after a lockout), the first tested date
/// becomes period 1 even if it is calendar period K. For NQM
/// step-down (triggers tested every period) the two indexings
/// coincide.
/// </summary>
public static class TriggerResultDtoConverter
{
    /// <summary>
    /// Build a 1-based period index keyed by <c>CashflowDate.Date</c>.
    /// </summary>
    public static Dictionary<DateTime, int> BuildPeriodByDateIndex(IEnumerable<DateTime> cashflowDates)
    {
        var distinctOrdered = cashflowDates
            .Select(d => d.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();
        var index = new Dictionary<DateTime, int>(distinctOrdered.Count);
        for (var i = 0; i < distinctOrdered.Count; i++)
        {
            index[distinctOrdered[i]] = i + 1;
        }
        return index;
    }

    /// <summary>
    /// Map a single <see cref="TriggerResult"/> to its DTO, looking up
    /// its period via the index from <see cref="BuildPeriodByDateIndex"/>.
    /// The lookup key is also normalized via <c>.Date</c>.
    /// </summary>
    public static TriggerResultDto ToDto(TriggerResult tr, IReadOnlyDictionary<DateTime, int> periodByDate)
    {
        return new TriggerResultDto
        {
            Period = periodByDate[tr.CashflowDate.Date],
            CashflowDate = tr.CashflowDate,
            TriggerName = tr.TriggerName,
            Triggered = tr.Passed,
            Value = tr.ActualValue,
            RequiredValue = tr.RequiredValue
        };
    }
}
