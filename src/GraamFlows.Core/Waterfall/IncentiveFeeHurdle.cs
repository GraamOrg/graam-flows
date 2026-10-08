using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Util;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Waterfall;

/// <summary>
///     Applies an <see cref="IncentiveFeeConfig" /> to the residual class's distributions, one
///     Payment Date at a time. One instance per waterfall run: it carries the running present value
///     (at the hurdle) of everything the residual has received, which is the only state the hurdle
///     needs — the amount still owed on date t is <c>(Investment − pv) × (1 + h)^t</c>.
/// </summary>
public sealed class IncentiveFeeHurdle
{
    private readonly IncentiveFeeConfig _cfg;
    private readonly double _hurdle;
    private readonly double _share;
    private double _pv;

    public IncentiveFeeHurdle(IncentiveFeeConfig cfg, IDeal deal)
    {
        // Resolve both names NOW. Apply only runs on the group holding the residual, so a misspelt
        // residual would match no group and the run would silently charge no fee at all.
        if (!deal.Tranches.Any(t => t.TrancheName == cfg.ResidualTranche && t.CashflowTypeEnum != CashflowType.Expense))
            throw new DealModelingException(deal.DealName,
                $"incentiveFee.residualTranche '{cfg.ResidualTranche}' is not a class of this deal.");
        if (!deal.Tranches.Any(t => t.TrancheName == cfg.FeeExpense && t.CashflowTypeEnum == CashflowType.Expense))
            throw new DealModelingException(deal.DealName,
                $"incentiveFee.feeExpense '{cfg.FeeExpense}' is not a declared expense of this deal. Declare it " +
                "in the deal's expenses (formula \"0\": the fee is booked by the hurdle, not by its formula).");

        _cfg = cfg;
        _hurdle = cfg.HurdleIrrPct / 100.0;
        _share = cfg.SharePct / 100.0;
        foreach (var (date, amount) in cfg.PriorDistributions)
            _pv += amount / Growth(date);
    }

    /// <summary>The residual's cumulative distributions discounted to the investment date at the hurdle.</summary>
    public double PresentValue => _pv;

    /// <summary>XIRR's compounding factor from the investment date: (1 + h)^(actual days / 365).</summary>
    public double Growth(DateTime date) =>
        Math.Pow(1.0 + _hurdle, (date - _cfg.InvestmentDate).TotalDays / 365.0);

    /// <summary>What the residual still needs on <paramref name="date" /> to have earned the hurdle.</summary>
    public double OwedOn(DateTime date) => Math.Max((_cfg.Investment - _pv) * Growth(date), 0.0);

    /// <summary>
    ///     Split this group's Payment Date distribution to the residual and book the fee. Returns the
    ///     fee taken (0 when the residual is not in this group or received nothing).
    ///     <paramref name="paymentDate" /> is the unadjusted period date — the IRR clock.
    /// </summary>
    public double Apply(DynamicGroup dynGroup, DateTime paymentDate)
    {
        var residual = dynGroup.DynamicClasses
            .SelectMany(dc => dc.DynamicTranches)
            .FirstOrDefault(t => t.Tranche.TrancheName == _cfg.ResidualTranche);
        if (residual == null)
            return 0.0;
        if (residual.Tranche.TrancheTypeEnum == TrancheTypeEnum.Certificate)
            throw new DealModelingException(residual.Tranche.DealName,
                $"incentiveFee.residualTranche '{_cfg.ResidualTranche}' is a Certificate class, whose " +
                "distributions are carried on its class cashflow; the incentive fee supports a residual " +
                "NOTE class (paid at EXCESS / as the residual), not a certificate.");

        var fee = dynGroup.ExpenseClasses
            .SelectMany(dc => dc.DynamicTranches)
            .FirstOrDefault(t => t.Tranche.TrancheName == _cfg.FeeExpense)
            ?? throw new DealModelingException(residual.Tranche.DealName,
                $"incentiveFee.feeExpense '{_cfg.FeeExpense}' is not a declared expense of group " +
                $"{dynGroup.GroupNum}. Declare it in the deal's expenses (formula \"0\": the fee is " +
                "booked by the hurdle, not by its formula).");

        if (!residual.Cashflows.TryGetValue(residual.AdjustedCashflowDate(paymentDate), out var cf))
            return 0.0;

        var interest = Math.Max(cf.Interest, 0.0);
        var principal = Math.Max(cf.ScheduledPrincipal, 0.0) + Math.Max(cf.UnscheduledPrincipal, 0.0);
        if (interest + principal <= 0.0)
            return 0.0;

        // Interest first, as the interest priority of payments runs before the principal one: the
        // residual takes interest up to what it still needs, the fee takes its share of the rest,
        // and principal meets whatever the interest left of the hurdle.
        var owed = OwedOn(paymentDate);
        var feeFromInterest = _share * Math.Max(interest - owed, 0.0);
        var owedAfterInterest = Math.Max(owed - interest, 0.0);
        var feeFromPrincipal = _share * Math.Max(principal - owedAfterInterest, 0.0);

        cf.Interest -= feeFromInterest;
        var fromUnscheduled = Math.Min(feeFromPrincipal, Math.Max(cf.UnscheduledPrincipal, 0.0));
        cf.UnscheduledPrincipal -= fromUnscheduled;
        cf.ScheduledPrincipal -= feeFromPrincipal - fromUnscheduled;

        var total = feeFromInterest + feeFromPrincipal;
        _pv += (interest + principal - total) / Growth(paymentDate);
        if (total > 0.0)
            fee.PayExpense(paymentDate, total, 0.0);
        return total;
    }
}
