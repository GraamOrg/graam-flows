namespace GraamFlows.Revolving;

/// <summary>What a revolving program's pool is made of.</summary>
public enum PoolModel
{
    /// <summary>One balance rolled by rates, with finance and principal buckets (<see cref="PoolLedger" />).</summary>
    Card,

    /// <summary>
    ///     Cohorts of fixed 0% installment contracts, each a schedule of remaining payments, valued at
    ///     the present value of those payments at a discount rate (<see cref="InstallmentPoolLedger" />).
    /// </summary>
    Installment
}

public static class PoolModelParser
{
    /// <summary>Absent is <see cref="PoolModel.Card" />, the pool every existing program has.</summary>
    public static PoolModel Parse(string? model)
    {
        return model?.Trim().ToUpperInvariant() switch
        {
            null => PoolModel.Card,
            "CARD" => PoolModel.Card,
            "INSTALLMENT" => PoolModel.Installment,
            _ => throw new RevolvingConfigException(
                $"Unknown pool model '{model}'. Supported: CARD and INSTALLMENT.")
        };
    }
}

/// <summary>
///     Contracts that pay the same schedule. A 0% contract has no interest, so its face balance is the
///     sum of its remaining payments.
/// </summary>
public sealed record InstallmentCohort
{
    public string Name { get; init; } = "";

    /// <summary>
    ///     Months since the contracts were bought as the run's first month opens. It reads the forecast
    ///     curves. A cohort bought at the close of a month opens the next month at 0.
    /// </summary>
    public required int Age { get; init; }

    /// <summary>
    ///     Remaining scheduled payments by month: the first falls due at the close of the next month.
    ///     It is stated, never derived from a term.
    /// </summary>
    public required IReadOnlyList<double> Schedule { get; init; }

    public double Face => Schedule.Sum();

    /// <summary>Fresh level contracts: <paramref name="face" /> paid in <paramref name="term" /> equal payments.</summary>
    public static IReadOnlyList<double> LevelSchedule(double face, int term)
    {
        return Enumerable.Repeat(face / term, term).ToArray();
    }
}

/// <summary>What a purchase or a repurchase pays: a share of face, or the present value at a rate.</summary>
public sealed record InstallmentPrice
{
    /// <summary>Cash per dollar of face, as a fraction.</summary>
    public double? PerFace { get; init; }

    /// <summary>Annual rate, as a fraction: cash is the present value of the payments at it.</summary>
    public double? PvRate { get; init; }
}

/// <summary>Cohorts bought at the close of a month.</summary>
public sealed record InstallmentPurchase
{
    public required InstallmentCohort Cohort { get; init; }

    /// <summary>One of this or <see cref="Cash" />.</summary>
    public InstallmentPrice? Price { get; init; }

    /// <summary>The cash paid, where it is printed. One of this or <see cref="Price" />.</summary>
    public double? Cash { get; init; }

    /// <summary>
    ///     The run month a forecast purchase closes in. Null on a replayed month's additions, which
    ///     belong to that month.
    /// </summary>
    public int? Period { get; init; }
}

/// <summary>
///     A rate by cohort age, as a monthly fraction of the cohort's schedule. An annual value converts
///     geometrically, as CPR to SMM: <c>1 − (1 − annual)^(1/12)</c>. A short vector holds its last value,
///     so one value is a flat rate.
/// </summary>
public sealed record InstallmentCurve(IReadOnlyList<double> Values, bool Annual = false)
{
    public double At(int age)
    {
        var value = Values[Math.Min(Math.Max(age, 0), Values.Count - 1)];
        return Annual ? 1.0 - Math.Pow(1.0 - value, 1.0 / 12.0) : value;
    }
}

/// <summary>One term of the contracts a reinvestment buys, and its share of the face bought.</summary>
public sealed record InstallmentTerm(int Term, double Weight);

/// <summary>
///     Spends a share of each forecast month's collections on fresh level contracts at a price, as a
///     card pool's hold-pool sets new receivables to principal collections. Write-offs and repurchases
///     still shrink the pool.
/// </summary>
public sealed record InstallmentReinvest
{
    /// <summary>The share of the month's collections spent, as a fraction.</summary>
    public required double Share { get; init; }

    public required InstallmentPrice Price { get; init; }

    /// <summary>The terms bought; their weights are shares of the face bought and come to 1.</summary>
    public required IReadOnlyList<InstallmentTerm> Terms { get; init; }
}

/// <summary>The forecast drivers. Curves read a cohort's age; repurchases read the forecast month.</summary>
public sealed record InstallmentDrivers
{
    /// <summary>Prepayments in full, as a share of the schedule left after the month's payment.</summary>
    public required InstallmentCurve Prepayment { get; init; }

    /// <summary>Upgrades: paid off as a prepayment is, on their own cash line. Null is 0, disclosed.</summary>
    public InstallmentCurve? Upgrade { get; init; }

    /// <summary>Gross write-offs, as a share of the whole schedule as the month opens. No recovery line.</summary>
    public required InstallmentCurve WriteOff { get; init; }

    /// <summary>Face repurchased by forecast month; a month past the end repurchases nothing.</summary>
    public IReadOnlyList<double> RepurchaseAmounts { get; init; } = Array.Empty<double>();

