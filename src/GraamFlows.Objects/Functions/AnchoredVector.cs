using GraamFlows.Assumptions;

namespace GraamFlows.Objects.Functions;

public class AnchoredVector : IAnchoredVector
{
    private readonly int _lastValueAbsT;
    private readonly FunctionOfConsecutiveIntegers func;

    public AnchoredVector(int anchorAbsT, double[] values, double previousValue)
    {
        AnchorDateAbsT = anchorAbsT;
        var paddedValues = new double[values.Length + 1];
        paddedValues[0] = previousValue;
        for (var i = 0; i < values.Length; i++) paddedValues[i + 1] = values[i];

        _lastValueAbsT = anchorAbsT + values.Length - 1;
        func = new FunctionOfConsecutiveIntegers(
            anchorAbsT - 1, // -1 because we added a value in front of the original values array 
            paddedValues, ExtrapolationBehavior.CONSTANT, ExtrapolationBehavior.CONSTANT);
    }

    public AnchoredVector(int anchorAbsT, double[] values)
        : this(anchorAbsT,
            anchorAbsT + values.Length - 1,
            new FunctionOfConsecutiveIntegers(anchorAbsT,
                values, ExtrapolationBehavior.CONSTANT, ExtrapolationBehavior.CONSTANT)
        )
    {
    }

    protected AnchoredVector(int anchorAbsT, int lastValueAbsT, FunctionOfConsecutiveIntegers func)
    {
        AnchorDateAbsT = anchorAbsT;
        _lastValueAbsT = lastValueAbsT;
        this.func = func;
    }

    public double ValueAt(int simT, int absT)
    {
        return ValueAtAbsT(absT);
    }

    public double ValueAtAbsT(int absT)
    {
        return func.valueAt(absT);
    }

    public int AnchorDateAbsT { get; }

    public IAnchoredVector transform(IFunctionOfFloat transform)
    {
        return new AnchoredVector(AnchorDateAbsT, _lastValueAbsT, func.transform(transform));
    }

    IAnchorableVector IAnchorableVector.transform(IFunctionOfFloat func)
    {
        return transform(func);
    }

    public IAnchorableVector transform(Func<double, double> transform)
    {
        return new AnchoredVector(AnchorDateAbsT, _lastValueAbsT, func.transform(transform));
    }

    public static AnchoredVector ramp(int anchorAbsT, float start, float end, int length)
    {
        if (length <= 0)
            throw new Exception("length must be >0");
        var values = new double[length + 1];
        double incline = (end - start) / length;

        for (var i = 0; i < length; i++) values[i] = start + i * incline;

        values[length] = end;
        return new AnchoredVector(anchorAbsT, values);
    }

    public static AnchoredVector fromDoubleArray(int anchorAbsT, double[] values)
    {
        var floatValues = new double[values.Length];
        for (var i = 0; i < values.Length; i++) floatValues[i] = values[i];

        return new AnchoredVector(anchorAbsT, floatValues);
    }
}