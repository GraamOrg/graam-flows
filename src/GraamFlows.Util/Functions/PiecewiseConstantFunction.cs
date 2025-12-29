namespace GraamFlows.Util.Functions;

public class PiecewiseConstantFunction : PiecewiseFunction
{
    protected PiecewiseConstantFunction(IIntFunctionOfDouble indexFinder, double[] y) : base(indexFinder,
        new CompositeStepFunction(y))
    {
    }

    public static PiecewiseConstantFunction FromPoints(double[] x, double[] y, ExtrapolationBehavior lowerBoundBehavior,
        ExtrapolationBehavior upperBoundBehavior)
    {
        if (lowerBoundBehavior == ExtrapolationBehavior.Extrapolate)
            lowerBoundBehavior = ExtrapolationBehavior.Constant;
        if (upperBoundBehavior == ExtrapolationBehavior.Extrapolate)
            upperBoundBehavior = ExtrapolationBehavior.Constant;

        var extendLeft = lowerBoundBehavior == ExtrapolationBehavior.Constant && x[0] > -double.MaxValue;
        var extendRight = upperBoundBehavior == ExtrapolationBehavior.Constant && x[x.Length - 1] < double.MaxValue;
        var nbXPoints = x.Length + (extendLeft ? 1 : 0) + (extendRight ? 1 : 0);

        var localX = new double[nbXPoints];

        var offset = 0;
        if (extendLeft)
        {
            localX[0] = -double.MaxValue;
            offset = 1;
        }

        Array.Copy(x, 0, localX, offset, x.Length);

        if (extendRight)
            localX[localX.Length - 1] = double.MaxValue;

        var a = new double[nbXPoints - 1];

        for (var i = 0; i < x.Length - 1; i++)
            a[offset + i] = y[i];

        if (extendLeft)
            a[0] = a[1];
        if (extendRight)
            a[a.Length - 1] = y[y.Length - 1];

        return new PiecewiseConstantFunction(new IndexFinderInSortedArray(localX), a);
    }

    public static PiecewiseConstantFunction FromEquidistantPoints(double xMin, double xStep, double[] y,
        ExtrapolationBehavior lowerBoundBehavior, ExtrapolationBehavior upperBoundBehavior)
    {
        if (lowerBoundBehavior == ExtrapolationBehavior.Extrapolate)
            lowerBoundBehavior = ExtrapolationBehavior.Constant;
        if (upperBoundBehavior == ExtrapolationBehavior.Extrapolate)
            upperBoundBehavior = ExtrapolationBehavior.Constant;

        var extendLeft = lowerBoundBehavior == ExtrapolationBehavior.Constant;
        var extendRight = upperBoundBehavior == ExtrapolationBehavior.Constant;

        var nbSegments = y.Length - 1 + (extendLeft ? 1 : 0) + (extendRight ? 1 : 0);
        var a = new double[nbSegments];

        var offset = 0;
        if (extendLeft)
            offset = 1;

        for (var i = 0; i < y.Length - 1; i++)
            a[offset + i] = y[i];

        if (extendLeft)
            a[0] = a[1];
        if (extendRight)
            a[a.Length - 1] = y[y.Length - 1];

        return new PiecewiseConstantFunction(
            new IndexFinderInRegularArray(xMin, xStep, y.Length, extendLeft, extendRight), a);
    }
}