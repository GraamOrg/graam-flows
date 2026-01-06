using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Util.Calender;

namespace GraamFlows.Domain;

public class Tranche : ITranche
{
    public Tranche()
    {
    }

    public Tranche(bool isPseudo)
    {
        IsPseudo = isPseudo;
    }

    public bool IsPseudo { get; set; }

    public IDeal Deal { get; set; }

    [Database("cfe_deal_name")] public string DealName { get; set; }

    [Database("Tranche_Name")] public string TrancheName { get; set; }

    [Database("Coupon_Type")] public string CouponType { get; set; }

    [Database("Cashflow_Type")] public string CashflowType { get; set; }

    [Database("Cusip")] public string Cusip { get; set; }

    [Database("Original_Balance")] public double OriginalBalance { get; set; }

    [Database("Stated_Maturity_Date")] public DateTime StatedMaturityDate { get; set; }

    [Database("Legal_Maturity_Date")] public DateTime LegalMaturityDate { get; set; }

    [Database("First_Pay_Date")] public DateTime FirstPayDate { get; set; }

    [Database("First_Settle_Date")] public DateTime FirstSettleDate { get; set; }

    [Database("Pay_Frequency")] public int PayFrequency { get; set; }

    [Database("Pay_Delay")] public int PayDelay { get; set; }

    [Database("Pay_Day")] public int PayDay { get; set; }

    [Database("Business_Day_Convention")] public string BusinessDayConvention { get; set; }

    [Database("Day_Count")] public string DayCount { get; set; }

    [Database("Holiday_Calendar")] public string HolidayCalendar { get; set; }

    [Database("Floater_Index")] public string FloaterIndex { get; set; }

    [Database("Floater_Spread")] public double FloaterSpread { get; set; }

    [Database("Reset_Slope")] public double ResetSlope { get; set; }

    [Database("Cap")] public double Cap { get; set; }

    [Database("Floor")] public double Floor { get; set; }

    [Database("Factor")] public double Factor { get; set; }

    [Database("Fixed_Coupon")] public double FixedCoupon { get; set; }

    [Database("Tranche_Type")] public string TrancheType { get; set; }

    [Database("Class_Reference")] public string ClassReference { get; set; }

    [Database("Coupon_Formula")] public string CouponFormula { get; set; }

    [Database("Description")] public string Description { get; set; }

    [Database("Interest_Priority")] public int InterestPriority { get; set; }

    public ReserveAccountConfig? ReserveConfig { get; set; }

    public CashflowType CashflowTypeEnum
    {
        get
        {
            switch (CashflowType.ToLower())
            {
                case "pi":
                    return Objects.TypeEnum.CashflowType.PrincipalAndInterest;
                case "io":
                    return Objects.TypeEnum.CashflowType.InterestOnly;
                case "po":
                    return Objects.TypeEnum.CashflowType.PrincipalOnly;
                case "expense":
                case "exp":
                case "fee":
                    return Objects.TypeEnum.CashflowType.Expense;
                case "reserve":
                    return Objects.TypeEnum.CashflowType.Reserve;
                default:
                    throw new ArgumentException($"{CashflowType} is not a valid cashflow type");
            }
        }
    }

    public MarketDataInstEnum FloaterIndexEnum
    {
        get
        {
            if (string.IsNullOrEmpty(FloaterIndex))
                return MarketDataInstEnum.None;
            if (Enum.TryParse(FloaterIndex, true, out MarketDataInstEnum floaterIndex))
                return floaterIndex;

            var index = FloaterIndex.ToLower();
            switch (index)
            {
                case "us0001m":
                case "libor1m":
                    return MarketDataInstEnum.Libor1M;
                case "us0003m":
                case "libor3m":
                    return MarketDataInstEnum.Libor3M;
                case "us0006m":
                case "libor6m":
                    return MarketDataInstEnum.Libor6M;
                case "us0012m":
                case "libor12m":
                    return MarketDataInstEnum.Libor12M;
                case "swap2y":
                    return MarketDataInstEnum.Swap2Y;
                case "swap3y":
                    return MarketDataInstEnum.Swap3Y;
                case "swap4y":
                    return MarketDataInstEnum.Swap4Y;
                case "swap5y":
                    return MarketDataInstEnum.Swap5Y;
                case "swap6y":
                    return MarketDataInstEnum.Swap6Y;
                case "swap7y":
                    return MarketDataInstEnum.Swap7Y;
                case "swap8y":
                    return MarketDataInstEnum.Swap8Y;
                case "swap9y":
                    return MarketDataInstEnum.Swap9Y;
                case "swap10y":
                    return MarketDataInstEnum.Swap10Y;
                case "swap12y":
                    return MarketDataInstEnum.Swap12Y;
                case "swap15y":
                    return MarketDataInstEnum.Swap15Y;
                case "swap20y":
                    return MarketDataInstEnum.Swap20Y;
                case "swap25y":
                    return MarketDataInstEnum.Swap25Y;
                case "swap30y":
                    return MarketDataInstEnum.Swap30Y;
                default:
                    throw new ArgumentException(
                        $"Error unknown market Floater Index {DealName}/{TrancheName} - {FloaterIndex}");
            }
        }
    }

    public CouponType CouponTypeEnum
    {
        get
        {
            if (Enum.TryParse(CouponType, true, out CouponType cpnType))
                return cpnType;
            switch (CouponType.ToLower())
            {
                case "fixed":
                    return Objects.TypeEnum.CouponType.Fixed;
                case "floating":
                    return Objects.TypeEnum.CouponType.Floating;
                case "tranche_wac":
                    return Objects.TypeEnum.CouponType.TrancheWac;
                case "formula":
                    return Objects.TypeEnum.CouponType.Formula;
                case "residual_interest":
                    return Objects.TypeEnum.CouponType.ResidualInterest;
                default:
                    throw new ArgumentException($"{CouponType} is not a valid cashflow type");
            }
        }
    }

    public TrancheTypeEnum TrancheTypeEnum => (TrancheTypeEnum)Enum.Parse(typeof(TrancheTypeEnum), TrancheType);

    public DayCounter GetDayCounter()
    {
        return CalendarFactory.GetDayCounter(DayCount);
    }

    public Calendar GetCalendar()
    {
        return CalendarFactory.GetUSCalendar(HolidayCalendar);
    }

    public BusinessDayConvention GetBusinessDayConvention()
    {
        return CalendarFactory.GetBusinessDayConvention(BusinessDayConvention);
    }

    public override string ToString()
    {
        return $"{DealName}/{TrancheName} Oface:{OriginalBalance:#,###} Cface:{OriginalBalance * Factor:#,###}";
    }
}