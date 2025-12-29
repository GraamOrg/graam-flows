using GraamFlows.Util.MathUtil;

namespace GraamFlows.Util.Solvers1D;

public class Bisection : Solver1D<IRealFunction1D>
{
    protected override double SolveImpl(IRealFunction1D f, double xAccuracy)
    {
        /* The implementation of the algorithm was inspired by
           Press, Teukolsky, Vetterling, and Flannery,
           "Numerical Recipes in C", 2nd edition, Cambridge
           University Press
        */

        double dx, xMid, fMid;

        // Orient the search so that f>0 lies at _root+dx
        if (_fxMin < 0.0)
        {
            dx = _xMax - _xMin;
            _root = _xMin;
        }
        else
        {
            dx = _xMin - _xMax;
            _root = _xMax;
        }

        while (_evaluationNumber <= _maxEvaluations)
        {
            dx /= 2.0;
            xMid = _root + dx;
            fMid = f.Value(xMid);
            _evaluationNumber++;
            if (fMid <= 0.0)
                _root = xMid;
            if (Math.Abs(dx) < xAccuracy || fMid == 0.0)
                return _root;
        }

        throw new ConvergenceException("maximum number of function evaluations (" + _maxEvaluations + ") exceeded");
    }
}