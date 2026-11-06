using GraamFlows.Objects.DataObjects;
using GraamFlows.Revolving;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>
///     A de-linked note program in the shape of the Chase Issuance Trust's application of funds. Class
///     A requires 8.13953% of its adjusted amount of each of Class B and Class C, as the prospectus
///     states. Class B's 7.52688% is worked from printed amounts, not stated in a prospectus.
/// </summary>
internal static class NoteProgram
{
    public const double ClassARequiredPct = 8.13953;
    public const double ClassBRequiredPct = 7.52688;

    public static IReadOnlyList<WaterfallStep> FinanceSteps { get; } = new WaterfallStep[]
    {
        new(WaterfallVerb.PayInterest, "A"),
        new(WaterfallVerb.PayInterest, "B"),
        new(WaterfallVerb.PayInterest, "C"),
        new(WaterfallVerb.PayFee, Fee: "servicing"),
        new(WaterfallVerb.CoverDefaults, "A"),
        new(WaterfallVerb.CoverDefaults, "B"),
        new(WaterfallVerb.CoverDefaults, "C"),
        new(WaterfallVerb.ReimburseChargeoff, "A"),
        new(WaterfallVerb.ReimburseChargeoff, "B"),
        new(WaterfallVerb.ReimburseChargeoff, "C"),
        new(WaterfallVerb.FundReserve),
        new(WaterfallVerb.ShareExcess),
        new(WaterfallVerb.ReleaseTransferor)
    };

    public static IReadOnlyList<WaterfallStep> PrincipalSteps { get; } = new WaterfallStep[]
    {
        new(WaterfallVerb.ReallocatePrincipal, "A", From: new[] { "C", "B" }, Covers: ReallocationCovers.Interest),
        new(WaterfallVerb.ReallocatePrincipal, "B", From: new[] { "C" }, Covers: ReallocationCovers.Interest),
        new(WaterfallVerb.ReallocatePrincipal, "A", From: new[] { "C", "B" }, Covers: ReallocationCovers.Fee),
        new(WaterfallVerb.ReallocatePrincipal, "B", From: new[] { "C" }, Covers: ReallocationCovers.Fee),
        new(WaterfallVerb.DepositPfa),
        new(WaterfallVerb.SharePrincipal),
        new(WaterfallVerb.ReleaseTransferor)
    };

    /// <summary>The month's excess spread test, below zero on a three-month average.</summary>
    public static TriggerTest ExcessSpreadBelowZero { get; } = new()
    {
        Name = "EXCESS_SPREAD_BELOW_ZERO",
        Metric = TriggerMetric.ExcessSpread,
        Window = 3,
        Comparison = TriggerComparison.Below,
        Level = 0.0,
        Action = TriggerAction.EarlyAmortization
    };

    /// <param name="freeze">a test's choice. No real program's value is stated or implied here.</param>
    public static RevolvingProgram Program(PrincipalFreeze freeze = PrincipalFreeze.FrozenNumerator,
        IReadOnlyList<TriggerTest>? triggers = null, bool segregated = false, int? actionLag = 0)
    {
        return new RevolvingProgram
        {
            Name = "Example note trust",
            RecoveryBucket = RecoveryBucket.PrincipalUpToNewDefaults,
            PoolMeasure = PoolMeasure.PeriodStart,
            ActionLag = actionLag,
            PrincipalFreeze = freeze,
            FinanceSteps = FinanceSteps,
            PrincipalSteps = PrincipalSteps,
            Triggers = triggers ?? Array.Empty<TriggerTest>(),
            Delinked = new DelinkedNotes { ClassOrder = new[] { "A", "B", "C" }, SegregatedFinance = segregated }
        };
    }

    public static RevolvingSeries Tranche(string name, string key, double balance, double coupon = 5.0,
        double? requiredPct = null)
    {
        var required = key switch
        {
            "A" => new Dictionary<string, double>
            {
                ["B"] = requiredPct ?? ClassARequiredPct,
                ["C"] = requiredPct ?? ClassARequiredPct
            },
            "B" => new Dictionary<string, double> { ["C"] = requiredPct ?? ClassBRequiredPct },
            _ => new Dictionary<string, double>()
        };

        return new RevolvingSeries
        {
            Name = name,
            Group = "notes",
            Classes = new[] { new RevolvingClass { Key = key, Balance = balance, Coupon = coupon } },
            Fees = new[] { new RevolvingFee("servicing", 1.5) },
            WritedownOrder = new[] { key },
            FundedClasses = new[] { key },
            RequiredSubordination = required
        };
    }

    /// <summary>A tranche in accumulation, part way through its deposits.</summary>
    public static RevolvingSeries Accumulating(RevolvingSeries tranche, double frozenNumerator, double funding,
        double priorNumerator, double priorFunding, double[] controlled, DateTime start, DateTime expected)
    {
        return tranche with
        {
            Phase = SeriesPhase.Accumulating,
            FrozenNumerator = frozenNumerator,
            FundingAccount = funding,
            PriorNumerator = priorNumerator,
            PriorFunding = priorFunding,
            ControlledAmounts = controlled,
            AccumulationStart = start,
            ExpectedDate = expected
        };
    }

    public static ActualPeriod Month(double begin, double end, double principal, double finance, double gross,
        double recoveries, int days, double? average = null)
    {
        // The statements print no new receivables line, and their principal collections hold the
        // recoveries that land in principal.
        var collected = principal - recoveries;
        return new ActualPeriod
        {
            Beginning = begin,
            NewReceivables = end - begin + collected + gross,
            PrincipalCollections = collected,
            FinanceCollections = finance,
            Recoveries = recoveries,
            ChargeOffs = gross,
            Ending = end,
            Days = days,
            AllocationBalance = average ?? begin
        };
    }

    public static RevolvingResult Execute(RevolvingProgram program, DateTime statementDate,
        IReadOnlyList<ActualPeriod> months, IReadOnlyList<RevolvingSeries> tranches)
    {
        return RevolvingEngine.Run(new RevolvingRun
        {
            Program = program,
            StatementDate = statementDate,
            Periods = months.Count,
            PoolBalance = months[0].Beginning!.Value,
            ActualPeriods = months,
            Series = tranches,
            FundingEarningsRate = new[] { 0.0 }
        }, new ConstantRateProvider(4.0));
    }

    public static RevolvingResult Forecast(RevolvingProgram program, double pool, IReadOnlyList<RevolvingSeries> tranches,
        PoolDriverPath drivers, int periods, DateTime? statementDate = null)
    {
        return RevolvingEngine.Run(new RevolvingRun
        {
            Program = program,
            StatementDate = statementDate ?? new DateTime(2026, 7, 31),
            Periods = periods,
            PoolBalance = pool,
            Drivers = drivers,
            Series = tranches,
            FundingEarningsRate = new[] { 0.0 }
        }, new ConstantRateProvider(4.0));
    }
}
