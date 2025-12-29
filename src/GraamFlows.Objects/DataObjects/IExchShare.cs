namespace GraamFlows.Objects.DataObjects;

public interface IExchShare
{
    string DealName { get; }
    string ClassGroupName { get; }
    string TrancheName { get; }
    double Quantity { get; }
}