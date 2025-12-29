namespace GraamFlows.Util.MathUtil;

public interface IValue
{
    double value(double v);
}

public abstract class ISolver1d : IValue
{
    public abstract double value(double v);

    public virtual double derivative(double x)
    {
        return 0;
    }
}

public class ConvergenceException : Exception
{
    public ConvergenceException()
    {
    }

    public ConvergenceException(string message) : base(message)
    {
    }

    public ConvergenceException(string message, Exception inner) : base(message, inner)
    {
    }
}

public class RootBracketingException : Exception
{
    public RootBracketingException()
    {
    }

    public RootBracketingException(string message) : base(message)
    {
    }

    public RootBracketingException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
///     Base class for 1D solvers
/// </summary>
public abstract class Solver1D<IFunction1D> where IFunction1D : IRealFunction1D
{
    private const int MAX_FUNCTION_EVALUATIONS = 100;
    protected const double EPSILON = 1e-15;
    protected int _evaluationNumber;

    private double _lowerBound, _upperBound;
    private bool _lowerBoundEnforced, _upperBoundEnforced;
    protected int _maxEvaluations = MAX_FUNCTION_EVALUATIONS;
    protected double _root, _xMin, _xMax, _fxMin, _fxMax;


    /// <summary>
    ///     This method returns the zero of the function f.
    ///     This method contains a bracketing routine to which an initial
    ///     guess must be supplied as well as a step used to
    ///     scan the range of the possible bracketing values.
    ///     Once the zero is bracketed, SolveImpl is called to zero in on the solution
    /// </summary>
    public double Solve(IFunction1D f, double accuracy, double guess, double step)
    {
        if (accuracy <= 0.0)
            throw new ArgumentException("accuracy (" + accuracy + ") must be positive");

        accuracy = Math.Max(accuracy, 1e-15);

        const double growthFactor = 1.6;
        var flipflop = -1;

        _root = guess;
        _fxMax = f.Value(_root);

        // monotonically crescent bias, as in optionValue(volatility)
        if (_fxMax == 0.0)
            return _root;

        if (_fxMax > 0.0)
        {
            _xMin = EnforceBounds(_root - step);
            _fxMin = f.Value(_xMin);
            _xMax = _root;
        }
        else
        {
            _xMin = _root;
            _fxMin = _fxMax;
            _xMax = EnforceBounds(_root + step);
            _fxMax = f.Value(_xMax);
        }

        _evaluationNumber = 2;
        while (_evaluationNumber <= _maxEvaluations)
        {
            if (_fxMin * _fxMax <= 0.0)
            {
                if (_fxMin == 0.0) return _xMin;
                if (_fxMax == 0.0) return _xMax;
                _root = (_xMax + _xMin) / 2.0;
                return SolveImpl(f, accuracy);
            }

            if (Math.Abs(_fxMin) < Math.Abs(_fxMax))
            {
                _xMin = EnforceBounds(_xMin + growthFactor * (_xMin - _xMax));
                _fxMin = f.Value(_xMin);
            }
            else if (Math.Abs(_fxMin) > Math.Abs(_fxMax))
            {
                _xMax = EnforceBounds(_xMax + growthFactor * (_xMax - _xMin));
                _fxMax = f.Value(_xMax);
            }
            else if (flipflop == -1)
            {
                _xMin = EnforceBounds(_xMin + growthFactor * (_xMin - _xMax));
                _fxMin = f.Value(_xMin);
                _evaluationNumber++;
                flipflop = 1;
            }
            else if (flipflop == 1)
            {
                _xMax = EnforceBounds(_xMax + growthFactor * (_xMax - _xMin));
                _fxMax = f.Value(_xMax);
                flipflop = -1;
            }

            _evaluationNumber++;
        }

        throw new ArgumentException("unable to bracket root in " + _maxEvaluations
                                                                 + " function evaluations (last bracket attempt: " +
                                                                 "f[" + _xMin + "," + _xMax + "] "
                                                                 + "-> [" + _fxMin + "," + _fxMax + "])");
    }

    /// <summary>
    ///     This method returns the zero of the function f.
    ///     An initial guess must be supplied, as well as two values which
    ///     must bracket the zero
    /// </summary>
    /// <param name="f"></param>
    /// <param name="accuracy"></param>
    /// <param name="guess"></param>
    /// <param name="xMin"></param>
    /// <param name="xMax"></param>
    /// <returns></returns>
    public double Solve(IFunction1D f, double accuracy, double guess, double xMin, double xMax)
    {
        if (accuracy <= 0.0)
            throw new ArgumentException("accuracy (" + accuracy + ") must be positive");

        accuracy = Math.Max(accuracy, EPSILON);

        _xMin = xMin;
        _xMax = xMax;

        if (!(_xMin < _xMax))
            throw new ArgumentException("invalid range: _xMin (" + _xMin + ") >= _xMax (" + _xMax + ")");
        if (!(!_lowerBoundEnforced || _xMin >= _lowerBound))
            throw new ArgumentException("_xMin (" + _xMin + ") < enforced low bound (" + _lowerBound + ")");
        if (!(!_upperBoundEnforced || _xMax <= _upperBound))
            throw new ArgumentException("_xMax (" + _xMax + ") > enforced hi bound (" + _upperBound + ")");

        _fxMin = f.Value(_xMin);
        if (_fxMin == 0.0) return _xMin;

        _fxMax = f.Value(_xMax);
        if (_fxMax == 0.0) return _xMax;

        _evaluationNumber = 2;

        if (!(_fxMin * _fxMax < 0.0))
            throw new RootBracketingException("root not bracketed: f[" + _xMin + "," + _xMax + "] -> [" + _fxMin + "," +
                                              _fxMax + "]");
        if (!(guess > _xMin))
            throw new ArgumentException("guess (" + guess + ") < _xMin (" + _xMin + ")");
        if (!(guess < _xMax))
            throw new ArgumentException("guess (" + guess + ") > _xMax (" + _xMax + ")");

        _root = guess;

        return SolveImpl(f, accuracy);
    }

    /*! This method sets the maximum number of function evaluations for the bracketing routine. An error is thrown
        if a bracket is not found after this number of evaluations.
    */
    public void SetMaxEvaluations(int evaluations)
    {
        _maxEvaluations = evaluations;
    }

    //! sets the lower bound for the function domain
    public void SetLowerBound(double lowerBound)
    {
        _lowerBound = lowerBound;
        _lowerBoundEnforced = true;
    }

    //! sets the upper bound for the function domain
    public void SetUpperBound(double upperBound)
    {
        _upperBound = upperBound;
        _upperBoundEnforced = true;
    }

    private double EnforceBounds(double x)
    {
        if (_lowerBoundEnforced && x < _lowerBound) return _lowerBound;
        if (_upperBoundEnforced && x > _upperBound) return _upperBound;
        return x;
    }

    protected abstract double SolveImpl(IFunction1D f, double xAccuracy);
}