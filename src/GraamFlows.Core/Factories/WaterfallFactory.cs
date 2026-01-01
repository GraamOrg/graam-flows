using GraamFlows.Waterfall.Structures;

namespace GraamFlows.Factories;

public static class WaterfallFactory
{
    public static IWaterfall GetWaterfall(string cashflowEngineName)
    {
        return cashflowEngineName switch
        {
            "ComposableStructure" => new ComposableStructure(),
            _ => throw new ArgumentException(
                $"{cashflowEngineName} is not supported. Available: ComposableStructure.")
        };
    }
}