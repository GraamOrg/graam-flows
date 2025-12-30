using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

public class ForcedPaydownStructure : BasePayable
{
    public ForcedPaydownStructure(IPayable forced, IPayable support)
    {
        Forced = forced;
        Support = support;
    }

    public IPayable Forced { get; }
    public IPayable Support { get; }

    public override bool IsLeaf => false;

    public override void PaySp(IPayable parent, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayables(cfDate, prin, (payable, amt) => payable.PaySp(this, cfDate, amt, payRuleExec), payRuleExec);
    }

    public override void PayUsp(IPayable parent, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayables(cfDate, prin, (payable, amt) => payable.PayUsp(this, cfDate, amt, payRuleExec), payRuleExec);
    }

    public override void PayRp(IPayable parent, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayables(cfDate, prin, (payable, amt) => payable.PayRp(this, cfDate, amt, payRuleExec), payRuleExec);
    }

    public override void PayWritedown(IPayable parent, DateTime cfDate, double amount, Action payRuleExec)
    {
        PayPayables(cfDate, amount, (payable, amt) => payable.PayWritedown(this, cfDate, amt, payRuleExec), payRuleExec);
    }

    public override double PayInterest(IPayable caller, DateTime cfDate, double availableFunds,
        IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches)
    {
        // Pay forced first, then support
        var paid = Forced.PayInterest(this, cfDate, availableFunds, rateProvider, allTranches);
        paid += Support.PayInterest(this, cfDate, availableFunds - paid, rateProvider, allTranches);
        return paid;
    }

    public override double InterestDue(DateTime cfDate, IRateProvider rateProvider,
        IEnumerable<DynamicTranche> allTranches)
    {
        return Forced.InterestDue(cfDate, rateProvider, allTranches) +
               Support.InterestDue(cfDate, rateProvider, allTranches);
    }

    public override string Describe(int level)
    {
        var tabs = string.Concat(Enumerable.Repeat("\t", level));
        return $"{tabs}FORCE_PAYDOWN(\n{Forced.Describe(level + 1)}, \n{Support.Describe(level + 1)})\n";
    }

    public override XElement DescribeXml()
    {
        var element = new XElement("FORCE_PAYDOWN");
        var seniors = new XElement("Forced");
        seniors.Add(Forced.DescribeXml());
        var subs = new XElement("Subs");
        subs.Add(Support.DescribeXml());
        element.Add(seniors);
        element.Add(subs);
        return element;
    }

    public override HashSet<IPayable> Leafs()
    {
        var set = new HashSet<IPayable>();
        set.UnionWith(Forced.Leafs());
        set.UnionWith(Support.Leafs());
        return set;
    }

    public override List<IPayable> GetChildren()
    {
        return new List<IPayable> { Forced, Support };
    }

    private void PayPayables(DateTime cfDate, double prin, Action<IPayable, double> pay, Action payRuleExec,
        bool ignoreLockedOut = false)
    {
        if (Math.Abs(prin) < .01)
            return;
        payRuleExec.Invoke();
        var amtToPay = prin;
        var forcedAmt = Forced.CurrentBalance(cfDate);
        pay.Invoke(Forced, forcedAmt);
        amtToPay -= forcedAmt;
        pay.Invoke(Support, amtToPay);
    }
}