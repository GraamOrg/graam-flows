using GraamFlows.Objects.DataObjects;
using GraamFlows.Revolving;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>
///     A series-stack card program in the shape of the American Express Credit Account Master Trust
///     priority of payments: Class A, Class B, then a Collateral Interest.
/// </summary>
internal static class CardProgram
{
    public const double ServicingPct = 2.0;

    private static readonly SeriesPhase[] Paying = { SeriesPhase.Amortizing, SeriesPhase.EarlyAmortization };

    public static IReadOnlyList<WaterfallStep> FinanceSteps { get; } = new WaterfallStep[]
    {
        new(WaterfallVerb.PayInterest, "A"),
        new(WaterfallVerb.CoverDefaults, "A"),
        new(WaterfallVerb.PayInterest, "B"),
        new(WaterfallVerb.ReallocatePrincipal, "A", From: new[] { "C", "B" }),
        new(WaterfallVerb.ReimburseChargeoff, "A"),
        new(WaterfallVerb.CoverDefaults, "B"),
        new(WaterfallVerb.ReallocatePrincipal, "B", From: new[] { "C" }),
        new(WaterfallVerb.ReimburseChargeoff, "B"),
        new(WaterfallVerb.PayInterest, "C"),
        new(WaterfallVerb.PayFee, Fee: "servicing"),
        new(WaterfallVerb.CoverDefaults, "C"),
        new(WaterfallVerb.ReimburseChargeoff, "C"),
        new(WaterfallVerb.FundReserve),
        new(WaterfallVerb.ShareExcess),
        new(WaterfallVerb.ReleaseTransferor)
    };

    public static IReadOnlyList<WaterfallStep> PrincipalSteps { get; } = new WaterfallStep[]
    {
        new(WaterfallVerb.DepositPfa, Phases: new[] { SeriesPhase.Accumulating }),
        new(WaterfallVerb.PayClass, "A", Phases: Paying),
        new(WaterfallVerb.PayClass, "B", WhenPaid: new[] { "A" }, Phases: Paying),
        new(WaterfallVerb.PayClass, "C", WhenPaid: new[] { "A", "B" }),
        new(WaterfallVerb.SharePrincipal),
        new(WaterfallVerb.ReleaseTransferor)
    };

    /// <summary>Three-month series yield below the three-month base rate.</summary>
    public static TriggerTest YieldBelowBaseRate { get; } = new()
    {
        Name = "YIELD_BELOW_BASE_RATE",
        Metric = TriggerMetric.PortfolioYield,
        Window = 3,
        Comparison = TriggerComparison.Below,
        AgainstMetric = TriggerMetric.BaseRate,
        Action = TriggerAction.EarlyAmortization
    };

    public static RevolvingProgram Program(RecoveryBucket bucket = RecoveryBucket.Finance, int? actionLag = 0,
        IReadOnlyList<TriggerTest>? triggers = null)
    {
        return new RevolvingProgram
        {
            Name = "Example card trust",
            RecoveryBucket = bucket,
            PoolMeasure = PoolMeasure.PeriodStart,
            ActionLag = actionLag,
            FinanceSteps = FinanceSteps,
            PrincipalSteps = PrincipalSteps,
            Triggers = triggers ?? Array.Empty<TriggerTest>()
        };
    }

    public static RevolvingSeries Series(string name, double a, double b, double c,
        double couponA = 5.0, double couponB = 5.5, double couponC = 7.0, string group = "1")
    {
        return new RevolvingSeries
        {
            Name = name,
            Group = group,
            PrincipalSharing = true,
            ExcessSharing = true,
            Classes = new[]
            {
                new RevolvingClass { Key = "A", Balance = a, Coupon = couponA },
                new RevolvingClass { Key = "B", Balance = b, Coupon = couponB },
                new RevolvingClass { Key = "C", Balance = c, Coupon = couponC }
            },
            Fees = new[] { new RevolvingFee("servicing", ServicingPct) },
            WritedownOrder = new[] { "C", "B", "A" },
            FundedClasses = new[] { "A", "B" }
        };
    }

    public static PoolDriverPath Drivers(double paymentRate = 0.40, double? newReceivables = 0.40,
        double yield = 0.02, double chargeOff = 0.002, double recovery = 0.0005, double dilution = 0.0)
    {
        return new PoolDriverPath
        {
            PaymentRate = Flat(paymentRate),
            NewReceivablesRate = newReceivables is { } rate ? Flat(rate) : null,
            HoldPool = newReceivables == null,
            Yield = Flat(yield),
            ChargeOffRate = Flat(chargeOff),
            RecoveryRate = Flat(recovery),
            DilutionRate = Flat(dilution)
        };
    }

    public static PoolDriver Flat(double value)
    {
        return new PoolDriver(new[] { value });
    }

    public static RevolvingRun Run(RevolvingProgram program, double pool, IReadOnlyList<RevolvingSeries> series,
        PoolDriverPath? drivers, int periods, DateTime? statementDate = null)
    {
        return new RevolvingRun
        {
            Program = program,
            StatementDate = statementDate ?? new DateTime(2026, 7, 31),
            Periods = periods,
            PoolBalance = pool,
            Drivers = drivers,
            Series = series,
            FundingEarningsRate = new[] { 0.0 }
        };
    }

    public static RevolvingResult Execute(RevolvingRun run)
    {
        return RevolvingEngine.Run(run, new ConstantRateProvider(4.0));
    }
}
