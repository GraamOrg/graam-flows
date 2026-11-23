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

    /// <summary>
    ///     What a call's Redemption Price pays beyond par and the Payment Date's regular interest:
    ///     <c>None</c> (null/empty) or <c>ToPaymentDate</c> — see <see cref="RedemptionInterest" />.
    ///     Read only by the termination triggers.
    /// </summary>
    string RedemptionInterest { get; }
}