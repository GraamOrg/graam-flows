using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Util;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

public class SequentialStructure : BasePayable
{
    private readonly IList<IPayable> _payables;

    public SequentialStructure(IList<IPayable> payables)
    {
        _payables = payables;
    }

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
        var interestPaid = 0.0;
        var remaining = availableFunds;

        foreach (var payable in _payables)
        {
            if (remaining < 0.01)
                break;

            if (payable.IsLockedOut(cfDate))
                continue;

            var paid = payable.PayInterest(this, cfDate, remaining, rateProvider, allTranches);
            interestPaid += paid;
            remaining -= paid;
        }

        return interestPaid;
    }

    public override double InterestDue(DateTime cfDate, IRateProvider rateProvider,
        IEnumerable<DynamicTranche> allTranches)
    {
        return _payables.Sum(p => p.InterestDue(cfDate, rateProvider, allTranches));
    }

    public override double PayInterestShortfall(DateTime cfDate, double availableFunds)
    {
        var totalPaid = 0.0;
        var remaining = availableFunds;
        foreach (var payable in _payables)
        {
            if (remaining < 0.01) break;
            var paid = payable.PayInterestShortfall(cfDate, remaining);
            totalPaid += paid;
            remaining -= paid;
        }
        return totalPaid;
    }

    public override string Describe(int level)
    {
        var tabs = string.Concat(Enumerable.Repeat("\t", level));
        return tabs + "SEQ(\n" + string.Join(",\n", _payables.Select(p => p.Describe(level + 1))) + ")";
    }

    public override XElement DescribeXml()
    {
        var element = new XElement("SEQ");
        foreach (var item in _payables)
            element.Add(item.DescribeXml());
        return element;
    }

    public override HashSet<IPayable> Leafs()
    {
        var set = new HashSet<IPayable>();
        foreach (var item in _payables)
            set.UnionWith(item.Leafs());
        return set;
    }

    public override List<IPayable> GetChildren()
    {
        return _payables.ToList();
    }

    private void PayPayables(DateTime cfDate, double prin, Action<IPayable, double> pay, Action payRuleExec,
        bool ignoreLockedOut = false)
    {
        payRuleExec.Invoke();

        var amtRemaining = prin;
        foreach (var payable in _payables)
        {
            if (amtRemaining < .001)
                continue;

            if (payable.IsLockedOut(cfDate) && !ignoreLockedOut)
                continue;

            var amtToPay = amtRemaining;
            if (payable.CurrentBalance(cfDate) < amtToPay)
                amtToPay = payable.CurrentBalance(cfDate);

            pay.Invoke(payable, amtToPay);
            amtRemaining -= amtToPay;
        }

        if (amtRemaining > 2)
        {
            if (ignoreLockedOut)
            {
                // Check if all payables have zero balance - if so, remaining goes to residual (not an error)
                var totalPayableBalance = _payables.Sum(p => p.CurrentBalance(cfDate));
                if (totalPayableBalance > 0.01 && amtRemaining > 100)
                {
                    // Error only if there are tranches with balance that didn't receive principal
                    Exceptions.PrincipalDistributionException(this, cfDate,
                        $"Paying sequential {prin} but having remaing {amtRemaining} not able to distribute");
                }
                // Else: all tranches paid off, remaining principal goes to residual (cert holder)
            }
            else
            {
                PayPayables(cfDate, amtRemaining, pay, payRuleExec, true);
            }
        }
    }
}