namespace GraamFlows.Objects.DataObjects;

public interface IPayRule
{
    string DealName { get; }
    string RuleName { get; }
    string ClassGroupName { get; }
    string Formula { get; }
    int RuleExecutionOrder { get; }
}