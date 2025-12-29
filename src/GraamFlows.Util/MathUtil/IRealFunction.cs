namespace GraamFlows.Util.MathUtil;

/// <summary>
///     a 1-dimensional real function
/// </summary>
public interface IRealFunction1D
{
    double Value(double x);
}

/// <summary>
///     a 1-dimensional  C1 real function, i.e. a function that has df/dx defined
/// </summary>
public interface IC1RealFunction1D : IRealFunction1D
{
    double Derivative(double x);
}

/// <summary>
///     a 1-dimensional  C1 real function, i.e. a function that has df/dx defined
/// </summary>
public interface IC2RealFunction1D : IC1RealFunction1D
{
    double SecondDerivative(double x);
}

#region simplest implemenation

public class SimpleRealFunction1D : IRealFunction1D
{
    private readonly Func<double, double> _value;

    public SimpleRealFunction1D(Func<double, double> value)
    {
        _value = value;
    }

    #region Implementation of IRealFunction1D

    public double Value(double x)
    {
        return _value(x);
    }

    #endregion
}

public class SimpleC1RealFunction1D : SimpleRealFunction1D, IC1RealFunction1D
{
    private readonly Func<double, double> _derivative;

    public SimpleC1RealFunction1D(Func<double, double> value)
        : this(value, x => { throw new NotImplementedException("Derivative not implemented, use another solver"); })
    {
    }

    public SimpleC1RealFunction1D(Func<double, double> value, Func<double, double> derivative) : base(value)
    {
        _derivative = derivative;
    }

    #region Implementation of IC1RealFunction1D

    public double Derivative(double x)
    {
        return _derivative(x);
    }

    #endregion
}

public class SimpleC2RealFunction1D : SimpleC1RealFunction1D, IC2RealFunction1D
{
    private readonly Func<double, double> _secondDerivative;

    public SimpleC2RealFunction1D(Func<double, double> value) : base(value)
    {
        _secondDerivative = x =>
        {
            throw new NotImplementedException(
                "Second Derivative not implemented, use another solver");
        };
    }

    public SimpleC2RealFunction1D(Func<double, double> value, Func<double, double> derivative) : base(value, derivative)
    {
        _secondDerivative = x =>
        {
            throw new NotImplementedException(
                "Second Derivative not implemented, use another solver");
        };
    }

    public SimpleC2RealFunction1D(Func<double, double> value,
        Func<double, double> derivative,
        Func<double, double> secondDerivative) : base(value, derivative)
    {
        _secondDerivative = secondDerivative;
    }


    #region Implementation of IC2RealFunction1D

    public double SecondDerivative(double x)
    {
        return _secondDerivative(x);
    }

    #endregion
}

#endregion

#region Util

public static class RealFunctionUtil
{
    public static SimpleC2RealFunction1D NumericalC2(this IRealFunction1D func, double dx = 1e-7)
    {
        return new SimpleC2RealFunction1D(
            func.Value,
            x => (func.Value(x + dx) - func.Value(x - dx)) / (2 * dx),
            x => (func.Value(x + dx) - 2 * func.Value(x) + func.Value(x - dx)) / (dx * dx)
        );
    }
}

#endregion