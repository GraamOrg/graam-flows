namespace GraamFlows.Objects.DataObjects;

public struct Speeds
{
    public double SMM;
    public double MDR;
    public double SEV;
    public double DQ;
}

public interface IPrepaymentModel
{
    void Tune(IModelAssumps assumps);

    Speeds Prob(int absT, int simT, IAsset asset, AssetState assetState, IMortgageRateModel mtgRateModel,
        IAssetPriceModel assetPriceModel);
}

public interface IModelAssumps
{
    string ModelName { get; }
    Dictionary<string, string> ModelParameters { get; }
    IMortgageRateModel LoadMortgageRateModel();
    IAssetPriceModel LoadAssetPriceModel();
    IPrepaymentModel LoadPrepaymentModel();
}

public interface IMortgageRateModel
{
    double MortgageRate(IAsset asset, IRateProvider rateProvider);
    void Tune(IModelAssumps assumps);
}

public interface IAssetPriceModel
{
    double AssetPrice(IAsset asset, IRateProvider rateProvider, double prevAssetPrice);
    void Tune(IModelAssumps assumps);
}