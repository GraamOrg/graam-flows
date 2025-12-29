using GraamFlows.Util.MathUtil;

namespace GraamFlows.Util.Solvers1D;

/// <summary>
///     Newton solver. The object passed in must implement a derivative.
/// </summary>
public class NewtonSafe : Solver1D<IC1RealFunction1D>
{
    protected override double SolveImpl(IC1RealFunction1D f, double xAccuracy)
    {
        /* The implementation of the algorithm was inspired by Press, Teukolsky, Vetterling, and Flannery,
           "Numerical Recipes in C", 2nd edition, Cambridge University Press */

        double dfroot, dx, dxold;
        double xh, xl;

        // Orient the search so that f(xl) < 0
        if (_fxMin < 0.0)
        {
            xl = _xMin;
            xh = _xMax;
        }
        else
        {
            xh = _xMin;
            xl = _xMax;
        }

        // the "stepsize before last"
        dxold = _xMax - _xMin;
        // it was dxold=std::fabs(_xMax-_xMin); in Numerical Recipes
        // here (_xMax-_xMin > 0) is verified in the constructor

        // and the last step
        dx = dxold;

        var froot = f.Value(_root);
        dfroot = f.Derivative(_root);
        if (dfroot == default)
            throw new ArgumentException("Newton requires function's derivative");
        _evaluationNumber++;

        while (_evaluationNumber <= _maxEvaluations)
        {
            // Bisect if (out of range || not decreasing fast enough)
            if (((_root - xh) * dfroot - froot) *
                ((_root - xl) * dfroot - froot) > 0.0
                || Math.Abs(2.0 * froot) > Math.Abs(dxold * dfroot))
            {
                dxold = dx;
                dx = (xh - xl) / 2.0;
                _root = xl + dx;
            }
            else
            {
                dxold = dx;
                dx = froot / dfroot;
                _root -= dx;
            }

            // Convergence criterion
            if (Math.Abs(dx) < xAccuracy)
                return _root;
            froot = f.Value(_root);
            dfroot = f.Derivative(_root);
            _evaluationNumber++;
            if (froot < 0.0)
                xl = _root;
            else
                xh = _root;
        }

        throw new ConvergenceException("maximum number of function evaluations (" + _maxEvaluations + ") exceeded");
    }
}