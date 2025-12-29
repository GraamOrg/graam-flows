namespace GraamFlows.Triggers;

public enum TriggerExecutionType
{
    Terminate
}

public interface ITriggerExecuter
{
    TriggerExecutionType TriggerExecType { get; }
}