using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Domain;

public class ExchShare : IExchShare
{
    [Database("cfe_deal_name")] public string DealName { get; set; }

    [Database("Class_Group_Name")] public string ClassGroupName { get; set; }

    [Database("Tranche_Name")] public string TrancheName { get; set; }

    [Database("Quantity")] public double Quantity { get; set; }
}