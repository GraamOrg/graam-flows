using System.Xml.Linq;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Util;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall.Structures.PayableStructures;

/// <summary>
///     Principal split by FIXED shares: each leg is paid its stated percentage of the amount,
///     whatever the legs' balances ("concurrently, as follows: 1. 30.0074920518%, sequentially, to
///     AB, BF, BV and BZ ... 2. ..."). <see cref="ProrataStructure" /> splits by balance instead, and
///     the two part as soon as a leg's balance moves on its own: an accrual class in one leg grows
///     it, so a balance split hands that leg more than its share.
///     <para>
///         A leg that cannot take its share (retired, or holding less than its share) passes what
///         it could not take to the legs still holding a balance, in proportion to their shares, so
///         no principal is lost. A document that sizes its legs to retire together never reaches
///         this; one that does not leaves nothing unpaid.
///     </para>
///     Interest is split the same way: each leg its share of the funds, capped at the interest it is
///     due, and what a leg does not need offered to the others.
///     <para>
///         The DSL's <c>PROFORMA(A, 0.6, B, 0.4)</c> is a split whose shares are fractions.
///     </para>
/// </summary>
public class SplitStructure : BasePayable
{
    /// <summary>How far the shares may total from 100 (a document prints ten decimals).</summary>
    public const double ShareTolerance = 1e-6;

    private readonly IList<IPayable> _payables;
    private readonly IList<double> _shares;

    /// <summary>A split whose shares <see cref="Shares" /> states for each amount it pays.</summary>
    protected SplitStructure(IList<IPayable> payables)
    {
        _payables = payables;
        _shares = payables.Select(_ => 0.0).ToList();
    }

    /// <summary>What each payment is split by, in its kind.</summary>
    protected enum SplitAmount
    {
        Scheduled,
        Unscheduled,
        Recovery,
        Writedown,
        Interest
    }

    /// <summary>
    ///     Each leg's share of an amount of <paramref name="kind" />, in any unit (they are weights):
    ///     the fixed shares the split states.
    /// </summary>
    protected virtual IList<double> Shares(SplitAmount kind, DateTime cfDate)
    {
        return _shares;
    }

    public SplitStructure(IList<double> shares, IList<IPayable> payables)
    {
        if (shares.Count != payables.Count)
            throw new ArgumentException(
                $"A SPLIT states {shares.Count} shares for {payables.Count} legs; state one share per leg.");
        if (shares.Any(s => !double.IsFinite(s) || s <= 0))
            throw new ArgumentException("A SPLIT share is more than 0, in percent of the amount.");
        var total = shares.Sum();
        if (Math.Abs(total - 100.0) > ShareTolerance)
            throw new ArgumentException($"A SPLIT's shares total {total}%, not 100%.");
        _shares = shares;
        _payables = payables;
    }

    public override bool IsLeaf => false;

    public override PrincipalReceipt PaySp(IPayable parent, DateTime cfDate, double prin, Action payRuleExec)
    {
        var receipt = PrincipalReceipt.None;
        PayLegs(cfDate, prin, Shares(SplitAmount.Scheduled, cfDate),
            (payable, amt) => receipt += payable.PaySp(this, cfDate, amt, payRuleExec), payRuleExec,
            (p, d) => p.CurrentBalance(d));
        return receipt;
    }

    public override PrincipalReceipt PayUsp(IPayable parent, DateTime cfDate, double prin, Action payRuleExec)
    {
        var receipt = PrincipalReceipt.None;
        PayLegs(cfDate, prin, Shares(SplitAmount.Unscheduled, cfDate),
            (payable, amt) => receipt += payable.PayUsp(this, cfDate, amt, payRuleExec), payRuleExec,
            (p, d) => p.CurrentBalance(d));
        return receipt;
    }

