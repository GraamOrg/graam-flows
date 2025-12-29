using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Domain;

public class DealFieldValue : IDealFieldValue
{
    [Database("cfe_deal_name")] public string DealName { get; set; }

    [Database("Group_Num")] public string GroupNum { get; set; }

    [Database("Factor_Date")] public DateTime FactorDate { get; set; }

    [Database("Field_Name")] public string FieldName { get; set; }

    [Database("Value_String")] public string ValueString { get; set; }

    [Database("Value_Num")] public double ValueNum { get; set; }

    [Database("Value_Date")] public DateTime ValueDate { get; set; }
}