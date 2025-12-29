using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Domain;

public class DealVariables : IDealVariables
{
    [Database("cfe_deal_name")] public string DealName { get; set; }

    [Database("Variable_Name")] public string VariableName { get; set; }

    [Database("Variable_Group_Name")] public string VariableGroupName { get; set; }

    [Database("Group_Num")] public string GroupNum { get; set; }

    [Database("Variable_Value")] public string VariableValue { get; set; }

    [Database("Variable_Value2")] public string VariableValue2 { get; set; }

    [Database("Variable_Description")] public string VariableDescription { get; set; }

    [Database("Is_Forecastable")] public bool IsForecastable { get; set; }
}