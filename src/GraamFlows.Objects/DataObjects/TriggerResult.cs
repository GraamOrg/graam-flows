namespace GraamFlows.Objects.DataObjects;

public class TriggerResult
{
    public TriggerResult(DateTime cashflowDate, string groupNum, string triggerName, double actualValue,
        double requiredValue, bool passed)
    {
        CashflowDate = cashflowDate;
        GroupNum = groupNum;
        TriggerName = triggerName;
        RequiredValue = requiredValue;
        ActualValue = actualValue;
        Passed = passed;
    }

    public DateTime CashflowDate { get; }
    public string TriggerName { get; }
    public double RequiredValue { get; }
    public double ActualValue { get; }
    public bool Passed { get; }
    public string GroupNum { get; }

    public override string ToString()
    {
        var passed = "FAIL";
        if (Passed)
            passed = "PASS";
        return
            $"{CashflowDate:yyyy-MM-dd} {TriggerName} - Required/Actual {RequiredValue:N4}/{ActualValue:N4} {passed}";
    }
}