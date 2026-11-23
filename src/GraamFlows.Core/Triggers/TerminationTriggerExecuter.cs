namespace GraamFlows.Triggers;

public class TerminationTriggerExecuter : ITriggerExecuter
{
    public TerminationTriggerExecuter(RedemptionInterest redemptionInterest = RedemptionInterest.None)
    {
        RedemptionInterest = redemptionInterest;
    }

    /// <summary>What the redemption pays beyond par and the Payment Date's regular interest.</summary>
    public RedemptionInterest RedemptionInterest { get; }

    public TriggerExecutionType TriggerExecType => TriggerExecutionType.Terminate;
}
