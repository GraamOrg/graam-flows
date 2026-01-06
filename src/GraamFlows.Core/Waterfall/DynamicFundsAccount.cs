using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall;

public class DynamicFundsAccount : DynamicClass
{
    private double _accountBalance;
    private double _periodDeposits;
    private double _periodWithdrawals;
    private double _beginningBalance;

    public DynamicFundsAccount(DynamicGroup dynamicGroup, ITranche tranche, IList<DynamicTranche> dynamicTranches) :
        base(dynamicGroup, tranche, dynamicTranches)
    {
        // Initialize balance from tranche original balance
        _accountBalance = tranche.OriginalBalance;
        _beginningBalance = _accountBalance;
    }

    /// <summary>
    /// Reserve configuration from the tranche definition
    /// </summary>
    public ReserveAccountConfig? Config => Tranche.ReserveConfig;

    /// <summary>
    /// Current account balance
    /// </summary>
    public double AccountBalance => _accountBalance;

    /// <summary>
    /// Deposits made this period
    /// </summary>
    public double PeriodDeposits => _periodDeposits;

    /// <summary>
    /// Withdrawals made this period
    /// </summary>
    public double PeriodWithdrawals => _periodWithdrawals;

    /// <summary>
    /// Calculate target reserve amount based on configuration
    /// </summary>
    public double TargetBalance(double currentPoolBalance)
    {
        if (Config == null)
            return Tranche.OriginalBalance; // Default to original balance if no config
        return Config.CalculateTarget(currentPoolBalance);
    }

    /// <summary>
    /// Calculate effective target (applying note balance cap if configured)
    /// </summary>
    public double EffectiveTarget(double currentPoolBalance, double noteBalance)
    {
        if (Config == null)
            return Math.Min(Tranche.OriginalBalance, noteBalance);
        return Config.CalculateEffectiveTarget(currentPoolBalance, noteBalance);
    }

    /// <summary>
    /// Calculate how much deposit is needed to reach target
    /// </summary>
    public double DepositNeeded(double currentPoolBalance, double noteBalance)
    {
        var target = EffectiveTarget(currentPoolBalance, noteBalance);
        return Math.Max(0, target - _accountBalance);
    }

    /// <summary>
    /// Calculate excess balance above effective target
    /// </summary>
    public double ExcessBalance(double currentPoolBalance, double noteBalance)
    {
        var target = EffectiveTarget(currentPoolBalance, noteBalance);
        return Math.Max(0, _accountBalance - target);
    }

    /// <summary>
    /// Start a new period - capture beginning balance and reset period accumulators
    /// </summary>
    public void StartPeriod()
    {
        _beginningBalance = _accountBalance;
        _periodDeposits = 0;
        _periodWithdrawals = 0;
    }

    /// <summary>
    /// Credit (deposit) funds to the account
    /// </summary>
    public void Credit(double amount)
    {
        if (amount <= 0) return;
        _accountBalance += amount;
        _periodDeposits += amount;
        SetBalance(_accountBalance); // Keep base class Balance in sync
    }

    /// <summary>
    /// Debit (withdraw) funds from the account
    /// Returns actual amount withdrawn (may be less than requested if insufficient balance)
    /// </summary>
    public double Debit(double amount)
    {
        if (amount <= 0) return 0;

        var withdrawAmount = Math.Min(amount, _accountBalance);
        _accountBalance -= withdrawAmount;
        _periodWithdrawals += withdrawAmount;
        SetBalance(_accountBalance); // Keep base class Balance in sync
        return withdrawAmount;
    }

    /// <summary>
    /// Record cashflows for the period (like a tranche)
    /// </summary>
    public void RecordCashflow(DateTime cfDate)
    {
        var cf = GetCashflow(cfDate);
        cf.BeginBalance = _beginningBalance;
        cf.Balance = _accountBalance;
        // Use ScheduledPrincipal for deposits, UnscheduledPrincipal for withdrawals
        cf.ScheduledPrincipal = _periodDeposits;
        cf.UnscheduledPrincipal = -_periodWithdrawals; // Negative to indicate outflow
        // Interest field tracks net change for easy reporting
        cf.Interest = _periodDeposits - _periodWithdrawals;
    }

    // Legacy methods for backwards compatibility
    public void Deposit(double amount)
    {
        _accountBalance = amount;
        _periodWithdrawals = 0;
    }

    public void NewPeriod()
    {
        StartPeriod();
    }

    public double Debits()
    {
        return _periodWithdrawals;
    }
}