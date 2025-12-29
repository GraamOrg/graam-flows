using GraamFlows.Assumptions;

namespace GraamFlows.Objects.Functions;

public class ConstVector : IUnanchoredVector, IAnchoredVector
{
    private readonly int lastValueAbsT;
    private readonly double value;

    public ConstVector(int anchorAbsT, double value)
    {
        AnchorDateAbsT = anchorAbsT;
        lastValueAbsT = AnchorDateAbsT;
        this.value = value;
    }

    public ConstVector(double value, int anchorAbsT)
    {
        AnchorDateAbsT = anchorAbsT;
        lastValueAbsT = AnchorDateAbsT;
        this.value = value;
    }

    public ConstVector(double value, int? anchorAbsT)
    {
        AnchorDateAbsT = anchorAbsT.HasValue ? anchorAbsT.Value : 0;
        lastValueAbsT = AnchorDateAbsT;
        this.value = value;
    }

    public ConstVector(double value) : this(value, null)
    {
    }

    public double ValueAtAbsT(int absT)
    {
        return value;
    }

    public int AnchorDateAbsT { get; }

    public IAnchoredVector transform(IFunctionOfFloat func)
    {
        return new ConstVector(func.valueAt(value), AnchorDateAbsT);
    }

    public double ValueAt(int simT, int absT)
    {
        return value;
    }

    public double ValueAtSimT(int simT)
    {
        return value;
    }

    IAnchorableVector IAnchorableVector.transform(IFunctionOfFloat func)
    {
        return transform(func);
    }

    public IAnchorableVector transform(Func<double, double> func)
    {
        return new ConstVector(func(value));
    }

    public int GetLastValueDateAbsT()
    {
        return lastValueAbsT;
    }
}