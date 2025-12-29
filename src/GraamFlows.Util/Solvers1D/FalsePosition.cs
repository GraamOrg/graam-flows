using GraamFlows.Util.MathUtil;

namespace GraamFlows.Util.Solvers1D;

public class FalsePosition : Solver1D<IRealFunction1D>
{
    protected override double SolveImpl(IRealFunction1D f, double xAccuracy)
    {
        /* The implementation of the algorithm was inspired by
           Press, Teukolsky, Vetterling, and Flannery,
           "Numerical Recipes in C", 2nd edition,
           Cambridge University Press
        */

        double fl, fh, xl, xh, dx, del, froot;

        // Identify the limits so that xl corresponds to the low side
        if (_fxMin < 0.0)
        {
            xl = _xMin;
            fl = _fxMin;
            xh = _xMax;
            fh = _fxMax;
        }
        else
        {
            xl = _xMax;
            fl = _fxMax;
            xh = _xMin;
            fh = _fxMin;
        }

        dx = xh - xl;

        while (_evaluationNumber <= _maxEvaluations)
        {
            // Increment with respect to latest value
            _root = xl + dx * fl / (fl - fh);
            froot = f.Value(_root);
            _evaluationNumber++;
            if (froot < 0.0)
            {
                // Replace appropriate limit
                del = xl - _root;
                xl = _root;
                fl = froot;
            }
            else
            {
                del = xh - _root;
                xh = _root;
                fh = froot;
            }

            dx = xh - xl;
            // Convergence criterion
            if (Math.Abs(del) < xAccuracy || froot == 0.0)
                return _root;
        }

        throw new ConvergenceException("maximum number of function evaluations (" + _maxEvaluations + ") exceeded");
    }
}