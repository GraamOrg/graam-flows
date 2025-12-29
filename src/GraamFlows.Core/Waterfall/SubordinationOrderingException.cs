namespace GraamFlows.Waterfall;

public class SubordinationOrderingException : Exception
{
    public SubordinationOrderingException(string msg) : base(msg)
    {
        throw new Exception("wth is this class");
    }
}