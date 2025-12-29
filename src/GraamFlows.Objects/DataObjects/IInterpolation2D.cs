namespace GraamFlows.Objects.DataObjects;

public interface IInterpolation2D
{
    void Interpolate(double[] x, double[] y, int nOutputPoints, out double[] xi, out double[] yi);
}