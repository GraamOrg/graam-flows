namespace GraamFlows.Objects.Functions;

public interface IUnanchoredVector : IAnchorableVector
{
    double ValueAtSimT(int simT);
}