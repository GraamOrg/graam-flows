namespace GraamFlows.Objects.DataObjects;

public enum PathTypeEnum
{
    Hpa
}

public interface IMonteCarloPathGenerator
{
    int Paths { get; }
    void Tune(IModelAssumps modelAssumps);
    List<string> GetParameters();
    MonteCarloPaths GetPaths();
}

public class MonteCarloPaths
{
    public MonteCarloPaths(PathTypeEnum pathType)
    {
        Paths = new Dictionary<int, MonteCarloPath>();
        PathType = pathType;
    }

    public PathTypeEnum PathType { get; }
    public Dictionary<int, MonteCarloPath> Paths { get; }

    public void AddPathItem(int pathNum, string pathDataKey, List<double> pathData)
    {
        if (!Paths.TryGetValue(pathNum, out var pathItem))
        {
            pathItem = new MonteCarloPath();
            Paths.Add(pathNum, pathItem);
        }

        pathItem.AddPathData(pathDataKey, pathData);
    }

    public void AddPathItem(int pathNum, MonteCarloPath path)
    {
        Paths.Add(pathNum, path);
    }
}

public class MonteCarloPath
{
    public MonteCarloPath()
    {
        PathData = new Dictionary<string, List<double>>();
    }

    public Dictionary<string, List<double>> PathData { get; }

    public void AddPathData(string pathDataKey, List<double> pathData)
    {
        PathData.Add(pathDataKey, pathData);
    }

    public void AddPathData(string pathDataKey, double item)
    {
        if (!PathData.TryGetValue(pathDataKey, out var pathData))
        {
            pathData = new List<double>();
            PathData.Add(pathDataKey, pathData);
        }

        pathData.Add(item);
    }
}