using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Waterfall;

/// <summary>
///     The calendar an expense row is dated on.
///
///     An expense (a trustee or administrative fee, a management fee, an incentive fee) is a
///     bookkeeping tranche: it states no calendar of its own and is paid at the EXPENSE step of
///     whatever distribution runs. Its row is still dated by
///     <see cref="MarketTranche.DynamicTranche.AdjustedCashflowDate" />, which reads the tranche's
///     own PayDay, DayCount, BusinessDayConvention, HolidayCalendar and FirstPayDate. Both deal
///     readers used to build every expense row on a fixed monthly 25th / 30/360 calendar, so on a
///     deal paying on the 20th the fee rows were dated the 25th — the right amounts on a date no
///     note row carries, and any consumer joining fees to notes by date mis-joined them.
///
///     An expense row therefore takes the calendar of the deal's first note (the first tranche
///     that is neither an expense nor a reserve account): the same five fields
///     <c>AdjustedCashflowDate</c> keys on, plus PayFrequency and FirstSettleDate so the row reports
///     the period it was paid in. A deal with no such tranche keeps the historical calendar.
/// </summary>
public static class ExpenseCalendar
{
    public const int DefaultPayFrequency = 12;
    public const int DefaultPayDay = 25;
    public const string DefaultDayCount = "30/360";
    public const string DefaultBusinessDayConvention = "Following";
    public const string DefaultHolidayCalendar = "Settlement";

    /// <summary>The tranche whose calendar the deal's expense rows follow, or null if none.</summary>
    public static ITranche? CalendarTranche(IDeal deal) =>
        deal.Tranches.FirstOrDefault(t =>
            t.CashflowTypeEnum != CashflowType.Expense && t.CashflowTypeEnum != CashflowType.Reserve)
        ?? deal.Tranches.FirstOrDefault(t => t.CashflowTypeEnum != CashflowType.Expense);

    /// <summary>Date <paramref name="expense" /> on the calendar of <paramref name="calendar" />.</summary>
    public static void Apply(Tranche expense, ITranche? calendar)
    {
        if (calendar == null)
        {
            expense.PayFrequency = DefaultPayFrequency;
            expense.PayDay = DefaultPayDay;
            expense.DayCount = DefaultDayCount;
            expense.BusinessDayConvention = DefaultBusinessDayConvention;
            expense.HolidayCalendar = DefaultHolidayCalendar;
            return;
        }

        expense.PayFrequency = calendar.PayFrequency;
        expense.PayDay = calendar.PayDay;
        expense.DayCount = calendar.DayCount;
        expense.BusinessDayConvention = calendar.BusinessDayConvention;
        expense.HolidayCalendar = calendar.HolidayCalendar;
        expense.FirstPayDate = calendar.FirstPayDate;
        expense.FirstSettleDate = calendar.FirstSettleDate;
    }
}
