namespace GraamFlows.Util.Functions;

public class ConstIntFunctionOfDouble : IIntFunctionOfDouble
{
    private readonly int _value;

    public ConstIntFunctionOfDouble(int value)
    {
        _value = value;
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
        return true;
    }

    public int ValueAt(double x)
    {
        return _value;
    }

    public int? TryValueAt(double x)
    {
        return _value;
    }
}