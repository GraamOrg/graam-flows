using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

public class FixedStructure : BasePayable
{
    public FixedStructure(IDealVariableProvider dealVars, string fixedVar, IPayable @fixed, IPayable support)
    {
        FixedAmtVar = fixedVar;
        Fixed = @fixed;
        Support = support;
        DealVars = dealVars;
    }

    public FixedStructure(double fixedAmt, IPayable @fixed, IPayable support)
    {
        FixedAmtConst = fixedAmt;
        Fixed = @fixed;
        Support = support;
        FixedAmtVar = null;
        DealVars = null;
    }

    public IPayable Fixed { get; }
    public IPayable Support { get; }
    public string FixedAmtVar { get; }
    public double FixedAmtConst { get; }
    public IDealVariableProvider DealVars { get; }

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
        // For FIXED structure, pay fixed first, then support
        var paid = Fixed.PayInterest(this, cfDate, availableFunds, rateProvider, allTranches);
        paid += Support.PayInterest(this, cfDate, availableFunds - paid, rateProvider, allTranches);
        return paid;
    }

    public override double InterestDue(DateTime cfDate, IRateProvider rateProvider,
        IEnumerable<DynamicTranche> allTranches)
    {
        return Fixed.InterestDue(cfDate, rateProvider, allTranches) +
               Support.InterestDue(cfDate, rateProvider, allTranches);
    }

    public override string Describe(int level)
    {
        var tabs = string.Concat(Enumerable.Repeat("\t", level));
        if (FixedAmtVar != null)
            return $"{tabs}FIXED('{FixedAmtVar}', \n{Fixed.Describe(level + 1)}, \n{Support.Describe(level + 1)})\n";

        return $"{tabs}FIXED({FixedAmtConst},\n{Fixed.Describe(level + 1)}, \n{Support.Describe(level + 1)})\n";
    }

    public override XElement DescribeXml()
    {
        var element = new XElement("FIXED");
        if (FixedAmtVar != null)
            element.Add(new XAttribute("FixedAmtVar", FixedAmtVar));
        else
            element.Add(new XAttribute("FixedAmtConst", FixedAmtConst));

        var seniors = new XElement("Fixed");
        seniors.Add(Fixed.DescribeXml());

        var subs = new XElement("Subs");
        subs.Add(Support.DescribeXml());
        element.Add(seniors);
        element.Add(subs);
        return element;
    }

    public override HashSet<IPayable> Leafs()
    {
        var set = new HashSet<IPayable>();
        set.UnionWith(Fixed.Leafs());
        set.UnionWith(Support.Leafs());
        return set;
    }

    public override List<IPayable> GetChildren()
    {
        return new List<IPayable> { Fixed, Support };
    }

    private double GetFixedAmt(DateTime asOfDate)
    {
        return DealVars?.GetVariable(FixedAmtVar, asOfDate) ?? FixedAmtConst;
    }

    private void PayPayables(DateTime cfDate, double prin, Action<IPayable, double> pay, Action payRuleExec,
        bool ignoreLockedOut = false)
    {
        if (Math.Abs(prin) < .01)
            return;
        payRuleExec.Invoke();
        var fixedAmt = GetFixedAmt(cfDate);
        var amtRemaining = prin;
        var amtToPayFixed = Math.Min(Math.Min(Fixed.CurrentBalance(cfDate), fixedAmt), amtRemaining);
        pay.Invoke(Fixed, amtToPayFixed);
        amtRemaining -= amtToPayFixed;
        var supportBal = Math.Min(amtRemaining, Support.CurrentBalance(cfDate));
        amtRemaining -= supportBal;
        pay.Invoke(Support, supportBal);
        pay.Invoke(Fixed, amtRemaining);
    }
}