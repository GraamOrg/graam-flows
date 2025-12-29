using GraamFlows.Assumptions;

namespace GraamFlows.Objects.Functions;

public interface IAnchorableVector
{
    double ValueAt(int simT, int absT);
    IAnchorableVector transform(IFunctionOfFloat func);
    IAnchorableVector transform(Func<double, double> func);
}