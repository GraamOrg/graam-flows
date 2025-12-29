namespace GraamFlows.Triggers;

public enum TriggerValueType
{
    None,
    BooleanValue,
    NumericValue,
    StringValue,
    Executer
}

public class TriggerValue
{
    public TriggerValue(string triggerName)
    {
        TriggerName = triggerName;
        TriggerResultType = TriggerValueType.None;
    }

    public TriggerValue(string triggerName, bool triggerResult)
    {
        TriggerName = triggerName;
        TriggerResultType = TriggerValueType.BooleanValue;
        TriggerResult = triggerResult;
    }

    public TriggerValue(string triggerName, bool triggerResult, double value)
        : this(triggerName, triggerResult, value, 0)
    {
    }

    public TriggerValue(string triggerName, bool triggerResult, double value, double requiredValue)
    {
        TriggerName = triggerName;
        TriggerResultType = TriggerValueType.NumericValue;
        TriggerResult = triggerResult;
        RequiredValue = requiredValue;
        NumericValue = value;
    }

    public TriggerValue(string triggerName, bool triggerResult, string value)
    {
        TriggerName = triggerName;
        TriggerResultType = TriggerValueType.StringValue;
        TriggerResult = triggerResult;
        StringValue = value;
    }

    public TriggerValue(string triggerName, ITriggerExecuter executer)
    {
        TriggerName = triggerName;
        TriggerResultType = TriggerValueType.Executer;
        TriggerExecuter = executer;
    }

    public string TriggerName { get; }
    public TriggerValueType TriggerResultType { get; }
    public bool TriggerResult { get; }
    public double NumericValue { get; }
    public string StringValue { get; }
    public double RequiredValue { get; }
    public ITriggerExecuter TriggerExecuter { get; }

    public override string ToString()
    {
        switch (TriggerResultType)
        {
            case TriggerValueType.BooleanValue:
                return $"{TriggerName} - {(TriggerResult ? "PASSED" : "FAILED")}";
            case TriggerValueType.NumericValue:
                return $"{TriggerName} - {NumericValue}";
            case TriggerValueType.StringValue:
                return $"{TriggerName} - {StringValue}";
            default:
                return TriggerName;
        }
    }
}