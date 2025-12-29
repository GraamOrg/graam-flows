namespace GraamFlows.Util.Functions;

public class PiecewiseFunction : IFunctionOfDouble
{
    public PiecewiseFunction(IIntFunctionOfDouble indexFinder, ICompositeFunctionOfDouble compositeFunction,
        double defaultValue)
    {
        IndexFinder = indexFinder;
        CompositeFunction = compositeFunction;
        DefaultValue = defaultValue;
    }

    public PiecewiseFunction(IIntFunctionOfDouble indexFinder, ICompositeFunctionOfDouble compositeFunction) : this(
        indexFinder, compositeFunction, 0)
    {
    }

    public IIntFunctionOfDouble IndexFinder { get; }
    public ICompositeFunctionOfDouble CompositeFunction { get; }
    public double DefaultValue { get; }

    public double GetMinArgument()
    {
        return Math.Max(IndexFinder.GetMinArgument(), CompositeFunction.GetMinArgument());
    }

    public double GetMaxArgument()
    {
        return Math.Min(IndexFinder.GetMaxArgument(), CompositeFunction.GetMaxArgument());
    }

    public bool IsValidArgument(double x)
    {
        return IndexFinder.IsValidArgument(x) && CompositeFunction.IsValidArgument(x);
    }

    public double ValueAt(double x)
    {
        if (double.IsNaN(x))
            return DefaultValue;
        return CompositeFunction.ValueAt(IndexFinder.ValueAt(x), x);
    }

    public double? TryValueAt(double x)
    {
        if (IsValidArgument(x))
            return ValueAt(x);
        return null;
    }
}