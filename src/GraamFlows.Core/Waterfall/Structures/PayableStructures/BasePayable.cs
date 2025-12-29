using System.Xml.Linq;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

public abstract class BasePayable : IPayable
{
    public abstract void PaySp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec);
    public abstract void PayUsp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec);
    public abstract void PayRp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec);
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