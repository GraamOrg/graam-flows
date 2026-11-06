namespace GraamFlows.Revolving;

/// <summary>
///     A series' principal funding account. It takes deposits up to the controlled amount and pays
///     the funded classes on the expected date, or at once in early amortization.
///
///     A month's demand is that month's controlled amount plus the prior month's shortfall. A
///     shortfall carries into the next month only. It is never re-spread over the remaining months.
/// </summary>
public sealed class FundingAccount
{
    public FundingAccount(double balance, double deficit)
    {
        Balance = balance;
        Deficit = deficit;
    }

    public double Balance { get; private set; }

    /// <summary>The shortfall carried into the next deposit.</summary>
    public double Deficit { get; private set; }

    /// <summary>What was deposited this month.</summary>
    public double Deposited { get; private set; }

    /// <summary>Earnings on the opening balance: 30/360, on a percent annual rate.</summary>
    public double Earnings(double annualRatePct)
    {
        return Balance * annualRatePct / 100.0 / 12.0;
    }

    public void OpenMonth()
    {
        Deposited = 0.0;
    }

    /// <summary>What may still be deposited this month, before the cash that is available.</summary>
    /// <param name="controlledAmount">this month's controlled amount.</param>
    /// <param name="room">the funded classes' invested amounts less the account balance.</param>
    public double Demand(double controlledAmount, double room)
    {
        return Math.Max(0.0, Math.Min(controlledAmount + Deficit - Deposited, room));
    }

    public void Deposit(double amount)
    {
        Balance += amount;
        Deposited += amount;
    }

    public void Withdraw(double amount)
    {
        Balance -= amount;
    }

    /// <summary>Closes the month: what is still demanded is next month's shortfall.</summary>
    public void CloseMonth(double controlledAmount, double room)
    {
        Deficit = Demand(controlledAmount, room);
    }

    public FundingAccount Copy()
    {
        return (FundingAccount)MemberwiseClone();
    }
}
