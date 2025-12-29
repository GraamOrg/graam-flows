namespace GraamFlows.RulesEngine;

public class RulesResults
{
    public RulesResults()
    {
        PaidAmount = 0;
    }

    public double PaidAmount { get; set; }

    public void Pay(double value)
    {
        PaidAmount += value;
    }
}