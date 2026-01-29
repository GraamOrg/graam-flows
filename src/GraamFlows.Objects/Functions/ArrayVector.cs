using GraamFlows.Assumptions;

namespace GraamFlows.Objects.Functions;

/// <summary>
/// A vector backed by a double[] array, where each element corresponds to one period.
/// Used for passing per-period assumption curves (e.g., varying CDR per month).
/// If the period index exceeds the array length, the last value is held constant.
/// </summary>
public class ArrayVector : IUnanchoredVector, IAnchoredVector
{
    private readonly double[] _values;

    public ArrayVector(int anchorAbsT, double[] values)
    {
        AnchorDateAbsT = anchorAbsT;
        _values = values;
    }

    public int AnchorDateAbsT { get; }

    public double ValueAt(int simT, int absT)
    {
        if (_values.Length == 0) return 0.0;
        // Clamp to last value if past array end
        var idx = Math.Min(simT, _values.Length - 1);
        return _values[idx];
    }

    public double ValueAtSimT(int simT)
    {
        if (_values.Length == 0) return 0.0;
        var idx = Math.Min(simT, _values.Length - 1);
        return _values[idx];
    }

    public double ValueAtAbsT(int absT)
    {
        if (_values.Length == 0) return 0.0;
        var offset = absT - AnchorDateAbsT;
        if (offset < 0) offset = 0;
        var idx = Math.Min(offset, _values.Length - 1);
        return _values[idx];
    }

    public IAnchoredVector transform(IFunctionOfFloat func)
    {
        var transformed = new double[_values.Length];
        for (var i = 0; i < _values.Length; i++)
            transformed[i] = func.valueAt(_values[i]);
        return new ArrayVector(AnchorDateAbsT, transformed);
    }

    IAnchorableVector IAnchorableVector.transform(IFunctionOfFloat func)
    {
        return transform(func);
    }

    public IAnchorableVector transform(Func<double, double> func)
    {
        var transformed = new double[_values.Length];
        for (var i = 0; i < _values.Length; i++)
            transformed[i] = func(_values[i]);
        return new ArrayVector(AnchorDateAbsT, transformed);
    }
}
