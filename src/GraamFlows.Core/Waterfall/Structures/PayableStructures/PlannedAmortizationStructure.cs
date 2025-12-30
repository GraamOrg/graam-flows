using System.Diagnostics;
using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

public class PlannedAmortizationStructure : BasePayable
{
    public PlannedAmortizationStructure(IDealVariableProvider dealVars, string balSchedVar, IPayable senior,
        IPayable support)
    {
        DealVars = dealVars;
        BalSchedVar = balSchedVar;
        Senior = senior;
        Support = support;
    }

    public IDealVariableProvider DealVars { get; }
    public IPayable Senior { get; }
    public IPayable Support { get; }
    public string BalSchedVar { get; }

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
        // For PAC, pay senior first, then support
        var paid = Senior.PayInterest(this, cfDate, availableFunds, rateProvider, allTranches);
        paid += Support.PayInterest(this, cfDate, availableFunds - paid, rateProvider, allTranches);
        return paid;
    }

    public override double InterestDue(DateTime cfDate, IRateProvider rateProvider,
        IEnumerable<DynamicTranche> allTranches)
    {
        return Senior.InterestDue(cfDate, rateProvider, allTranches) +
               Support.InterestDue(cfDate, rateProvider, allTranches);
    }

    public override string Describe(int level)
    {
        var tabs = string.Concat(Enumerable.Repeat("\t", level));
        return $"{tabs}PAC('{BalSchedVar}', \n{Senior.Describe(level + 1)}, \n{Support.Describe(level + 1)})\n";
    }

    public override XElement DescribeXml()
    {
        var element = new XElement("PAC");
        element.Add(new XAttribute("SchedName", BalSchedVar));
        var senior = new XElement("Senior");
        senior.Add(Senior.DescribeXml());
        var support = new XElement("Support");
        support.Add(Support.DescribeXml());
        element.Add(senior);
        element.Add(support);
        return element;
    }

    public override HashSet<IPayable> Leafs()
    {
        var set = new HashSet<IPayable>();
        set.UnionWith(Senior.Leafs());
        set.UnionWith(Support.Leafs());
        return set;
    }

    public override List<IPayable> GetChildren()
    {
        return new List<IPayable> { Senior, Support };
    }

    private void PayPayables(DateTime cfDate, double prin, Action<IPayable, double> pay, Action payRuleExec)
    {
        if (prin <= 0)
            return;

        payRuleExec.Invoke();
        var pacbal = DealVars.GetVariable(BalSchedVar, cfDate);
        var senBal = Senior.CurrentBalance(cfDate);

        var seniorPrin = prin;
        var supportPrin = 0.0;

        if (senBal - prin < pacbal)
        {
            seniorPrin = senBal - pacbal;
            if (seniorPrin < 0)
                seniorPrin = 0;
            supportPrin = prin - seniorPrin;
        }

        if (seniorPrin > senBal)
        {
            var resid = seniorPrin - senBal;
            seniorPrin = senBal;
            supportPrin += resid;
        }

        var supBal = Support.CurrentBalance(cfDate);
        if (supportPrin > supBal)
        {
            var resid = supportPrin - supBal;
            supportPrin = supBal;
            seniorPrin += resid;
        }

        if (seniorPrin < 0 || supportPrin < 0)
            // shouldn't happen but keep an eye for now
            Debugger.Break();

        pay.Invoke(Senior, seniorPrin);
        pay.Invoke(Support, supportPrin);
    }
}