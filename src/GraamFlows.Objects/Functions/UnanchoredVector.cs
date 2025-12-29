using System.Diagnostics;
using GraamFlows.Assumptions;

namespace GraamFlows.Objects.Functions;

public class UnanchoredVector : IUnanchoredVector
{
    private readonly FunctionOfConsecutiveIntegers func;

    public UnanchoredVector(double[] values, double previousValue)
    {
        var paddedValues = new double[values.Length + 1];
        paddedValues[0] = previousValue;
        for (var i = 0; i < values.Length; i++) paddedValues[i + 1] = values[i];

        func = new FunctionOfConsecutiveIntegers(
            0 - 1, // -1 because we added a value in front of the original values array 
            paddedValues, ExtrapolationBehavior.CONSTANT, ExtrapolationBehavior.CONSTANT);
    }

    protected UnanchoredVector(FunctionOfConsecutiveIntegers func)
    {
        Debug.Assert(func.upperBoundBehavior == ExtrapolationBehavior.CONSTANT);
        Debug.Assert(func.lowerBoundBehavior == ExtrapolationBehavior.CONSTANT);
        this.func = func;
    }

    public double ValueAt(int simT, int absT)
    {
        return ValueAtSimT(simT);
    }

    public IAnchorableVector transform(IFunctionOfFloat transform)
    {
        return new UnanchoredVector(func.transform(transform));
    }

    public IAnchorableVector transform(Func<double, double> transform)
    {
        return new UnanchoredVector(func.transform(transform));
    }

    public double ValueAtSimT(int simT)
    {
        return func.valueAt(simT);
    }
}