namespace GraamFlows.Objects.DataObjects;

/// <summary>
///     One period's asset reinvestment: collateral bought with principal proceeds.
///
///     The bought collateral itself is NOT reported here. It joins the collateral pool, so its
///     later interest, principal, defaults and recoveries appear in the ordinary collateral
///     cashflows. This record is the ACT of buying — cash leaving principal — which is the one
///     thing the collateral cashflows cannot show on their own, because they are already net of it.
///
///     Interest earned on idle cash (cash reinvestment) is a different mechanism and is never
///     recorded here.
/// </summary>
public record ReinvestmentPurchase
{
    /// <summary>Zero-based projection period of the purchase.</summary>
    public int Period { get; init; }

    /// <summary>Date of the collateral period the purchase is drawn from.</summary>
    public DateTime CashflowDate { get; init; }

    /// <summary>Cash spent buying collateral this period.</summary>
    public double CashSpent { get; init; }

    /// <summary>Portion of <see cref="CashSpent" /> drawn from scheduled principal.</summary>
    public double FromScheduledPrincipal { get; init; }

    /// <summary>Portion of <see cref="CashSpent" /> drawn from prepayments.</summary>
    public double FromUnscheduledPrincipal { get; init; }

    /// <summary>Portion of <see cref="CashSpent" /> drawn from recoveries.</summary>
    public double FromRecoveryPrincipal { get; init; }

    /// <summary>
    ///     Face amount of collateral bought. Equals <see cref="CashSpent" /> at par; exceeds it by
    ///     the purchase discount when bought below par.
    /// </summary>
    public double FaceBought { get; init; }

    /// <summary>Eligible principal proceeds available to reinvest this period, after holdback.</summary>
    public double ProceedsAvailable { get; init; }

    /// <summary>Collateral balance at the end of the period before this purchase.</summary>
    public double PoolBalanceBefore { get; init; }

    /// <summary>Balance target in force this period.</summary>
    public double TargetBalance { get; init; }

    /// <summary>The purchase split by reinvestment template, in template order.</summary>
    public IReadOnlyList<ReinvestmentTemplatePurchase> ByTemplate { get; init; } =
        Array.Empty<ReinvestmentTemplatePurchase>();
}

/// <summary>One template's share of a period's purchase.</summary>
public record ReinvestmentTemplatePurchase
{
    /// <summary>Zero-based index of the template in the reinvestment config.</summary>
    public int TemplateIndex { get; init; }

    /// <summary>Cash spent on this template.</summary>
    public double CashSpent { get; init; }

    /// <summary>Face bought: cash over the template's effective price.</summary>
    public double FaceBought { get; init; }

    /// <summary>Effective purchase price used, percent of par.</summary>
    public double Price { get; init; }
}

/// <summary>
///     The reinvestment loop's output: the bought collateral's cashflows (to merge into the pool,
///     including the redirect of the principal that paid for it) and the purchases themselves.
/// </summary>
public record ReinvestmentResult(IList<PeriodCashflows> Cashflows, IList<ReinvestmentPurchase> Purchases)
{
    public static ReinvestmentResult Empty =>
        new(new List<PeriodCashflows>(), new List<ReinvestmentPurchase>());
}
