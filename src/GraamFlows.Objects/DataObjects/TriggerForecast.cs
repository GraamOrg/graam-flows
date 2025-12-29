namespace GraamFlows.Objects.DataObjects;

public class TriggerForecast
{
    public TriggerForecast(string triggerName, string groupNum, bool alwaysTrigger)
    {
        TriggerName = triggerName;
        GroupNum = groupNum;
        AlwaysTrigger = alwaysTrigger;
        HasCustomParam = false;
    }

    public TriggerForecast(string triggerName, string groupNum, string customParam)
    {
        TriggerName = triggerName;
        GroupNum = groupNum;
        AlwaysTrigger = false;
        HasCustomParam = true;
        CustomParam = customParam;
    }

    public TriggerForecast()
    {
    }

    public string TriggerName { get; set; }
    public string GroupNum { get; set; }
    public bool AlwaysTrigger { get; set; }
    public bool HasCustomParam { get; set; }
    public string CustomParam { get; set; }
}