    /// <summary>Required where any repurchase amount is above 0.</summary>
    public InstallmentPrice? RepurchasePrice { get; init; }

    /// <summary>Null buys nothing but the stated purchases, disclosed.</summary>
    public InstallmentReinvest? Reinvest { get; init; }
}

/// <summary>A month of printed pool figures, for replay. Every amount is dollars of face or cash.</summary>
public sealed record InstallmentActual
{
    /// <summary>Printed beginning face. The run reports its difference from the ledger and never resets to it.</summary>
    public double? BeginningFace { get; init; }

    public required double ScheduledCollections { get; init; }
    public required double PrepaidCollections { get; init; }
    public double UpgradeCollections { get; init; }
    public required double WriteOffs { get; init; }

    /// <summary>Face repurchased.</summary>
    public double Repurchases { get; init; }

    /// <summary>Printed repurchase cash. One of this or <see cref="RepurchasePrice" /> where there are repurchases.</summary>
    public double? RepurchaseCash { get; init; }

    public InstallmentPrice? RepurchasePrice { get; init; }

    /// <summary>Cohorts bought at the month's close.</summary>
    public IReadOnlyList<InstallmentPurchase> Additions { get; init; } = Array.Empty<InstallmentPurchase>();

    /// <summary>Printed ending face. The run reports its difference from the ledger.</summary>
    public double? EndingFace { get; init; }
}

/// <summary>Everything a pool-only run needs.</summary>
public sealed record InstallmentPoolRun
{
    /// <summary>Record date of the last closed monthly period. It must be a month end.</summary>
    public required DateTime StatementDate { get; init; }

    public required int Periods { get; init; }

    /// <summary>The pool at the statement date.</summary>
    public required IReadOnlyList<InstallmentCohort> Cohorts { get; init; }

    /// <summary>Annual rates, as fractions, the pool is valued at each month.</summary>
    public IReadOnlyList<double> DiscountRates { get; init; } = Array.Empty<double>();

    public InstallmentDrivers? Drivers { get; init; }
    public IReadOnlyList<InstallmentActual> ActualPeriods { get; init; } = Array.Empty<InstallmentActual>();

    /// <summary>Purchases in forecast months, each naming its month.</summary>
    public IReadOnlyList<InstallmentPurchase> Purchases { get; init; } = Array.Empty<InstallmentPurchase>();
}

/// <summary>
///     The pool's present value at one rate: <c>Σ pay_k / (1 + r/12)^k</c>, payments at month end. Each
///     removal is the present value, at the month's close, of the part of the schedule it took, so
///     <c>Ending = Beginning × (1 + r/12) − ScheduledDue − removals + Additions</c>.
/// </summary>
public sealed record InstallmentValuation
{
    /// <summary>Annual, as a fraction.</summary>
    public double Rate { get; init; }

    public double Beginning { get; init; }
    public double WriteOffs { get; init; }
    public double Repurchases { get; init; }
    public double PrepaidCollections { get; init; }
    public double UpgradeCollections { get; init; }

    /// <summary>On a replayed month, the value of printed scheduled collections less what fell due.</summary>
    public double ScheduledVariance { get; init; }

    public double Additions { get; init; }
    public double Ending { get; init; }
}

/// <summary>One month of an installment pool.</summary>
public sealed record InstallmentPoolPeriod
{
    public int Period { get; init; }
    public DateTime PeriodEnd { get; init; }
    public bool Replayed { get; init; }
    public double BeginningFace { get; init; }

    /// <summary>What the schedule had falling due this month, after write-offs and repurchases.</summary>
    public double ScheduledDue { get; init; }

    /// <summary>The printed amount on a replayed month; what fell due in a forecast one.</summary>
    public double ScheduledCollections { get; init; }

    public double PrepaidCollections { get; init; }
    public double UpgradeCollections { get; init; }

    /// <summary>The one collections bucket: scheduled, prepaid and upgrades.</summary>
    public double Collections => ScheduledCollections + PrepaidCollections + UpgradeCollections;

    public double WriteOffs { get; init; }

    /// <summary>Face repurchased.</summary>
    public double Repurchases { get; init; }

    public double RepurchaseCash { get; init; }
    public double AdditionsFace { get; init; }
    public double AdditionsCash { get; init; }
    public double EndingFace { get; init; }

    /// <summary>Cohorts with a payment left at the close.</summary>
    public int Cohorts { get; init; }

    /// <summary>Printed beginning face less the ledger's, on a replayed month that prints one.</summary>
    public double? RollBreak { get; init; }

    /// <summary>Ledger ending face less the printed one, on a replayed month that prints one.</summary>
    public double? LedgerBreak { get; init; }

    /// <summary>One per discount rate, in the order the run lists them.</summary>
    public IReadOnlyList<InstallmentValuation> Valuations { get; init; } = Array.Empty<InstallmentValuation>();
}

public sealed record InstallmentPoolResult
{
    public required IReadOnlyList<InstallmentPoolPeriod> Periods { get; init; }

    /// <summary>The pool as the run closed it, ages advanced: a next run opens on this.</summary>
    public required IReadOnlyList<InstallmentCohort> Closing { get; init; }

    /// <summary>What the run took as 0 or none because the request did not state it.</summary>
    public required IReadOnlyList<string> Disclosures { get; init; }
}
