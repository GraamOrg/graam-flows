using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall;

public enum PrincipalTypeEnum
{
    Sched,
    Ppay,
    Recov
}

public enum ResidualHandlingEnum
{
    Sequential,
    Prorata
}

public interface IPayable
{
    bool IsLeaf { get; }
    void PaySp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec);
    void PayUsp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec);
    void PayRp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec);
    void PayWritedown(IPayable caller, DateTime cfDate, double amount, Action payRuleExec);

    /// <summary>
    /// Pay interest through this payable structure.
    /// </summary>
    /// <param name="caller">Parent payable (null if top-level)</param>
    /// <param name="cfDate">Cashflow date</param>
    /// <param name="availableFunds">Funds available for interest payment</param>
    /// <param name="rateProvider">Rate provider for floaters</param>
    /// <param name="allTranches">All tranches for WAC calculations</param>
    /// <returns>Amount of interest actually paid</returns>
    double PayInterest(IPayable caller, DateTime cfDate, double availableFunds,
        IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches);

    /// <summary>
    /// Calculate total interest due for this payable structure without paying.
    /// </summary>
    double InterestDue(DateTime cfDate, IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches);

    /// <summary>
    /// Pay back accumulated interest shortfalls (Cap Carryover) through this payable structure.
    /// Walks leaf tranches and pays back AccumInterestShortfall from available funds.
    /// </summary>
    /// <param name="cfDate">Cashflow date</param>
    /// <param name="availableFunds">Funds available for shortfall payback</param>
    /// <returns>Amount actually paid back</returns>
    double PayInterestShortfall(DateTime cfDate, double availableFunds);

    double BeginBalance(DateTime cfDate);
    double CurrentBalance(DateTime cfDate);

    /// <summary>
    /// Principal-writedown capacity for this payable. Equals <see cref="CurrentBalance"/>
    /// for funded classes, but is 0 for an ExcessInterest (XS) or Residual class,
    /// which has no principal to write down — its notional balance is reset to the pool
    /// each period, so a writedown against it is a no-op. Reporting 0 lets the writedown
    /// cascade flow past it to the funded bonds instead of silently consuming the
    /// allocation. Excess-spread loss absorption is handled on the interest side.
    /// </summary>
    double WritedownCapacity(DateTime cfDate);

    bool IsLockedOut(DateTime cfDate);
    double LockedOutBalance(DateTime cfDate);
    string Describe(int level);
    XElement DescribeXml();
    HashSet<IPayable> Leafs();
    List<IPayable> GetChildren();
}

public interface IPayablesHost
{
    IPayable ScheduledPayable { get; set; }
    IPayable PrepayPayable { get; set; }
    IPayable RecoveryPayable { get; set; }
    IPayable ReservePayable { get; set; }

    // Unified waterfall properties
    IPayable InterestPayable { get; set; }
    IPayable WritedownPayable { get; set; }
    IPayable ExcessPayable { get; set; }
}