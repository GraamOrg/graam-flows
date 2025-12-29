using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Domain;

public class ScheduledVariable : IScheduledVariable
{
    [Database("cfe_deal_name")] public string DealName { get; set; }

    [Database("Group_Num")] public string GroupNum { get; set; }

    [Database("Scheduled_Variable_Group_Name")]
    public string ScheduleVariableName { get; set; }

    [Database("Begin_Date")] public DateTime BeginDate { get; set; }

    [Database("End_Date")] public DateTime EndDate { get; set; }

    [Database("Value_Num")] public double ValueNum { get; set; }

    [Database("Value_String")] public string ValueString { get; set; }

    [Database("Value_Date")] public DateTime ValueDate { get; set; }
}