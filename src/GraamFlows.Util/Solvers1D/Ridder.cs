using GraamFlows.Util.MathUtil;

namespace GraamFlows.Util.Solvers1D;

public class Ridder : Solver1D<IRealFunction1D>
{
    protected override double SolveImpl(IRealFunction1D f, double xAcc)
    {
        /* The implementation of the algorithm was inspired by
           Press, Teukolsky, Vetterling, and Flannery,
           "Numerical Recipes in C", 2nd edition, Cambridge
           University Press
        */

        // test on Black-Scholes implied volatility show that
        // Ridder solver algorithm actually provides an
        // accuracy 100 times below promised
        var xAccuracy = xAcc / 100.0;

        // Any highly unlikely value, to simplify logic below
        _root = double.MinValue;

        while (_evaluationNumber <= _maxEvaluations)
        {
            var xMid = 0.5 * (_xMin + _xMax);
            // First of two function evaluations per iteraton
            var fxMid = f.Value(xMid);
            _evaluationNumber++;
            var s = Math.Sqrt(fxMid * fxMid - _fxMin * _fxMax);
            if (s == 0.0)
                return _root;
            // Updating formula
            var nextRoot = xMid + (xMid - _xMin) *
                ((_fxMin >= _fxMax ? 1.0 : -1.0) * fxMid / s);
            if (Math.Abs(nextRoot - _root) <= xAccuracy)
                return _root;

            _root = nextRoot;
            // Second of two function evaluations per iteration
            var froot = f.Value(_root);
            _evaluationNumber++;
            if (froot == 0.0)
                return _root;

            // Bookkeeping to keep the root bracketed on next iteration
            if (Sign(fxMid, froot) != fxMid)
            {
                _xMin = xMid;
                _fxMin = fxMid;
                _xMax = _root;
                _fxMax = froot;
            }
            else if (Sign(_fxMin, froot) != _fxMin)
            {
                _xMax = _root;
                _fxMax = froot;
            }
            else if (Sign(_fxMax, froot) != _fxMax)
            {
                _xMin = _root;
                _fxMin = froot;
            }
            else
            {
                throw new Exception("never get here.");
            }

            if (Math.Abs(_xMax - _xMin) <= xAccuracy) return _root;
        }

        throw new ConvergenceException("maximum number of function evaluations (" + _maxEvaluations + ") exceeded");
    }

    private static double Sign(double a, double b)
    {
        return b >= 0.0 ? Math.Abs(a) : -Math.Abs(a);
    }
}