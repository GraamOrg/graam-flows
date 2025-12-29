using GraamFlows.Assumptions;

namespace GraamFlows.Objects.Functions;

public class FunctionOfConsecutiveIntegers : IFunctionOfInt
{
    private readonly int offset;
    private readonly double[] values;
    internal ExtrapolationBehavior lowerBoundBehavior;
    internal ExtrapolationBehavior upperBoundBehavior;

    public FunctionOfConsecutiveIntegers(int offset, double[] values) :
        this(offset, values, ExtrapolationBehavior.NONE, ExtrapolationBehavior.NONE)
    {
    }

    public FunctionOfConsecutiveIntegers(int offset, double[] values,
        ExtrapolationBehavior lowerBoundBehavior, ExtrapolationBehavior upperBoundBehavior)
    {
        this.offset = offset;
        this.values = (double[])values.Clone();
        this.lowerBoundBehavior = lowerBoundBehavior;
        this.upperBoundBehavior = upperBoundBehavior;

        sanityCheck(values, lowerBoundBehavior, upperBoundBehavior);
    }

    public FunctionOfConsecutiveIntegers(int offset, List<double> values) :
        this(offset, values, ExtrapolationBehavior.NONE, ExtrapolationBehavior.NONE)
    {
    }

    public FunctionOfConsecutiveIntegers(int offset, List<double> values,
        ExtrapolationBehavior lowerBoundBehavior, ExtrapolationBehavior upperBoundBehavior)
    {
        this.offset = offset;
        this.values = new double[values.Count];
        var i = 0;
        foreach (var f in values) this.values[i++] = f;

        this.lowerBoundBehavior = lowerBoundBehavior;
        this.upperBoundBehavior = upperBoundBehavior;
        sanityCheck(this.values, lowerBoundBehavior, upperBoundBehavior);
    }

    public int getMinArgument()
    {
        return lowerBoundBehavior == ExtrapolationBehavior.NONE ? offset : int.MinValue;
    }

    public int getMaxArgument()
    {
        return upperBoundBehavior == ExtrapolationBehavior.NONE
            ? offset + values.Length - 1
            : int.MaxValue;
    }

    public bool isValidArgument(int i)
    {
        return i >= getMinArgument() && i <= getMaxArgument();
    }

    public double valueAt(int i)
    {
        return FunctionUtil.getExtendedValueFromArray(i - offset, values, lowerBoundBehavior, upperBoundBehavior);
    }

    public double? tryValueAt(int i)
    {
        if (isValidArgument(i))
            return valueAt(i);
        return null;
    }

    public int size()
    {
        return values.Length;
    }

    /**
     * returns a transformed function.
     * WARNING: linear extrapolation is used in output if used in input, even
     * if the transformation function is not linear!
     * @param func
     * @return
     */
    public FunctionOfConsecutiveIntegers transform(IFunctionOfFloat func)
    {
        var mappedValues = new double[values.Length];
        for (var i = 0; i < mappedValues.Length; i++)
            // small optimization to limit call to expensive func if value does not change:
            if (i > 0 && values[i] == values[i - 1])
                mappedValues[i] = mappedValues[i - 1];
            else
                mappedValues[i] = func.valueAt(values[i]);

        return new FunctionOfConsecutiveIntegers(offset, mappedValues, lowerBoundBehavior, upperBoundBehavior);
    }

    public FunctionOfConsecutiveIntegers transform(Func<double, double> func)
    {
        var mappedValues = new double[values.Length];
        for (var i = 0; i < mappedValues.Length; i++)
            // small optimization to limit call to expensive func if value does not change:
            if (i > 0 && values[i] == values[i - 1])
                mappedValues[i] = mappedValues[i - 1];
            else
                mappedValues[i] = func(values[i]);

        return new FunctionOfConsecutiveIntegers(offset, mappedValues, lowerBoundBehavior, upperBoundBehavior);
    }

    private void sanityCheck(double[] values,
        ExtrapolationBehavior lowerBoundBehavior,
        ExtrapolationBehavior upperBoundBehavior)
    {
        if (values.Length < 2 &&
            (lowerBoundBehavior == ExtrapolationBehavior.EXTRAPOLATE ||
             upperBoundBehavior != ExtrapolationBehavior.EXTRAPOLATE))
            throw new Exception("extrapolation behavior is not 'EXTRAPOLATE' but there are less than 2 values");
    }
}