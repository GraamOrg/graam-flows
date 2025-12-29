namespace GraamFlows.Util.Functions;

public class IndexFinderInSortedArray : IIntFunctionOfDouble
{
    public IndexFinderInSortedArray(double[] x)
    {
        if (x.Length < 2)
            throw new ArgumentException("x must have Length >=2");

        X = new double[x.Length];
        Array.Copy(x, X, x.Length);
    }

    public double[] X { get; }


    public double GetMinArgument()
    {
        return X[0];
    }

    public double GetMaxArgument()
    {
        return X[X.Length - 1];
    }

    public bool IsValidArgument(double x)
    {
        return x >= GetMinArgument() && x < GetMaxArgument();
    }

    public int ValueAt(double val)
    {
        return BinarySearch.InterpolationPosition(X, val);
    }

    public int? TryValueAt(double x)
    {
        return ValueAt(x);
    }
}