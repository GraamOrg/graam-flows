namespace GraamFlows.Objects.DataObjects;

public struct AssetState
{
    public IRateProvider RateProvider { get; set; }
    public int AbsT { get; set; }
    public double Balance { get; set; }
    public int Age { get; set; }
    public double Coupon { get; set; }
    public double Payment { get; set; }
    public double AmortLtv { get; set; }
    public double HpaLtv { get; set; }
    public double OrigAssetPrice { get; set; }
    public double CurrAssetPrice { get; set; }
    public double MortgageRate { get; set; }
    public double RateIncentive { get; set; }
}