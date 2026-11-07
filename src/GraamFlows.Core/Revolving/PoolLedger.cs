namespace GraamFlows.Revolving;

/// <summary>Which collection bucket a program's recoveries land in.</summary>
public enum RecoveryBucket
{
    /// <summary>All recoveries are finance charge collections.</summary>
    Finance,

    /// <summary>
    ///     Recoveries are principal collections up to that month's new defaults, and only the
    ///     excess is finance charge collections.
    /// </summary>
    PrincipalUpToNewDefaults
}

public static class RecoveryBucketParser
{
    /// <summary>
    ///     Filings disagree on this, so it has no default: a program that does not state its bucket
    ///     is refused by name.
    /// </summary>
    public static RecoveryBucket Parse(string? program, string? bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new RevolvingConfigException(
                $"Program '{program}' states no recoveryBucket. Supplied programs must state "
                + "FINANCE or PRINCIPAL_UP_TO_NEW_DEFAULTS.");

        return bucket.Trim().ToUpperInvariant() switch
        {
            "FINANCE" => RecoveryBucket.Finance,
            "PRINCIPAL_UP_TO_NEW_DEFAULTS" => RecoveryBucket.PrincipalUpToNewDefaults,
            _ => throw new RevolvingConfigException(
                $"Program '{program}' has unknown recoveryBucket '{bucket}'. Supported: FINANCE and "
                + "PRINCIPAL_UP_TO_NEW_DEFAULTS.")
        };
    }
}

/// <summary>
///     One month's forecast drivers. Every rate is that month's dollars as a fraction of beginning
///     principal receivables, so all of them apply to the beginning balance, in parallel.
/// </summary>
public sealed record PoolDrivers
{
    public double PaymentRate { get; init; }

    /// <summary>Ignored under <see cref="HoldPool" />.</summary>
    public double NewReceivablesRate { get; init; }

    /// <summary>
    ///     Sets new receivables equal to principal collections. It replaces principal collections
    ///     only, so charge-offs and dilution still shrink the pool.
    /// </summary>
    public bool HoldPool { get; init; }

    /// <summary>Finance charge collections, excluding recoveries.</summary>
    public double Yield { get; init; }

    public double ChargeOffRate { get; init; }
    public double RecoveryRate { get; init; }
    public double DilutionRate { get; init; }
}

/// <summary>A month's printed dollars, for replay.</summary>
public sealed record PoolActuals
{
    public double Beginning { get; init; }
    public double NewReceivables { get; init; }
    public double PrincipalCollections { get; init; }

    /// <summary>Finance charge collections, excluding recoveries.</summary>
    public double FinanceCollections { get; init; }

    public double Recoveries { get; init; }
    public double ChargeOffs { get; init; }
    public double Dilution { get; init; }
}

/// <summary>One month of the whole program's pool: the receivables roll and the cash it collected.</summary>
public sealed record PoolPeriod
{
    public double Beginning { get; init; }
    public double NewReceivables { get; init; }
    public double PrincipalCollections { get; init; }
    public double ChargeOffs { get; init; }
    public double Dilution { get; init; }

    /// <summary>Finance charge collections, excluding recoveries.</summary>
    public double FinanceCollections { get; init; }

    public double Recoveries { get; init; }
    public double RecoveriesToFinance { get; init; }
    public double RecoveriesToPrincipal { get; init; }
    public double Ending { get; init; }

    /// <summary>The finance bucket: finance collections plus any recoveries that land in it.</summary>
    public double FinanceCash => FinanceCollections + RecoveriesToFinance;

    /// <summary>The principal bucket: principal collections plus any recoveries that land in it.</summary>
    public double PrincipalCash => PrincipalCollections + RecoveriesToPrincipal;
}

/// <summary>
///     The monthly roll of a revolving pool:
///     <c>ending = beginning + new receivables − principal collections − charge-offs − dilution</c>.
///     Recoveries are cash and never move the ledger. It works on the pool alone, so it does not
///     depend on any series and needs no iteration against the debt.
/// </summary>
public static class PoolLedger
{
    /// <summary>
    ///     An annual rate as a fraction of the month's beginning balance: <c>annual × days / 365</c>.
    ///     It is simple, never geometric, because that is how trusts print these rates.
    /// </summary>
    public static double MonthlyShare(double annualRate, int days)
    {
        return annualRate * days / 365.0;
    }

    public static PoolPeriod Roll(double beginning, PoolDrivers drivers, RecoveryBucket bucket)
    {
        var principal = beginning * drivers.PaymentRate;
        return Build(new PoolActuals
        {
            Beginning = beginning,
            PrincipalCollections = principal,
            NewReceivables = drivers.HoldPool ? principal : beginning * drivers.NewReceivablesRate,
            FinanceCollections = beginning * drivers.Yield,
            Recoveries = beginning * drivers.RecoveryRate,
            ChargeOffs = beginning * drivers.ChargeOffRate,
            Dilution = beginning * drivers.DilutionRate
        }, bucket);
    }

    /// <summary>Rolls a month of printed dollars. Nothing is rescaled.</summary>
    public static PoolPeriod Replay(PoolActuals actuals, RecoveryBucket bucket)
    {
        return Build(actuals, bucket);
    }

    private static PoolPeriod Build(PoolActuals a, RecoveryBucket bucket)
    {
        var ending = a.Beginning + a.NewReceivables - a.PrincipalCollections - a.ChargeOffs - a.Dilution;
        if (ending < 0)
            throw new RevolvingConfigException(
                $"Pool ledger went negative: {a.Beginning:F2} beginning + {a.NewReceivables:F2} new "
                + $"receivables - {a.PrincipalCollections:F2} principal - {a.ChargeOffs:F2} charge-offs "
                + $"- {a.Dilution:F2} dilution = {ending:F2}.");

        var toPrincipal = bucket == RecoveryBucket.PrincipalUpToNewDefaults
            ? Math.Min(a.Recoveries, a.ChargeOffs)
            : 0.0;

        return new PoolPeriod
        {
            Beginning = a.Beginning,
            NewReceivables = a.NewReceivables,
            PrincipalCollections = a.PrincipalCollections,
            ChargeOffs = a.ChargeOffs,
            Dilution = a.Dilution,
            FinanceCollections = a.FinanceCollections,
            Recoveries = a.Recoveries,
            RecoveriesToPrincipal = toPrincipal,
            RecoveriesToFinance = a.Recoveries - toPrincipal,
            Ending = ending
        };
    }
}
