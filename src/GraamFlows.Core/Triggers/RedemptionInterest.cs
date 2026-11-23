using GraamFlows.Objects.DataObjects;
using GraamFlows.Util;

namespace GraamFlows.Triggers;

/// <summary>
///     What a call's Redemption Price pays beyond par and the Payment Date's regular interest.
///
///     A delay note accrues for a period that ends before its Payment Date — a calendar month paid
///     on the 25th of the next one, say — so on a Payment Date the note has already run part of its
///     NEXT accrual period (the 1st to the 25th). A note retired that day was owed those days too, and
///     a Redemption Price of "accrued and unpaid interest for the month in which the Notes are
///     redeemed … to and including the Payment Date" pays them. Documents with that same wording have
///     published to-call yields struck both with and without them, so the choice is the deal's, stated
///     on its call trigger.
/// </summary>
public enum RedemptionInterest
{
    /// <summary>Par plus the Payment Date's regular interest (and any unpaid shortfall) — the default.</summary>
    None,

    /// <summary>
    ///     Also the interest from the end of the note's accrual period to the Payment Date: the note's
    ///     own coupon on the balance redeemed, over its own day count for its <c>PayDelay</c>. A note
    ///     with no delay has nothing to add.
    /// </summary>
    ToPaymentDate
}

public static class RedemptionInterestParser
{
    /// <summary>
    ///     The trigger's <see cref="IDealTrigger.RedemptionInterest" />. Absent is <c>None</c>; a value
    ///     the engine does not know is refused rather than read as <c>None</c>, so a misspelling cannot
    ///     silently drop interest the deal asked for.
    /// </summary>
    public static RedemptionInterest Parse(IDealTrigger trigger)
    {
        var raw = trigger.RedemptionInterest;
        if (string.IsNullOrWhiteSpace(raw))
            return RedemptionInterest.None;
        if (Enum.TryParse<RedemptionInterest>(raw.Trim(), true, out var value) &&
            Enum.IsDefined(typeof(RedemptionInterest), value))
            return value;
        throw new DealModelingException(trigger.DealName,
            $"Trigger {trigger.TriggerName}: redemptionInterest '{raw}' is not one of None, ToPaymentDate");
    }
}
