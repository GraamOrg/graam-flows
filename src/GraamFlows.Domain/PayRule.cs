using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Domain;

public class PayRule : IPayRule
{
    [Database("cfe_deal_name")] public string DealName { get; set; }

    [Database("Rule_Name")] public string RuleName { get; set; }

    [Database("Class_Group_Name")] public string ClassGroupName { get; set; }

    [Database("Formula")] public string Formula { get; set; }

    [Database("Rule_Execution_Order")] public int RuleExecutionOrder { get; set; }
}