namespace GraamFlows.Waterfall;

public interface IDealVariableProvider
{
    void SetVariable(string varName, object varValue);
    double GetVariable(string varName, DateTime? asOfDate = null);
    object GetVariableObj(string varName, DateTime? asOfDate = null);
}