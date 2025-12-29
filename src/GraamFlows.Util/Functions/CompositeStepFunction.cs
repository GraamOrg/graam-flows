namespace GraamFlows.Util.Functions;

public class CompositeStepFunction : ICompositeFunctionOfDouble
{
    public CompositeStepFunction(double[] values)
    {
        Y = new double[values.Length];
        Array.Copy(values, Y, values.Length);
    }

    public double[] Y { get; }


    public double ValueAt(int ifunc, double x)
    {
        return Y[ifunc];
    }

    public double GetMinArgument()
    {
        return -double.MinValue;
    }

    public double GetMaxArgument()
    {
        return double.MaxValue;
    }

    public bool IsValidArgument(double x)
    {
        return true;
    }
}