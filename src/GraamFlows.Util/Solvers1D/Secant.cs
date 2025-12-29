using GraamFlows.Util.MathUtil;

namespace GraamFlows.Util.Solvers1D;

public class Secant : Solver1D<IRealFunction1D>
{
    protected override double SolveImpl(IRealFunction1D f, double xAccuracy)
    {
        /* The implementation of the algorithm was inspired by
           Press, Teukolsky, Vetterling, and Flannery,
           "Numerical Recipes in C", 2nd edition, Cambridge
           University Press
        */

        double fl, froot;
        double xl;

        // Pick the bound with the smaller function value
        // as the most recent guess
        if (Math.Abs(_fxMin) < Math.Abs(_fxMax))
        {
            _root = _xMin;
            froot = _fxMin;
            xl = _xMax;
            fl = _fxMax;
        }
        else
        {
            _root = _xMax;
            froot = _fxMax;
            xl = _xMin;
            fl = _fxMin;
        }

        while (_evaluationNumber <= _maxEvaluations)
        {
            var dx = (xl - _root) * froot / (froot - fl);
            xl = _root;
            fl = froot;
            _root += dx;
            froot = f.Value(_root);
            _evaluationNumber++;
            if (Math.Abs(dx) < xAccuracy || froot == 0.0)
                return _root;
        }

        throw new ConvergenceException("maximum number of function evaluations (" + _maxEvaluations + ") exceeded");
    }
}