using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

public class ShiftingInterestStructure : BasePayable
{
    public ShiftingInterestStructure(IDealVariableProvider dealVars, string shiftiVar, IPayable seniors, IPayable subs)
    {
        DealVars = dealVars;
        ShiftiPctVar = shiftiVar;
        Seniors = seniors;
        Subs = subs;
    }

    public ShiftingInterestStructure(double shiftiPct, IPayable seniors, IPayable subs)
    {
        DealVars = null;
        ShiftiPctVar = null;
        ShiftiPctConst = shiftiPct;
        Seniors = seniors;
        Subs = subs;
    }

    public IDealVariableProvider DealVars { get; }
    public IPayable Seniors { get; }
    public IPayable Subs { get; }
    public string ShiftiPctVar { get; }
    public double ShiftiPctConst { get; }

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
        PayPayables(cfDate, amount, (p, a) => p.PayWritedown(this, cfDate, a, payRuleExec), payRuleExec);
    }

    public override double PayInterest(IPayable caller, DateTime cfDate, double availableFunds,
        IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches)
    {
        // For SHIFTI, pay seniors first, then subordinates
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

    private void PayPayables(DateTime cfDate, double prin, Action<IPayable, double> payFunc, Action payRuleExec)
    {
        if (Math.Abs(prin) < .01)
            return;
        payRuleExec.Invoke();
        var shiftPct = GetShiftPct(cfDate);
        var senPrin = shiftPct * prin;
        var subPrin = (1 - shiftPct) * prin;

        var currSenBal = Seniors.CurrentBalance(cfDate);
        var currSubBal = Subs.CurrentBalance(cfDate);

        // lock out waterfall
        var loSubBal = Subs.LockedOutBalance(cfDate);
        if (subPrin > currSubBal - loSubBal)
        {
            var maxSubPrin = currSubBal - loSubBal;
            senPrin += subPrin - maxSubPrin;
            subPrin -= subPrin - maxSubPrin;
        }

        // balance waterfall
        if (senPrin > currSenBal)
        {
            var resi = senPrin - currSenBal;
            senPrin = currSenBal;
            subPrin += resi;
        }

        payFunc.Invoke(Seniors, senPrin);
        if (subPrin > Subs.CurrentBalance(cfDate))
        {
            var resid = subPrin - Subs.CurrentBalance(cfDate);
            subPrin = Subs.CurrentBalance(cfDate);
            payFunc.Invoke(Subs, subPrin);
            payFunc.Invoke(Seniors, resid);
        }
        else
        {
            payFunc.Invoke(Subs, subPrin);
        }
    }

    public override string Describe(int level)
    {
        var tabs = string.Concat(Enumerable.Repeat("\t", level));
        if (ShiftiPctVar != null)
            return $"{tabs}SHIFTI('{ShiftiPctVar}', \n{Seniors.Describe(level + 1)}, \n{Subs.Describe(level + 1)})\n";

        return $"{tabs}SHIFTI({ShiftiPctConst},\n{Seniors.Describe(level + 1)}, \n{Subs.Describe(level + 1)})\n";
    }

    public override XElement DescribeXml()
    {
        var element = new XElement("SHIFTI");
        if (ShiftiPctVar != null)
            element.Add(new XAttribute("ShiftPct", ShiftiPctVar));
        else
            element.Add(new XAttribute("ShiftPct", ShiftiPctConst));

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

    private double GetShiftPct(DateTime asOfDate)
    {
        return DealVars?.GetVariable(ShiftiPctVar, asOfDate) ?? ShiftiPctConst;
    }
}