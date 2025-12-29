namespace GraamFlows.Util.Functions;

public class PiecewiseLinearFunction : PiecewiseFunction
{
    protected PiecewiseLinearFunction(IIntFunctionOfDouble indexFinder, double[] intercepts, double[] slopes) : base(
        indexFinder, new PiecewiseLinearFunctionInternal(intercepts, slopes))
    {
    }

    public static PiecewiseLinearFunction FromPoints(double[] x, double[] y, ExtrapolationBehavior lowerBoundBehavior,
        ExtrapolationBehavior upperBoundBehavior)
    {
        var extendLeft =
            (lowerBoundBehavior == ExtrapolationBehavior.Extrapolate ||
             lowerBoundBehavior == ExtrapolationBehavior.Constant) && x[0] > -double.MaxValue;
        var extendRight =
            (upperBoundBehavior == ExtrapolationBehavior.Extrapolate ||
             upperBoundBehavior == ExtrapolationBehavior.Constant) && x[x.Length - 1] < double.MaxValue;
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
        var b = new double[nbXPoints - 1];

        for (var i = 0; i < x.Length - 1; i++)
        {
            b[offset + i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);
            a[offset + i] = y[i] - b[offset + i] * x[i];
        }

        if (extendLeft)
            if (lowerBoundBehavior == ExtrapolationBehavior.Constant)
            {
                a[0] = a[1] + b[1] * localX[1]; // CONSTANT
                b[0] = 0;
            }
            else
            {
                a[0] = a[1];
                b[0] = b[1];
            }

        if (extendRight)
            if (upperBoundBehavior == ExtrapolationBehavior.Constant)
            {
                a[a.Length - 1] = a[a.Length - 2] + b[a.Length - 2] * localX[nbXPoints - 2]; // CONSTANT
                b[0] = 0;
            }
            else
            {
                a[a.Length - 1] = a[a.Length - 2];
                b[b.Length - 1] = b[b.Length - 2];
            }

        return new PiecewiseLinearFunction(new IndexFinderInSortedArray(localX), a, b);
    }

    public static PiecewiseLinearFunction fromInterceptAndSlope(double intercept, double slope)
    {
        return new PiecewiseLinearFunction(new ConstIntFunctionOfDouble(0), new[] { intercept }, new[] { slope });
    }

    public static PiecewiseLinearFunction fromEquidistantPoints(double xMin, double xStep, double[] y,
        ExtrapolationBehavior lowerBoundBehavior, ExtrapolationBehavior upperBoundBehavior)
    {
        if (y.Length == 2 && lowerBoundBehavior == ExtrapolationBehavior.Extrapolate &&
            upperBoundBehavior == ExtrapolationBehavior.Extrapolate)
        {
            // this is just a linear function
            var slope = (y[1] - y[0]) / xStep;
            var intercept = y[0] - slope * xMin;
            return fromInterceptAndSlope(intercept, slope);
        }

        var extendLeft = lowerBoundBehavior == ExtrapolationBehavior.Extrapolate ||
                         lowerBoundBehavior == ExtrapolationBehavior.Constant;
        var extendRight = upperBoundBehavior == ExtrapolationBehavior.Extrapolate ||
                          upperBoundBehavior == ExtrapolationBehavior.Constant;

        var nbSegments = y.Length - 1 + (extendLeft ? 1 : 0) + (extendRight ? 1 : 0);
        var a = new double[nbSegments];
        var b = new double[nbSegments];

        var offset = 0;
        if (extendLeft)
            offset = 1;

        for (var i = 0; i < y.Length - 1; i++)
        {
            b[offset + i] = (y[i + 1] - y[i]) / xStep;
            a[offset + i] = y[i] - b[offset + i] * (xMin + i * xStep);
        }

        if (extendLeft)
            if (lowerBoundBehavior == ExtrapolationBehavior.Constant)
            {
                a[0] = a[1] + b[1] * xMin; // CONSTANT
                b[0] = 0;
            }
            else
            {
                a[0] = a[1];
                b[0] = b[1];
            }

        if (extendRight)
            if (upperBoundBehavior == ExtrapolationBehavior.Constant)
            {
                a[a.Length - 1] = a[a.Length - 2] + b[a.Length - 2] * (xMin + (y.Length - 1) * xStep); // CONSTANT
                b[0] = 0;
            }
            else
            {
                a[a.Length - 1] = a[a.Length - 2];
                b[b.Length - 1] = b[b.Length - 2];
            }

        return new PiecewiseLinearFunction(
            new IndexFinderInRegularArray(xMin, xStep, y.Length, extendLeft, extendRight), a, b);
    }

    public static PiecewiseLinearFunction FromInterceptSlopesAndBreaks(double firstIntercept, double[] slopes,
        double[] breaks)
    {
        if (slopes.Length != breaks.Length + 1)
            // only slope-break-slope...-slope-break-slope kind of sequence supported for now
            throw new ArgumentException("needs 1 more slope than breaks");

        var localX = new double[breaks.Length + 2];
        localX[0] = -double.MaxValue;
        Array.Copy(breaks, 0, localX, 1, breaks.Length);
        localX[localX.Length - 1] = double.MaxValue;

        var b = new double[slopes.Length];
        var a = new double[slopes.Length];
        Array.Copy(slopes, 0, b, 0, slopes.Length);

        a[0] = firstIntercept;
        for (var i = 1; i < a.Length; i++)
            a[i] = a[i - 1] + breaks[i - 1] * (b[i - 1] - b[i]);
        return new PiecewiseLinearFunction(new IndexFinderInSortedArray(localX), a, b);
    }

    private class PiecewiseLinearFunctionInternal : ICompositeFunctionOfDouble
    {
        private readonly double[] _a;
        private readonly double[] _b;

        public PiecewiseLinearFunctionInternal(double[] a, double[] b)
        {
            if (a.Length != b.Length)
                throw new ArgumentException("the number of intercepts should be equal to the number of slopes");
            _a = a;
            _b = b;
        }


        public double ValueAt(int ifunc, double x)
        {
            return _a[ifunc] + _b[ifunc] * x;
        }


        public double GetMinArgument()
        {
            return -double.MaxValue;
        }


        public double GetMaxArgument()
        {
            return double.MaxValue;
        }


        public bool IsValidArgument(double x)
        {
            return x >= GetMinArgument() && x <= GetMaxArgument();
        }
    }
}