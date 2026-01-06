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
    DateTime FirstSettleDate { get; }
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
    double FixedCoupon { get; }
    string TrancheType { get; }
    string ClassReference { get; }
    CashflowType CashflowTypeEnum { get; }
    CouponType CouponTypeEnum { get; }
    TrancheTypeEnum TrancheTypeEnum { get; }
    MarketDataInstEnum FloaterIndexEnum { get; }
    bool IsPseudo { get; }
    string CouponFormula { get; }
    string Description { get; }
    int InterestPriority { get; }
    ReserveAccountConfig? ReserveConfig { get; }
}