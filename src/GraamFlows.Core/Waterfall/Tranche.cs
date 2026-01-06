using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Waterfall;

public class Tranche : ITranche
{
    public Tranche(bool isPseudo)
    {
        IsPseudo = isPseudo;
    }

    public IDeal Deal { get; set; }
    public string DealName { get; set; }
    public string TrancheName { get; set; }
    public string CouponType { get; set; }
    public string CashflowType { get; set; }
    public string Cusip { get; set; }
    public double OriginalBalance { get; set; }
    public DateTime StatedMaturityDate { get; set; }
    public DateTime LegalMaturityDate { get; set; }
    public DateTime FirstPayDate { get; set; }
    public DateTime FirstSettleDate { get; set; }
    public int PayFrequency { get; set; }
    public int PayDelay { get; set; }
    public int PayDay { get; set; }
    public string BusinessDayConvention { get; set; }
    public string DayCount { get; set; }
    public string HolidayCalendar { get; set; }
    public string FloaterIndex { get; set; }
    public double FloaterSpread { get; set; }
    public double ResetSlope { get; set; }
    public double Cap { get; set; }
    public double Floor { get; set; }
    public double Factor { get; set; }
    public double FixedCoupon { get; set; }
    public string TrancheType { get; set; }
    public string ClassReference { get; set; }
    public CashflowType CashflowTypeEnum { get; set; }
    public CouponType CouponTypeEnum { get; set; }
    public TrancheTypeEnum TrancheTypeEnum { get; set; }
    public MarketDataInstEnum FloaterIndexEnum { get; set; }
    public bool IsPseudo { get; set; }
    public string CouponFormula { get; set; }
    public string Description { get; set; }
    public int InterestPriority { get; set; }
    public ReserveAccountConfig? ReserveConfig { get; set; }
}
