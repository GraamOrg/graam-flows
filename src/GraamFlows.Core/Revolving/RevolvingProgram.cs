using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Revolving;

/// <summary>The closed set of waterfall steps. A program is ordered data over these verbs.</summary>
public enum WaterfallVerb
{
    PayInterest,
    PayFee,
    CoverDefaults,
    ReimburseChargeoff,
    ReallocatePrincipal,
    DepositPfa,
    PayClass,
    FundReserve,
    ShareExcess,
    SharePrincipal,
    ReleaseTransferor
}

public static class WaterfallVerbs
{
    private static readonly Dictionary<string, WaterfallVerb> Names = new()
    {
        ["PAY_INTEREST"] = WaterfallVerb.PayInterest,
        ["PAY_FEE"] = WaterfallVerb.PayFee,
        ["COVER_DEFAULTS"] = WaterfallVerb.CoverDefaults,
        ["REIMBURSE_CHARGEOFF"] = WaterfallVerb.ReimburseChargeoff,
        ["REALLOCATE_PRINCIPAL"] = WaterfallVerb.ReallocatePrincipal,
        ["DEPOSIT_PFA"] = WaterfallVerb.DepositPfa,
        ["PAY_CLASS"] = WaterfallVerb.PayClass,
        ["FUND_RESERVE"] = WaterfallVerb.FundReserve,
        ["SHARE_EXCESS"] = WaterfallVerb.ShareExcess,
        ["SHARE_PRINCIPAL"] = WaterfallVerb.SharePrincipal,
        ["RELEASE_TRANSFEROR"] = WaterfallVerb.ReleaseTransferor
    };

    public static WaterfallVerb Parse(string? verb)
    {
        if (verb != null && Names.TryGetValue(verb.Trim().ToUpperInvariant(), out var parsed))
            return parsed;

        throw new RevolvingConfigException(
            $"Unknown waterfall verb '{verb}'. Supported: {string.Join(", ", Names.Keys)}.");
    }

    public static bool RunsOnFinance(WaterfallVerb verb)
    {
        return verb is not (WaterfallVerb.DepositPfa or WaterfallVerb.PayClass or WaterfallVerb.SharePrincipal);
    }

    public static bool RunsOnPrincipal(WaterfallVerb verb)
    {
        return verb is WaterfallVerb.DepositPfa or WaterfallVerb.PayClass or WaterfallVerb.SharePrincipal
            or WaterfallVerb.ReleaseTransferor;
    }
}

/// <param name="Verb">what the step does.</param>
/// <param name="Class">the class it acts on, for the verbs that act on one.</param>
/// <param name="Fee">the fee PAY_FEE pays.</param>
/// <param name="From">the classes REALLOCATE_PRINCIPAL takes from, most junior first.</param>
/// <param name="WhenPaid">classes that must be paid in full before PAY_CLASS runs.</param>
/// <param name="Phases">the phases the step runs in. Null means all of them.</param>
public sealed record WaterfallStep(
    WaterfallVerb Verb,
    string? Class = null,
    string? Fee = null,
    IReadOnlyList<string>? From = null,
    IReadOnlyList<string>? WhenPaid = null,
    IReadOnlyList<SeriesPhase>? Phases = null)
{
    public bool RunsIn(SeriesPhase phase)
    {
        return Phases == null || Phases.Contains(phase);
    }
}

public enum TriggerMetric
{
    PortfolioYield,
    BaseRate,
    ExcessSpread
}

public enum TriggerComparison
{
    Below,
    Above
}

public enum TriggerAction
{
    EarlyAmortization,
    FundReserve
}

public static class TriggerNames
{
    public static TriggerMetric ParseMetric(string test, string? metric)
    {
        return metric?.Trim().ToUpperInvariant() switch
        {
            "PORTFOLIO_YIELD" => TriggerMetric.PortfolioYield,
            "BASE_RATE" => TriggerMetric.BaseRate,
            "EXCESS_SPREAD" => TriggerMetric.ExcessSpread,
            _ => throw new RevolvingConfigException(
                $"Trigger '{test}' has unknown metric '{metric}'. Supported: PORTFOLIO_YIELD, BASE_RATE "
                + "and EXCESS_SPREAD.")
        };
    }

    public static TriggerComparison ParseComparison(string test, string? comparison)
    {
        return comparison?.Trim().ToUpperInvariant() switch
        {
            "BELOW" => TriggerComparison.Below,
            "ABOVE" => TriggerComparison.Above,
            _ => throw new RevolvingConfigException(
                $"Trigger '{test}' has unknown comparison '{comparison}'. Supported: BELOW and ABOVE.")
        };
    }

