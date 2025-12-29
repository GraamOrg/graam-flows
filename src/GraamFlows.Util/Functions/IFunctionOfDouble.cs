namespace GraamFlows.Util.Functions;

public interface IFunctionOfDouble
{
    double GetMinArgument();
    double GetMaxArgument();
    bool IsValidArgument(double x);
    double ValueAt(double x);
    double? TryValueAt(double x);
}

public interface ICompositeFunctionOfDouble
{
    double ValueAt(int ifunc, double x);
    double GetMinArgument();
    double GetMaxArgument();
    bool IsValidArgument(double x);
}

public interface IIntFunctionOfDouble
{
    double GetMinArgument();
    double GetMaxArgument();
    bool IsValidArgument(double x);
    int ValueAt(double x);
    int? TryValueAt(double x);
}

public enum ExtrapolationBehavior
{
    Constant,
    Extrapolate,
    None
}