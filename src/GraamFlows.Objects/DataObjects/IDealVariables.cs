namespace GraamFlows.Objects.DataObjects;

public interface IDealVariables
{
    string DealName { get; }
    string VariableName { get; }
    string VariableGroupName { get; }
    string GroupNum { get; }
    string VariableValue { get; }
    string VariableValue2 { get; }
    string VariableDescription { get; }
    bool IsForecastable { get; }
}