    public override PrincipalReceipt PayRp(IPayable parent, DateTime cfDate, double prin, Action payRuleExec)
    {
        var receipt = PrincipalReceipt.None;
        PayLegs(cfDate, prin, Shares(SplitAmount.Recovery, cfDate),
            (payable, amt) => receipt += payable.PayRp(this, cfDate, amt, payRuleExec), payRuleExec,
            (p, d) => p.CurrentBalance(d));
        return receipt;
    }

    public override void PayWritedown(IPayable parent, DateTime cfDate, double amount, Action payRuleExec)
    {
        PayLegs(cfDate, amount, Shares(SplitAmount.Writedown, cfDate),
            (payable, amt) => payable.PayWritedown(this, cfDate, amt, payRuleExec), payRuleExec,
            (p, d) => p.WritedownCapacity(d));
    }

    /// <summary>
    ///     Each leg its share of <paramref name="amount" />, capped at what it can take; what a leg
    ///     could not take is offered again to the legs with room, by their shares, until nothing is
    ///     left or no leg has room.
    /// </summary>
    private void PayLegs(DateTime cfDate, double amount, IList<double> shares, Action<IPayable, double> pay,
        Action payRuleExec, Func<IPayable, DateTime, double> capFn)
    {
        payRuleExec.Invoke();
        var room = _payables.Select(p => Math.Max(capFn(p, cfDate), 0.0)).ToArray();
        var paid = new double[_payables.Count];
        var remaining = amount;
        for (var pass = 0; pass < _payables.Count && remaining > 0.001; pass++)
        {
            var open = Enumerable.Range(0, _payables.Count)
                .Where(i => room[i] - paid[i] > 0.001 && shares[i] > 0).ToList();
            if (open.Count == 0)
                break;
            var weight = open.Sum(i => shares[i]);
            var handed = 0.0;
            foreach (var i in open)
            {
                var take = Math.Min(remaining * shares[i] / weight, room[i] - paid[i]);
                paid[i] += take;
                handed += take;
            }

            remaining -= handed;
        }

        for (var i = 0; i < _payables.Count; i++)
            if (paid[i] > 0)
                pay.Invoke(_payables[i], paid[i]);

        // More than every leg holds: the residual's, as SequentialStructure treats it. Less than
        // the legs hold but not paid is not possible: each pass pays all a leg can take.
        if (remaining > 100 && room.Sum() - paid.Sum() > 0.01)
            Exceptions.PrincipalDistributionException(this, cfDate,
                $"Paying split {amount} but {remaining} was left with legs still holding a balance");
    }

    public override double PayInterest(IPayable caller, DateTime cfDate, double availableFunds,
        IRateProvider rateProvider, IEnumerable<DynamicTranche> allTranches)
    {
        var tranches = allTranches as IList<DynamicTranche> ?? allTranches?.ToList();
        var interestPaid = 0.0;
        PayLegs(cfDate, availableFunds, Shares(SplitAmount.Interest, cfDate),
            (payable, amt) => interestPaid += payable.PayInterest(this, cfDate, amt, rateProvider, tranches!),
            () => { },
            (p, d) => p.IsLockedOut(d) ? 0.0 : p.InterestDue(d, rateProvider, tranches!));
        return interestPaid;
    }

    public override double InterestDue(DateTime cfDate, IRateProvider rateProvider,
        IEnumerable<DynamicTranche> allTranches)
    {
        return _payables.Sum(p => p.InterestDue(cfDate, rateProvider, allTranches));
    }

    public override string Describe(int level)
    {
        var tabs = string.Concat(Enumerable.Repeat("\t", level));
        return tabs + "SPLIT(\n" + string.Join(",\n",
            _payables.Select((p, i) => $"{tabs}\t{_shares[i]}%:\n" + p.Describe(level + 1))) + ")";
    }

    public override XElement DescribeXml()
    {
        var element = new XElement("SPLIT");
        for (var i = 0; i < _payables.Count; i++)
        {
            var leg = new XElement("LEG", new XAttribute("share", _shares[i]));
            leg.Add(_payables[i].DescribeXml());
            element.Add(leg);
        }

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
}