    public static TriggerAction ParseAction(string test, string? action)
    {
        return action?.Trim().ToUpperInvariant() switch
        {
            "EARLY_AMORTIZATION" => TriggerAction.EarlyAmortization,
            "FUND_RESERVE" => TriggerAction.FundReserve,
            _ => throw new RevolvingConfigException(
                $"Trigger '{test}' has unknown action '{action}'. Supported: EARLY_AMORTIZATION and "
                + "FUND_RESERVE.")
        };
    }
}

/// <summary>
///     A named test: the rolling average of a metric against a level or the rolling average of a
///     second metric, and what happens when it fails.
/// </summary>
public sealed record TriggerTest
{
    public required string Name { get; init; }
    public required TriggerMetric Metric { get; init; }
    public required int Window { get; init; }
    public required TriggerComparison Comparison { get; init; }
    public double? Level { get; init; }
    public TriggerMetric? AgainstMetric { get; init; }
    public required TriggerAction Action { get; init; }

    /// <summary>Good periods that cure a FUND_RESERVE test. EARLY_AMORTIZATION never cures.</summary>
    public int? CurePeriods { get; init; }

    /// <summary>The reserve a FUND_RESERVE test requires while it is tripped.</summary>
    public double? ReserveAmount { get; init; }
}

/// <summary>One program's rules: everything the series of a trust have in common.</summary>
public sealed record RevolvingProgram
{
    public required string Name { get; init; }
    public required RecoveryBucket RecoveryBucket { get; init; }
    public required PoolMeasure PoolMeasure { get; init; }

    /// <summary>
    ///     0 acts in the month a test trips, 1 in the next. It varies by program and has no default.
    /// </summary>
    public int? ActionLag { get; init; }

    public required IReadOnlyList<WaterfallStep> FinanceSteps { get; init; }
    public required IReadOnlyList<WaterfallStep> PrincipalSteps { get; init; }
    public IReadOnlyList<TriggerTest> Triggers { get; init; } = Array.Empty<TriggerTest>();

    /// <summary>
    ///     Percent of invested amounts added to them for the minimum pool test. Reported only.
    /// </summary>
    public double? SeriesRequiredTransferorPct { get; init; }

    /// <summary>Percent of the pool required as the transferor amount. Reported only.</summary>
    public double? RequiredTransferorPct { get; init; }
}

public sealed record RevolvingClass
{
    public required string Key { get; init; }

    /// <summary>Outstanding principal at the statement date.</summary>
    public required double Balance { get; init; }

    /// <summary>Fixed coupon in percent. A fixed class must state it. A floating class ignores it.</summary>
    public double? Coupon { get; init; }

    public MarketDataInstEnum? Index { get; init; }
    public double Margin { get; init; }
    public AccrualBasis Basis { get; init; } = AccrualBasis.Thirty360;

    /// <summary>Balance a factor is measured from. Defaults to <see cref="Balance" />.</summary>
    public double? OriginalBalance { get; init; }

    /// <summary>Reductions not yet reimbursed, from the statement.</summary>
    public double WrittenDown { get; init; }

    /// <summary>Interest due and unpaid, from the statement.</summary>
    public double InterestShortfall { get; init; }
}

/// <param name="Name">what PAY_FEE calls it.</param>
/// <param name="RatePct">annual percent of the adjusted invested amount, accrued 30/360.</param>
public sealed record RevolvingFee(string Name, double RatePct);

public sealed record RevolvingSeries
{
    public required string Name { get; init; }

    /// <summary>Sharing happens inside a group.</summary>
    public string Group { get; init; } = "";

    public bool PrincipalSharing { get; init; }
    public bool ExcessSharing { get; init; }
    public required IReadOnlyList<RevolvingClass> Classes { get; init; }
    public IReadOnlyList<RevolvingFee> Fees { get; init; } = Array.Empty<RevolvingFee>();

    /// <summary>Every class, in the order uncovered defaults write them down.</summary>
    public required IReadOnlyList<string> WritedownOrder { get; init; }

    /// <summary>The classes the funding account pays, in order.</summary>
    public IReadOnlyList<string> FundedClasses { get; init; } = Array.Empty<string>();

    /// <summary>First day of accumulation. Supplied by the caller; the engine never derives it.</summary>
    public DateTime? AccumulationStart { get; init; }

    public DateTime? ExpectedDate { get; init; }

    /// <summary>Controlled amount per accumulation month. The last value holds.</summary>
    public IReadOnlyList<double> ControlledAmounts { get; init; } = Array.Empty<double>();

    /// <summary>The first distribution the series' waterfall runs on. Null means the first one.</summary>
    public DateTime? FirstPayDate { get; init; }

