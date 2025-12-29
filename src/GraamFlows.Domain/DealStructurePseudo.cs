using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Domain;

public class DealStructurePseudo : IDealStructurePseudo
{
    [Database("cfe_deal_name")] public string DealName { get; set; }

    [Database("Class_Group_Name")] public string ClassGroupName { get; set; }

    [Database("Exchangable_Class_List")] public string ExchangableClassList { get; set; }
}