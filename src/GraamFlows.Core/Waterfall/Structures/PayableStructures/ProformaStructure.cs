using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Util;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

public class ProformaStructure : BasePayable
{
    private const double Tolerance = .0001;
    private readonly List<Tuple<IPayable, double>> _proformaList = new();

    public ProformaStructure(IPayable payable)
    {
        _proformaList.Add(new Tuple<IPayable, double>(payable, 1));
    }

    public ProformaStructure(IPayable payable, double forma)
    {
        if (Math.Abs(forma - 1) > Tolerance)
            throw new DealModelingException("",
                $"Proforma amounts must sum up to 1 otherwise there will be lost cashflow. Sum is {forma}");

        _proformaList.Add(new Tuple<IPayable, double>(payable, 1));
    }

    public ProformaStructure(IPayable payable1, double forma1, IPayable payable2, double forma2)
    {
        if (Math.Abs(forma1 + forma2 - 1) > Tolerance)
            throw new DealModelingException("",
                $"Proforma amounts must sum up to 1 otherwise there will be lost cashflow. Sum is {forma1 + forma2}");

        _proformaList.Add(new Tuple<IPayable, double>(payable1, forma1));
        _proformaList.Add(new Tuple<IPayable, double>(payable2, 1 - forma1));
    }

    public ProformaStructure(IPayable payable1, double forma1, IPayable payable2, double forma2, IPayable payable3,
        double forma3)
    {
        if (Math.Abs(forma1 + forma2 + forma3 - 1) > Tolerance)
            throw new DealModelingException("",
                $"Proforma amounts must sum up to 1 otherwise there will be lost cashflow. Sum is {forma1 + forma2 + forma3}");

        _proformaList.Add(new Tuple<IPayable, double>(payable1, forma1));
        _proformaList.Add(new Tuple<IPayable, double>(payable2, forma2));
        _proformaList.Add(new Tuple<IPayable, double>(payable3, 1 - (forma1 + forma2)));
    }

    public override bool IsLeaf => false;

    public override void PaySp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayables(cfDate, prin, (payable, amt) => payable.PaySp(this, cfDate, amt, payRuleExec), payRuleExec);
    }

    public override void PayUsp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayables(cfDate, prin, (payable, amt) => payable.PayUsp(this, cfDate, amt, payRuleExec), payRuleExec);
    }

    public override void PayRp(IPayable caller, DateTime cfDate, double prin, Action payRuleExec)
    {
        PayPayables(cfDate, prin, (payable, amt) => payable.PayRp(this, cfDate, amt, payRuleExec), payRuleExec);
    }

    public override void PayWritedown(IPayable caller, DateTime cfDate, double amount, Action payRuleExec)
    {
        PayPayables(cfDate, amount, (payable, amt) => payable.PayWritedown(this, cfDate, amt, payRuleExec), payRuleExec);
    }

    public override double PayInterest(IPayable caller, DateTime cfDate, double availableFunds,
        IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches)
    {
        // Pay pro rata based on each payable's proforma share
        var interestPaid = 0.0;
        foreach (var (payable, share) in _proformaList)
        {
            var fundsForPayable = availableFunds * share;
            var paid = payable.PayInterest(this, cfDate, fundsForPayable, rateProvider, allTranches);
            interestPaid += paid;
        }
        return interestPaid;
    }

    public override double InterestDue(DateTime cfDate, IRateProvider rateProvider,
        IEnumerable<DynamicTranche> allTranches)
    {
        return _proformaList.Sum(p => p.Item1.InterestDue(cfDate, rateProvider, allTranches));
    }

    public override string Describe(int level)
    {
        var tabs = string.Concat(Enumerable.Repeat("\t", level));
        if (_proformaList.Count == 1)
            return tabs + $"PROFORMA({_proformaList[0].Item1.Describe(level + 1)}, {_proformaList[0].Item2})";
        if (_proformaList.Count == 2)
            return tabs + $"PROFORMA({_proformaList[0].Item1.Describe(level + 1)}, {_proformaList[0].Item2}, " +
                   $"{_proformaList[1].Item1.Describe(level + 1)}, {_proformaList[1].Item2})";
        if (_proformaList.Count == 3)
            return tabs + $"PROFORMA({_proformaList[0].Item1.Describe(level + 1)}, {_proformaList[0].Item2}, " +
                   $"{_proformaList[1].Item1.Describe(level + 1)}, {_proformaList[1].Item2}, " +
                   $"{_proformaList[2].Item1.Describe(level + 1)}, {_proformaList[2].Item2})";

        throw new NotImplementedException($"Only support up to 3 proformas but got {_proformaList.Count}");
    }

    public override XElement DescribeXml()
    {
        var element = new XElement("PROFORMA");
        foreach (var item in _proformaList)
        {
            var profItem = new XElement("ProformaItem");
            profItem.Add(new XAttribute("Share", item.Item2));
            profItem.Add(item.Item1.DescribeXml());
            element.Add(profItem);
        }

        return element;
    }

    public override HashSet<IPayable> Leafs()
    {
        var set = new HashSet<IPayable>();
        foreach (var item in _proformaList)
            set.UnionWith(item.Item1.Leafs());
        return set;
    }

    public override List<IPayable> GetChildren()
    {
        return _proformaList.Select(p => p.Item1).ToList();
    }

    private void PayPayables(DateTime cfDate, double prin, Action<IPayable, double> pay, Action payRuleExec)
    {
        payRuleExec.Invoke();
        var amtRemaining = prin;

        var i = 0;
        while (amtRemaining > 0.001 && i++ < 10)
        {
            var formaPrin = amtRemaining;
            foreach (var item in _proformaList)
            {
                var amtToPay = Math.Min(item.Item2 * formaPrin, item.Item1.CurrentBalance(cfDate));
                pay.Invoke(item.Item1, amtToPay);
                amtRemaining -= amtToPay;
            }
        }

        if (amtRemaining > 2)
            throw new PrincipalDistributionException(this, cfDate, $"Cannot distribute {amtRemaining}");
    }
}