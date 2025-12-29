using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall;

public class DynamicFundsAccount : DynamicClass
{
    private double _accountBalance;
    private double _debits;

    public DynamicFundsAccount(DynamicGroup dynamicGroup, ITranche tranche, IList<DynamicTranche> dynamicTranches) :
        base(dynamicGroup, tranche, dynamicTranches)
    {
    }

    public void Deposit(double amount)
    {
        _accountBalance = amount;
        _debits = 0;
    }

    public void NewPeriod()
    {
        _accountBalance = 0;
        _debits = 0;
    }

    public double Debits()
    {
        return _debits;
    }

    public double Debit(double amount)
    {
        double withdrawAmount;
        if (amount > _accountBalance)
            withdrawAmount = _accountBalance;
        else
            withdrawAmount = amount;

        _accountBalance -= withdrawAmount;
        _debits += withdrawAmount;
        return withdrawAmount;
    }
}