using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Factories;

public interface IDealLoader
{
    IDeal LoadDeal(string dealName, DateTime factorDate, bool incAssets = true);
    IDeal LoadDeal(Stream stream);
}