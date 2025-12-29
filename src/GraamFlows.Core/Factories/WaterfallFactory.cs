using GraamFlows.Waterfall.Structures;

namespace GraamFlows.Factories;

public static class WaterfallFactory
{
    public static IWaterfall GetWaterfall(string cashflowEngineName)
    {
        if (cashflowEngineName == "UnifiedStructure")
            return new UnifiedStructure();

        throw new ArgumentException($"{cashflowEngineName} is not supported. Only UnifiedStructure is available.");
    }
}