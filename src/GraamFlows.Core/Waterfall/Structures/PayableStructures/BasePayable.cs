using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

public abstract class BasePayable : IPayable
{
    public abstract void PaySp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec);
    public abstract void PayUsp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec);
    public abstract void PayRp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec);
    public abstract void PayWritedown(IPayable caller, DateTime cfDate, double amount, Action payRuleExec);

    public abstract double PayInterest(IPayable caller, DateTime cfDate, double availableFunds,
        IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches);

    public abstract double InterestDue(DateTime cfDate, IRateProvider rateProvider,
        IEnumerable<DynamicTranche> allTranches);

    /// <summary>
    /// Default implementation: delegate sequentially to child payables.
    /// Overridden by SequentialStructure and DynamicClass for proper behavior.
    /// </summary>
    public virtual double PayInterestShortfall(DateTime cfDate, double availableFunds)
    {
        var totalPaid = 0.0;
        var remaining = availableFunds;
        foreach (var child in GetChildren())
        {
            if (remaining < 0.01) break;
            var paid = child.PayInterestShortfall(cfDate, remaining);
            totalPaid += paid;
            remaining -= paid;
        }
        return totalPaid;
    }

    public abstract string Describe(int level);
    public abstract XElement DescribeXml();
    public abstract HashSet<IPayable> Leafs();
    public abstract bool IsLeaf { get; }
    public abstract List<IPayable> GetChildren();

    public virtual double BeginBalance(DateTime cfDate)
    {
        return Leafs().Sum(leaf => leaf.BeginBalance(cfDate));
    }

    public virtual double CurrentBalance(DateTime cfDate)
    {
        return Leafs().Sum(leaf => leaf.CurrentBalance(cfDate));
    }

    public virtual bool IsLockedOut(DateTime cfDate)
    {
        return Leafs().All(leaf => leaf.IsLockedOut(cfDate));
    }

    public virtual double LockedOutBalance(DateTime cfDate)
    {
        return Leafs().Sum(leaf => leaf.LockedOutBalance(cfDate));
    }
}