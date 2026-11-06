namespace GraamFlows.Revolving;

/// <summary>A class's month, in the terms of the tranche output.</summary>
public sealed record ClassPeriod
{
    public int Period { get; init; }
    public DateTime DistributionDate { get; init; }
    public double BeginBalance { get; init; }
    public double EndBalance { get; init; }
    public double Factor { get; init; }
    public double InterestAccrued { get; init; }
    public double InterestPaid { get; init; }
    public double InterestDueUnpaid { get; init; }
    public double PrincipalPaid { get; init; }
    public double Writedown { get; init; }
    public double CumWritedown { get; init; }
    public double CouponPct { get; init; }
    public double? IndexValue { get; init; }
    public double Margin { get; init; }
    public int AccrualDays { get; init; }
}

/// <summary>A series' month.</summary>
public sealed record SeriesPeriod
{
    public int Period { get; init; }
    public DateTime DistributionDate { get; init; }
    public SeriesPhase Phase { get; init; }
    public double FloatingPct { get; init; }
    public double PrincipalPct { get; init; }
    public double FinanceAllocation { get; init; }

    /// <summary>What group reallocation gave the series. Null if the program does not reallocate.</summary>
    public double? ReallocatedFinance { get; init; }

    public double PrincipalAllocation { get; init; }
    public double InvestorDefault { get; init; }
    public double InvestedAmount { get; init; }
    public double AdjustedInvested { get; init; }
    public double FundingAccount { get; init; }
    public double FundingEarnings { get; init; }
    public double FundingDeposit { get; init; }
    public double FundingDeficit { get; init; }
    public double FundingRelease { get; init; }
    public double Reserve { get; init; }
    public double ReserveRequired { get; init; }
    public double ReserveDeposit { get; init; }
    public double ReserveRelease { get; init; }
    public double CarriedFinance { get; init; }
    public double FeesAccrued { get; init; }
    public double FeesPaid { get; init; }
    public double SharedPrincipalIn { get; init; }
    public double SharedPrincipalOut { get; init; }
    public double SharedExcessIn { get; init; }
    public double SharedExcessOut { get; init; }
    public double PortfolioYieldPct { get; init; }
    public double BaseRatePct { get; init; }

    /// <summary>Series yield less base rate. It is not computed from <see cref="ExcessDollars" />.</summary>
    public double ExcessSpreadPct { get; init; }

    /// <summary>Finance left after the waterfall. It is not computed from <see cref="ExcessSpreadPct" />.</summary>
    public double ExcessDollars { get; init; }

    public IReadOnlyList<TriggerReading> Triggers { get; init; } = Array.Empty<TriggerReading>();
}

/// <summary>Minimum pool or transferor test. It is reported every month and never trips.</summary>
public sealed record ReportingTest(string Name, double Required, double Actual, double Headroom);

/// <summary>The whole program's month.</summary>
public sealed record ProgramPeriod
{
    public required RevolvingPeriodDates Dates { get; init; }
    public required PoolPeriod Pool { get; init; }

    /// <summary>Ledger ending less the printed ending, on a replayed month that prints one.</summary>
    public double? LedgerBreak { get; init; }

    /// <summary>Printed beginning less the prior month's ending, on a replayed month.</summary>
    public double? RollBreak { get; init; }

    public double TransferorAmount { get; init; }
    public double TransferorFinance { get; init; }
    public double TransferorPrincipal { get; init; }

    /// <summary>Cash available this month: collections, earnings and opening accounts.</summary>
    public double CashIn { get; init; }

    /// <summary>Cash paid to classes, fees and the transferor, plus closing accounts.</summary>
    public double CashOut { get; init; }

    public IReadOnlyList<ReportingTest> ReportingTests { get; init; } = Array.Empty<ReportingTest>();
}

/// <summary>A move in a series' phase path.</summary>
public sealed record PhaseMove(SeriesPhase Phase, int Period, DateTime DistributionDate);

/// <summary>The first month a test tripped.</summary>
public sealed record FirstTrip(string Test, int Period, DateTime DistributionDate);

public sealed record SeriesResult
{
    public required string Name { get; init; }
    public required IReadOnlyList<SeriesPeriod> Periods { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<ClassPeriod>> Classes { get; init; }
    public required IReadOnlyList<PhaseMove> PhasePath { get; init; }
    public FirstTrip? FirstTrip { get; init; }
}

public sealed record RevolvingResult
{
    public required IReadOnlyList<ProgramPeriod> Periods { get; init; }
    public required IReadOnlyList<SeriesResult> Series { get; init; }

    /// <summary>What the run took as 0 because the request did not state it.</summary>
    public required IReadOnlyList<string> Disclosures { get; init; }
}
