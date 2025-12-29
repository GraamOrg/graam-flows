using GraamFlows.Util.MathUtil;

namespace GraamFlows.Util.Solvers1D;

public class Brent : Solver1D<IRealFunction1D>
{
    protected override double SolveImpl(IRealFunction1D f, double xAccuracy)
    {
        /* The implementation of the algorithm was inspired by Press, Teukolsky, Vetterling, and Flannery,
           "Numerical Recipes in C", 2nd edition, Cambridge University Press */

        // dummy assignements to avoid compiler warning
        double d = 0.0, e = 0.0;

        _root = _xMax;
        var froot = _fxMax;
        while (_evaluationNumber <= _maxEvaluations)
        {
            if ((froot > 0.0 && _fxMax > 0.0) ||
                (froot < 0.0 && _fxMax < 0.0))
            {
                // Rename _xMin, _root, _xMax and adjust bounds
                _xMax = _xMin;
                _fxMax = _fxMin;
                e = d = _root - _xMin;
            }

            if (Math.Abs(_fxMax) < Math.Abs(froot))
            {
                _xMin = _root;
                _root = _xMax;
                _xMax = _xMin;
                _fxMin = froot;
                froot = _fxMax;
                _fxMax = _fxMin;
            }

            // Convergence check
            var xAcc1 = 2.0 * EPSILON * Math.Abs(_root) + 0.5 * xAccuracy;
            var xMid = (_xMax - _root) / 2.0;
            if (Math.Abs(xMid) <= xAcc1 || froot == 0.0)
                return _root;
            if (Math.Abs(e) >= xAcc1 &&
                Math.Abs(_fxMin) > Math.Abs(froot))
            {
                // Attempt inverse quadratic interpolation
                var s = froot / _fxMin;
                double p;
                double q;
                if (_xMin == _xMax)
                {
                    p = 2.0 * xMid * s;
                    q = 1.0 - s;
                }
                else
                {
                    q = _fxMin / _fxMax;
                    var r = froot / _fxMax;
                    p = s * (2.0 * xMid * q * (q - r) - (_root - _xMin) * (r - 1.0));
                    q = (q - 1.0) * (r - 1.0) * (s - 1.0);
                }

                if (p > 0.0) q = -q; // Check whether in bounds
                p = Math.Abs(p);
                var min1 = 3.0 * xMid * q - Math.Abs(xAcc1 * q);
                var min2 = Math.Abs(e * q);
                if (2.0 * p < (min1 < min2 ? min1 : min2))
                {
                    e = d; // Accept interpolation
                    d = p / q;
                }
                else
                {
                    d = xMid; // Interpolation failed, use bisection
                    e = d;
                }
            }
            else
            {
                // Bounds decreasing too slowly, use bisection
                d = xMid;
                e = d;
            }

            _xMin = _root;
            _fxMin = froot;
            if (Math.Abs(d) > xAcc1)
                _root += d;
            else
                _root += Math.Abs(xAcc1) * Math.Sign(xMid);
            froot = f.Value(_root);
            _evaluationNumber++;
        }

        throw new ConvergenceException("maximum number of function evaluations (" + _maxEvaluations + ") exceeded");
    }
}