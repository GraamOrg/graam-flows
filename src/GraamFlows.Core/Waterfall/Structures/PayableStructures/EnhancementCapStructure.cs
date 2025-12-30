using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

public class EnhancementCapStructure : BasePayable
{
    public EnhancementCapStructure(IDealVariableProvider dealVars, string enhanceCapVar, IPayable seniors,
        IPayable subs)
    {
        DealVars = dealVars;
        EnhancementPctVar = enhanceCapVar;
        Seniors = seniors;
        Subs = subs;
    }

    public EnhancementCapStructure(double enhanceCap, IPayable seniors, IPayable subs)
    {
        DealVars = null;
        EnhancementPctVar = null;
        EnhancementPctConst = enhanceCap;
        Seniors = seniors;
        Subs = subs;
    }

    public IDealVariableProvider DealVars { get; }
    public IPayable Seniors { get; }
    public IPayable Subs { get; }
    public string EnhancementPctVar { get; }
    public double EnhancementPctConst { get; }

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
        // For CSCAP, pay seniors first, then subordinates
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
        var currSenBal = Seniors.Leafs().Sum(leaf => leaf.CurrentBalance(cfDate));
        var currSubBal = Subs.Leafs().Sum(leaf => leaf.CurrentBalance(cfDate));
        var enhanceCap = GetSeniorEnhancementCap(cfDate);

        // check enhancement cap 
        var expectedSupport = CalcExpectedEnhancement(cfDate, prin);
        double subPrin = 0;
        var senPrin = prin;

        if (expectedSupport > enhanceCap)
        {
            var excessEnhacementAmt = CalcExcessEnhancement(cfDate, prin);
            subPrin += excessEnhacementAmt;
            senPrin -= excessEnhacementAmt;
        }

        // balance waterfall
        if (senPrin > currSenBal)
        {
            var resi = senPrin - currSenBal;
            senPrin = currSenBal;
            subPrin += resi;
        }

        if (subPrin > currSubBal)
        {
            var resi = subPrin - currSubBal;
            subPrin = currSubBal;
            senPrin += resi;
        }

        payFunc.Invoke(Seniors, senPrin);
        if (subPrin > 0)
        {
            currSenBal = Seniors.Leafs().Sum(leaf => leaf.CurrentBalance(cfDate));
            currSubBal = Subs.Leafs().Sum(leaf => leaf.CurrentBalance(cfDate));
            // re-check enhancement cap since seniors may pay subs and change initial calculation 
            expectedSupport = CalcExpectedEnhancement(cfDate, subPrin);
            if (expectedSupport > enhanceCap && currSenBal > 0 && currSubBal >= subPrin)
            {
                var newExcess = CalcExcessEnhancement(cfDate, subPrin);
                if (Math.Abs(newExcess - subPrin) < .01)
                    payFunc.Invoke(Subs, subPrin);
                else
                    PayPayables(cfDate, subPrin, payFunc, payRuleExec);
            }
            else
            {
                PayPayables(cfDate, subPrin, payFunc, payRuleExec);
            }
        }
    }

    public override string Describe(int level)
    {
        var tabs = string.Concat(Enumerable.Repeat("\t", level));
        if (EnhancementPctVar != null)
            return
                $"{tabs}CSCAP('{EnhancementPctVar}', \n{Seniors.Describe(level + 1)}, \n{Subs.Describe(level + 1)})\n";

        return $"{tabs}CSCAP({EnhancementPctConst},\n{Seniors.Describe(level + 1)}, \n{Subs.Describe(level + 1)})\n";
    }

    public override XElement DescribeXml()
    {
        var element = new XElement("CSCAP");
        if (EnhancementPctVar != null)
            element.Add(new XAttribute("EnhanceCap", EnhancementPctVar));
        else
            element.Add(new XAttribute("EnhanceCap", EnhancementPctConst));

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

    private double GetSeniorEnhancementCap(DateTime asOfDate)
    {
        return DealVars?.GetVariable(EnhancementPctVar, asOfDate) ?? EnhancementPctConst;
    }

    private double CalcExpectedEnhancement(DateTime cfDate, double prin)
    {
        var seniors = Seniors.Leafs();
        var subs = Subs.Leafs();
        seniors.ExceptWith(subs);
        var seniorBal = seniors.Sum(c => c.CurrentBalance(cfDate)) - prin;
        var subBal = subs.Sum(c => c.CurrentBalance(cfDate));
        var cs = 1 - seniorBal / (seniorBal + subBal);
        if (double.IsNaN(cs) || double.IsInfinity(cs))
            return 0;
        return cs;
    }

    private double CalcExcessEnhancement(DateTime cfDate, double prin)
    {
        var seniors = Seniors.Leafs();
        var subs = Subs.Leafs();
        seniors.ExceptWith(subs);
        var seniorBal = seniors.Sum(c => c.CurrentBalance(cfDate)) - prin;
        var subBal = subs.Sum(c => c.CurrentBalance(cfDate));
        var cs = 1 - seniorBal / (seniorBal + subBal);

        var ceCap = GetSeniorEnhancementCap(cfDate);
        if (cs < ceCap)
            return 0;

        var excess = cs - ceCap;
        var excessAmt = excess * (seniorBal + subBal);
        excessAmt = Math.Min(excessAmt, prin);
        return excessAmt;
    }
}