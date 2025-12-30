using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

public class SinglePlannedAmortizationClass : BasePayable
{
    public SinglePlannedAmortizationClass(IDealVariableProvider dealVars, string balSchedVar, IPayable spac)
    {
        DealVars = dealVars;
        BalSchedVar = balSchedVar;
        Spac = spac;
    }

    public IDealVariableProvider DealVars { get; }
    public IPayable Spac { get; }
    public string BalSchedVar { get; }

    public override bool IsLeaf => false;

    public override void PaySp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayable(Spac, prin, (payable, amt) => payable.PaySp(this, cfDate, amt, payRuleExec), payRuleExec);
    }

    public override void PayUsp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayable(Spac, prin, (payable, amt) => payable.PayUsp(this, cfDate, amt, payRuleExec), payRuleExec);
    }

    public override void PayRp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayable(Spac, prin, (payable, amt) => payable.PayRp(this, cfDate, amt, payRuleExec), payRuleExec);
    }

    public override void PayWritedown(IPayable caller, DateTime cfDate, double amount, Action payRuleExec)
    {
        PayPayable(Spac, amount, (payable, amt) => payable.PayWritedown(this, cfDate, amt, payRuleExec), payRuleExec);
    }

    public override double PayInterest(IPayable caller, DateTime cfDate, double availableFunds,
        IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches)
    {
        return Spac.PayInterest(this, cfDate, availableFunds, rateProvider, allTranches);
    }

    public override double InterestDue(DateTime cfDate, IRateProvider rateProvider,
        IEnumerable<DynamicTranche> allTranches)
    {
        return Spac.InterestDue(cfDate, rateProvider, allTranches);
    }

    public override double CurrentBalance(DateTime cfDate)
    {
        var currBal = Spac.Leafs().Sum(leaf => leaf.CurrentBalance(cfDate));
        var pacbal = DealVars.GetVariable(BalSchedVar, cfDate);
        var maxPrin = currBal - pacbal;
        return Math.Max(maxPrin, 0);
    }

    public override string Describe(int level)
    {
        var tabs = string.Concat(Enumerable.Repeat("\t", level));
        return $"{tabs}SPAC('{BalSchedVar}', \n{Spac.Describe(level + 1)})\n";
    }

    public override XElement DescribeXml()
    {
        var element = new XElement("SPAC");
        element.Add(new XAttribute("SchedName", BalSchedVar));
        element.Add(Spac.DescribeXml());
        return element;
    }

    public override HashSet<IPayable> Leafs()
    {
        var set = new HashSet<IPayable>();
        set.UnionWith(Spac.Leafs());
        return set;
    }

    public override List<IPayable> GetChildren()
    {
        return new List<IPayable> { Spac };
    }

    private void PayPayable(IPayable payable, double prin, Action<IPayable, double> pay, Action payRuleExec)
    {
        payRuleExec.Invoke();
        pay.Invoke(payable, prin);
    }
}