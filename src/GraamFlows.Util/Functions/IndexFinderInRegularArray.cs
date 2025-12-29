namespace GraamFlows.Util.Functions;

public class IndexFinderInRegularArray : IIntFunctionOfDouble
{
    private readonly double _adjustedXMin;
    private readonly bool _extendLeft;
    private readonly bool _extendRight;
    private readonly int _maxIdx;
    private readonly double _xMax;
    private readonly double _xMin;
    private readonly double _xStep;

    public IndexFinderInRegularArray(double xMin, double xStep, int nPoints, bool extendLeft, bool extendRight)
    {
        if (xStep <= 0f)
            throw new ArgumentException("xStep must be positive");
        if (nPoints < 2)
            throw new ArgumentException("nPoints must be at leat 2");


        _xMin = xMin;
        _adjustedXMin = xMin - (extendLeft ? xStep : 0f);
        _xStep = xStep;
        _xMax = xMin + (nPoints - 1) * xStep;
        _maxIdx = nPoints - 2 + (extendLeft ? 1 : 0) + (extendRight ? 1 : 0);
        _extendLeft = extendLeft;
        _extendRight = extendRight;
    }

    public double GetMinArgument()
    {
        return _extendLeft ? -double.MaxValue : _xMin;
    }

    public double GetMaxArgument()
    {
        return _extendRight ? double.MaxValue : _xMax;
    }

    public bool IsValidArgument(double x)
    {
        return x >= GetMinArgument() && x < GetMaxArgument();
    }

    public int ValueAt(double val)
    {
        var idx = (int)((val - _adjustedXMin) / _xStep);
        if (idx <= 0) // make sure to include equal, not just < because (int) (-0.5) is 0, not -1

            if (_extendLeft)
                idx = 0;
            else if (val < _adjustedXMin) // this condition is necessary since (int) (-0.1) == 0, 
                throw new ArgumentException("val must be >= xMin");

        if (idx > _maxIdx)

            if (_extendRight)
                idx = _maxIdx;
            else
                throw new ArgumentException("val must be <= xMax = xMin + (nPoints-1) * xStep");

        return idx;
    }

    public int? TryValueAt(double x)
    {
        if (IsValidArgument(x))
            return ValueAt(x);
        return null;
    }
}