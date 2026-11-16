using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Objects.DataObjects;

public interface ITranche
{
    IDeal Deal { get; set; }
    string DealName { get; }
    string TrancheName { get; }
    string CouponType { get; }
    string CashflowType { get; }
    string Cusip { get; }
    double OriginalBalance { get; }
    DateTime StatedMaturityDate { get; }
    DateTime LegalMaturityDate { get; }
    DateTime FirstPayDate { get; }
    /// <summary>
    ///     Start of the first accrual period. Settable because Align re-dates the pool onto
    ///     the pay schedule and the accrual has to move with the cash (#4848); both
    ///     implementations already exposed a setter.
    /// </summary>
    DateTime FirstSettleDate { get; set; }
    int PayFrequency { get; }
    int PayDelay { get; }
    int PayDay { get; }
    string BusinessDayConvention { get; set; }
    string DayCount { get; }
    string HolidayCalendar { get; }
    string FloaterIndex { get; }
    double FloaterSpread { get; }
    double ResetSlope { get; }
    double Cap { get; }
    double Floor { get; }
    double Factor { get; set; }

    /// <summary>
    /// What the class had been written down by, and not yet written back up, when the run starts
    /// (graam-harmony#6199 Q13): a run from a trustee date carries the write-downs taken before it,
    /// so WRITEUP and the turbo's pay-back can restore them. Zero unless the request states it.
    /// </summary>
    double WrittenDown => 0;
    double FixedCoupon { get; }
    string TrancheType { get; }
    string ClassReference { get; }
    CashflowType CashflowTypeEnum { get; }
    CouponType CouponTypeEnum { get; }
    TrancheTypeEnum TrancheTypeEnum { get; }
    MarketDataInstEnum FloaterIndexEnum { get; }
    bool IsPseudo { get; }

    /// <summary>A CMBS vertical interest: it takes its percentage of every amount distributed.</summary>
    bool VerticalInterest { get; }
    string CouponFormula { get; }
    string Description { get; }
    int InterestPriority { get; }
    ReserveAccountConfig? ReserveConfig { get; }
}