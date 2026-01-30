using GraamFlows.Waterfall.Structures;

namespace GraamFlows.Factories;

public static class WaterfallFactory
{
    public static IWaterfall GetWaterfall(string cashflowEngineName)
    {
        return cashflowEngineName switch
        {
            "ComposableStructure" => new ComposableStructure(),
            // Default to ComposableStructure for any other value (Sequential, ProRata, etc.)
            _ => new ComposableStructure()
        };
    }
}