    public double RequiredReserve { get; init; }

    public SeriesPhase Phase { get; init; } = SeriesPhase.Revolving;

    /// <summary>Required from the statement once a series has left revolving.</summary>
    public double? FrozenPrincipalPct { get; init; }

    public double FundingAccount { get; init; }
    public double FundingDeficit { get; init; }
    public double Reserve { get; init; }

    /// <summary>Months of accumulation already run, so a mid-period statement keeps its schedule.</summary>
    public int AccumulationMonthsRun { get; init; }

    /// <summary>Seeded monthly metric values, oldest first.</summary>
    public IReadOnlyDictionary<TriggerMetric, IReadOnlyList<double>> History { get; init; } =
        new Dictionary<TriggerMetric, IReadOnlyList<double>>();
}

/// <summary>
///     One forecast driver. Monthly values are fractions of beginning principal receivables; annual
///     values convert per month as <c>annual × days / 365</c>. A vector is read by forecast month, so
///     its first value is the first month after any replayed ones. A short vector holds its last value.
/// </summary>
public sealed record PoolDriver(IReadOnlyList<double> Values, bool Annual = false)
{
    public double At(int forecastMonth, int days)
    {
        var value = Values[Math.Min(forecastMonth, Values.Count - 1)];
        return Annual ? PoolLedger.MonthlyShare(value, days) : value;
    }
}

/// <summary>The forecast drivers. Those that are null are 0, and the run discloses it.</summary>
public sealed record PoolDriverPath
{
    public required PoolDriver PaymentRate { get; init; }
    public PoolDriver? NewReceivablesRate { get; init; }
    public bool HoldPool { get; init; }
    public required PoolDriver Yield { get; init; }
    public required PoolDriver ChargeOffRate { get; init; }
    public PoolDriver? RecoveryRate { get; init; }
    public PoolDriver? DilutionRate { get; init; }

    /// <param name="forecastMonth">0 is the first month after any replayed ones.</param>
    /// <param name="days">days in the month, for annual drivers.</param>
    public PoolDrivers For(int forecastMonth, int days)
    {
        return new PoolDrivers
        {
            PaymentRate = PaymentRate.At(forecastMonth, days),
            NewReceivablesRate = NewReceivablesRate?.At(forecastMonth, days) ?? 0.0,
            HoldPool = HoldPool,
            Yield = Yield.At(forecastMonth, days),
            ChargeOffRate = ChargeOffRate.At(forecastMonth, days),
            RecoveryRate = RecoveryRate?.At(forecastMonth, days) ?? 0.0,
            DilutionRate = DilutionRate?.At(forecastMonth, days) ?? 0.0
        };
    }
}

/// <summary>A month of printed pool figures, for replay.</summary>
public sealed record ActualPeriod
{
    /// <summary>Printed beginning balance. Null continues from the prior month's ending balance.</summary>
    public double? Beginning { get; init; }

    public required double NewReceivables { get; init; }
    public required double PrincipalCollections { get; init; }
    public required double FinanceCollections { get; init; }
    public double Recoveries { get; init; }
    public required double ChargeOffs { get; init; }
    public double Dilution { get; init; }

    /// <summary>Printed ending balance. The run reports its difference from the ledger.</summary>
    public double? Ending { get; init; }

    /// <summary>Days in the monthly period. Null uses the calendar month.</summary>
    public int? Days { get; init; }
}

/// <summary>
///     Everything one engine run needs. <see cref="Series" /> may be every series of a trust, or a
///     subject series plus one block for the rest of it: both are the same contract.
/// </summary>
public sealed record RevolvingRun
{
    public required RevolvingProgram Program { get; init; }

    /// <summary>Record date of the last closed monthly period. It must be a month end.</summary>
    public required DateTime StatementDate { get; init; }

    /// <summary>Day of the following month a period distributes on, before business-day roll.</summary>
    public int DistributionDay { get; init; } = 15;

    public required int Periods { get; init; }

    /// <summary>Principal receivables at the statement date.</summary>
    public required double PoolBalance { get; init; }

    public PoolDriverPath? Drivers { get; init; }
    public IReadOnlyList<ActualPeriod> ActualPeriods { get; init; } = Array.Empty<ActualPeriod>();
    public required IReadOnlyList<RevolvingSeries> Series { get; init; }

    /// <summary>
    ///     Annual percent the funding accounts earn, accrued 30/360 and swept to cash monthly. Null
    ///     is 0, and the run discloses it. Accounts earn in replayed months too, so a vector is read
    ///     by run month, unlike the forecast drivers.
    /// </summary>
    public IReadOnlyList<double>? FundingEarningsRate { get; init; }
}
