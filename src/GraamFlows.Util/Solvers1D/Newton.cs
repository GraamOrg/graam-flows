using GraamFlows.Util.MathUtil;

namespace GraamFlows.Util.Solvers1D;

/// <summary>
///     Newton 1-D solver the passed function object
///     implement a real derivative
/// </summary>
public class Newton : Solver1D<IC1RealFunction1D>
{
    protected override double SolveImpl(IC1RealFunction1D f, double xAccuracy)
    {
        /* The implementation of the algorithm was inspired by Press, Teukolsky, Vetterling, and Flannery,
           "Numerical Recipes in C", 2nd edition, Cambridge University Press */

        var froot = f.Value(_root);
        var dfroot = f.Derivative(_root);

        if (dfroot == default)
            throw new ArgumentException("Newton requires function's derivative");
        _evaluationNumber++;

        while (_evaluationNumber <= _maxEvaluations)
        {
            var dx = froot / dfroot;
            _root -= dx;
            // jumped out of brackets, switch to NewtonSafe
            if ((_xMin - _root) * (_root - _xMax) < 0.0)
            {
                var s = new NewtonSafe();
                s.SetMaxEvaluations(_maxEvaluations - _evaluationNumber);
                return s.Solve(f, xAccuracy, _root + dx, _xMin, _xMax);
            }

            if (Math.Abs(dx) < xAccuracy)
                return _root;
            froot = f.Value(_root);
            dfroot = f.Derivative(_root);
            _evaluationNumber++;
        }

        throw new ConvergenceException("maximum number of function evaluations (" + _maxEvaluations + ") exceeded");
    }
}