using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Util;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

public class ProrataStructure : BasePayable
{
    private readonly IList<IPayable> _payables;

    public ProrataStructure(IList<IPayable> payables)
    {
        _payables = payables;
    }

    public override bool IsLeaf => false;

    public override void PaySp(IPayable parent, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayables(parent, _payables, cfDate, prin, (payable, amt) => payable.PaySp(this, cfDate, amt, payRuleExec),
            payRuleExec);
    }

    public override void PayUsp(IPayable parent, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayables(parent, _payables, cfDate, prin, (payable, amt) => payable.PayUsp(this, cfDate, amt, payRuleExec),
            payRuleExec);
    }

    public override void PayRp(IPayable parent, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayables(parent, _payables, cfDate, prin, (payable, amt) => payable.PayRp(this, cfDate, amt, payRuleExec),
            payRuleExec);
    }

    public override void PayWritedown(IPayable parent, DateTime cfDate, double amount, Action payRuleExec)
    {
        PayPayables(parent, _payables, cfDate, amount,
            (payable, amt) => payable.PayWritedown(this, cfDate, amt, payRuleExec), payRuleExec);
    }

    public override double PayInterest(IPayable caller, DateTime cfDate, double availableFunds,
        IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches)
    {
        // Calculate interest due for each payable
        var interestDueByPayable = _payables
            .Where(p => !p.IsLockedOut(cfDate))
            .ToDictionary(p => p, p => p.InterestDue(cfDate, rateProvider, allTranches));

        var totalInterestDue = interestDueByPayable.Values.Sum();
        if (totalInterestDue < 0.01)
            return 0;

        var interestPaid = 0.0;

        // Distribute pro rata based on interest due
        foreach (var (payable, interestDue) in interestDueByPayable)
        {
            if (interestDue < 0.01)
                continue;

            // Pro rata share based on interest due (not balance)
            var share = interestDue / totalInterestDue;
            var fundsForPayable = Math.Min(availableFunds * share, interestDue);

            var paid = payable.PayInterest(this, cfDate, fundsForPayable, rateProvider, allTranches);
            interestPaid += paid;
        }

        return interestPaid;
    }

    public override double InterestDue(DateTime cfDate, IRateProvider rateProvider,
        IEnumerable<DynamicTranche> allTranches)
    {
        return _payables.Sum(p => p.InterestDue(cfDate, rateProvider, allTranches));
    }

    private void PayPayables(IPayable parent, IList<IPayable> payables, DateTime cfDate, double prin,
        Action<IPayable, double> pay, Action payRuleExec, bool ignoreLockout = false)
    {
        payRuleExec.Invoke();
        var payStack = new Stack<IPayable>();
        double resi = 0, amtPaid = 0;
        foreach (var payable in payables)
        {
            var prorataPrin = GetProRataPrincipal(cfDate, payable, payables, prin, ignoreLockout);
            prorataPrin += resi;
            resi = 0;

            var amtToPay = prorataPrin;
            if (prorataPrin > payable.CurrentBalance(cfDate))
            {
                amtToPay = payable.CurrentBalance(cfDate);
                resi = prorataPrin - amtToPay;
            }

            // lockout waterfall handling
            var lockedOutBal = payable.LockedOutBalance(cfDate);
            var curbal = payable.CurrentBalance(cfDate);

            if (amtToPay > curbal - lockedOutBal)
                if (payables.Where(p => p != payable).Sum(p => p.CurrentBalance(cfDate)) > 0)
                    // here we should take the remaining principal and pay the other payables. Maybe we should create a residual action to handle? that may prevent and recursive requirements
                    if (parent == null) // TODO: check why does parent need to be null to be in here?
                    {
                        var loresid = amtToPay - (curbal - lockedOutBal);
                        amtToPay -= loresid;
                        PayStack(null, payStack.ToList(), cfDate, loresid, pay, payRuleExec);
                        amtPaid += loresid;
                    }

            payStack.Push(payable);
            amtPaid += amtToPay;
            pay.Invoke(payable, amtToPay);
        }

        if (Math.Abs(amtPaid - prin) > 1)
        {
            // We can only get in here if all the principal could not be paid. Before failing, this tries to distribute the remaining principal.
            // First we will try to distribute the principal to the sibling payables
            // And then we try to bubble it to the parent.
            // If that doesn't work, too bad.

            var remPrin = prin - amtPaid;

            if (payables.Sum(p => p.CurrentBalance(cfDate)) + 1 > remPrin)
                PayPayables(parent, payables, cfDate, remPrin, pay, payRuleExec, true);
            else if (_payables.Sum(p => p.CurrentBalance(cfDate)) + 1 > remPrin)
                PayPayables(parent, _payables, cfDate, remPrin, pay, payRuleExec, true);
            else if (parent != null && parent.CurrentBalance(cfDate) >= remPrin)
                pay.Invoke(parent, remPrin);
            else
                Exceptions.PrincipalDistributionException(this, cfDate,
                    $"Paying Prorata {prin} but only paid {amtPaid}. Cause lost cashflow of {prin - amtPaid}");
        }
    }

    private void PayStack(IPayable parent, IList<IPayable> payables, DateTime cfDate, double prin,
        Action<IPayable, double> pay, Action payRuleExec)
    {
        PayPayables(parent, payables, cfDate, prin, pay, payRuleExec);
    }

    public override string Describe(int level)
    {
        var tabs = string.Concat(Enumerable.Repeat("\t", level));
        return tabs + "PRORATA(" + string.Join(",\n", _payables.Select(p => "\n" + p.Describe(level + 1))) + ")";
    }

    public override XElement DescribeXml()
    {
        var element = new XElement("PRORATA");
        foreach (var item in _payables) element.Add(item.DescribeXml());

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

    private static double GetProRataPrincipal(DateTime cfDate, IPayable payable, IList<IPayable> payables, double prin,
        bool ignoreLockout = false)
    {
        if (Math.Abs(prin) < .01)
            return 0;

        if (!ignoreLockout && payable.IsLockedOut(cfDate))
            return 0;

        var lockedOut = 0.0;
        if (!ignoreLockout)
            lockedOut = payables.Where(p => p.IsLockedOut(cfDate)).Sum(p => p.BeginBalance(cfDate));

        // all lockouts from denom to numer
        var numer = payable.BeginBalance(cfDate);
        var denom = payables.Sum(p => p.BeginBalance(cfDate)) - lockedOut;
        if (Math.Abs(numer) < .01 || Math.Abs(denom) < .01)
            return 0;
        var prorataPct = numer / denom;
        var cashflow = prin * prorataPct;
        return cashflow;
    }
}