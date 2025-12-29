using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Domain;

public class DealTrigger : IDealTrigger
{
    [Database("cfe_deal_name")] public string DealName { get; set; }

    [Database("cfe_trigger_name")] public string TriggerName { get; set; }

    [Database("Trigger_Type")] public string TriggerType { get; set; }

    [Database("Trigger_Mandatory")] public bool IsMandatory { get; set; }

    [Database("Trigger_Param")] public string TriggerParam { get; set; }

    [Database("Trigger_Param2")] public string TriggerParam2 { get; set; }

    [Database("Group_Num")] public string GroupNum { get; set; }

    [Database("Possible_Values")] public string PossibleValues { get; set; }

    [Database("Trigger_Formula")] public string TriggerFormula { get; set; }
}