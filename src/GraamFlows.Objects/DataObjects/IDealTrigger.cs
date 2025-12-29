namespace GraamFlows.Objects.DataObjects;

public interface IDealTrigger
{
    string DealName { get; }
    string TriggerName { get; }
    string TriggerType { get; }
    bool IsMandatory { get; }
    string TriggerParam { get; }
    string TriggerParam2 { get; }
    string GroupNum { get; }
    string PossibleValues { get; }
    string TriggerFormula { get; }
}