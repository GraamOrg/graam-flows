using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

/// <summary>
///     Pays the senior leg down to a target BALANCE, and everything past it to the subordinate leg:
///     "to the Class A Noteholders … until the Class A Note Balance has been reduced to the Class A
///     Target Balance, second, to the Class B Noteholders …". The target is a deal variable (dollars),
///     usually a computed variable set each period from the pool.
///
///     Unlike <see cref="EnhancementCapStructure" />, whose cap is a credit-support share measured on
///     the balances at the moment it is called, the target is the same number in every principal step
///     of the period (scheduled, unscheduled, recovery, turbo), so the split does not depend on how the
///     period's principal is divided between them.
/// </summary>
public class TargetBalanceStructure : BasePayable
{
    public TargetBalanceStructure(IDealVariableProvider dealVars, string targetVar, IPayable seniors,
        IPayable subs)
    {
        DealVars = dealVars;
        TargetVar = targetVar;
        Seniors = seniors;
        Subs = subs;
    }

    public IDealVariableProvider DealVars { get; }
    public string TargetVar { get; }
    public IPayable Seniors { get; }
    public IPayable Subs { get; }

    public override bool IsLeaf => false;

    public override void PaySp(IPayable parent, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayables(cfDate, prin, (p, a) => p.PaySp(this, cfDate, a, payRuleExec), payRuleExec);
    }

    public override void PayUsp(IPayable parent, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayables(cfDate, prin, (p, a) => p.PayUsp(this, cfDate, a, payRuleExec), payRuleExec);
    }

    public override void PayRp(IPayable parent, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayables(cfDate, prin, (p, a) => p.PayRp(this, cfDate, a, payRuleExec), payRuleExec);
    }

    public override void PayWritedown(IPayable parent, DateTime cfDate, double amount, Action payRuleExec)
    {
        // Losses are not paid to a target: the subordinate leg absorbs them first, then the seniors.
        var subBal = Subs.Leafs().Sum(leaf => leaf.CurrentBalance(cfDate));
        var toSubs = Math.Min(amount, Math.Max(subBal, 0));
        if (toSubs > 0)
            Subs.PayWritedown(this, cfDate, toSubs, payRuleExec);
        if (amount - toSubs > 0)
            Seniors.PayWritedown(this, cfDate, amount - toSubs, payRuleExec);
    }

    public override double PayInterest(IPayable caller, DateTime cfDate, double availableFunds,
        IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches)
    {
        var paid = Seniors.PayInterest(this, cfDate, availableFunds, rateProvider, allTranches);
        paid += Subs.PayInterest(this, cfDate, availableFunds - paid, rateProvider, allTranches);
        return paid;
    }

    public override double InterestDue(DateTime cfDate, IRateProvider rateProvider,
        IEnumerable<DynamicTranche> allTranches)
    {
        return Seniors.InterestDue(cfDate, rateProvider, allTranches) +
               Subs.InterestDue(cfDate, rateProvider, allTranches);
    }

    private void PayPayables(DateTime cfDate, double prin, Action<IPayable, double> payFunc,
        Action payRuleExec)
    {
        if (prin <= 0)
            return;
        // The target is a computed variable: run this period's pay rules before reading it, as
        // SHIFTI and FIXED do — otherwise it is LAST period's target (a month behind the pool).
        payRuleExec.Invoke();
        var senBal = Seniors.Leafs().Sum(leaf => leaf.CurrentBalance(cfDate));
        var subBal = Subs.Leafs().Sum(leaf => leaf.CurrentBalance(cfDate));
        var target = Math.Max(DealVars.GetVariable(TargetVar, cfDate), 0);
        if (double.IsNaN(target) || double.IsInfinity(target))
            target = 0;

        var senPrin = Math.Min(prin, Math.Max(senBal - target, 0));
        var subPrin = prin - senPrin;
        // What the subordinates cannot absorb goes back to the seniors, below their target.
        if (subPrin > subBal)
        {
            senPrin += subPrin - Math.Max(subBal, 0);
            subPrin = Math.Max(subBal, 0);
        }

        if (senPrin > 0)
            payFunc.Invoke(Seniors, senPrin);
        if (subPrin > 0)
            payFunc.Invoke(Subs, subPrin);
    }

    public override string Describe(int level)
    {
        var tabs = string.Concat(Enumerable.Repeat("\t", level));
        return $"{tabs}TARGET('{TargetVar}', \n{Seniors.Describe(level + 1)}, \n{Subs.Describe(level + 1)})\n";
    }

    public override XElement DescribeXml()
    {
        var element = new XElement("TARGET");
        element.Add(new XAttribute("TargetBalance", TargetVar));
        var seniors = new XElement("Senior");
        seniors.Add(Seniors.DescribeXml());
        var subs = new XElement("Subs");
        subs.Add(Subs.DescribeXml());
        element.Add(seniors);
        element.Add(subs);
        return element;
    }

    public override HashSet<IPayable> Leafs()
    {
        var set = new HashSet<IPayable>();
        set.UnionWith(Seniors.Leafs());
        set.UnionWith(Subs.Leafs());
        return set;
    }

    public override List<IPayable> GetChildren()
    {
        return new List<IPayable> { Seniors, Subs };
    }
}
