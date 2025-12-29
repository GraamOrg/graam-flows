using GraamFlows.Assumptions;

namespace GraamFlows.Objects.Functions;

public interface IAnchoredVector : IAnchorableVector
{
    int AnchorDateAbsT { get; }
    double ValueAtAbsT(int absT);
    IAnchoredVector transform(IFunctionOfFloat func);
}