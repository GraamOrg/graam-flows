namespace GraamFlows.Objects.DataObjects;

public class AssetCashflows
{
    public AssetCashflows(IAsset asset, Cashflows cashflows)
    {
        Asset = asset;
        Cashflows = cashflows;
    }

    public IAsset Asset { get; private set; }
    public Cashflows Cashflows { get; }
}