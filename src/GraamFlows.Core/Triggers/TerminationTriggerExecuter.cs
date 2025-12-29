namespace GraamFlows.Triggers;

public class TerminationTriggerExecuter : ITriggerExecuter
{
    public TriggerExecutionType TriggerExecType => TriggerExecutionType.Terminate;
}