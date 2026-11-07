using FluentAssertions;
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

    /// <param name="freeze">a test's choice. No real program's value is stated or implied here.</param>
    public static RevolvingProgram Program(RecoveryBucket bucket = RecoveryBucket.Finance, int? actionLag = 0,
        IReadOnlyList<TriggerTest>? triggers = null, PrincipalFreeze freeze = PrincipalFreeze.FrozenPercent)
    {
        return new RevolvingProgram
        {
            Name = "Example card trust",
            RecoveryBucket = bucket,
            PoolMeasure = PoolMeasure.PeriodStart,
            ActionLag = actionLag,
            PrincipalFreeze = freeze,
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

    /// <summary>
    ///     The months of a run, one request at a time on the printed figures it produced, each opening
    ///     on the last one's close and nothing else.
    /// </summary>
    public static IReadOnlyList<RevolvingResult> MonthByMonth(RevolvingRun run, RevolvingResult whole)
    {
        var months = new List<RevolvingResult>();
        var series = run.Series;
        var statement = run.StatementDate;
        foreach (var period in whole.Periods)
        {
            var month = Execute(run with
            {
                StatementDate = statement,
                Periods = 1,
                PoolBalance = period.Pool.Beginning,
                Drivers = null,
                ActualPeriods = new[] { Printed(period) },
                Series = series
            });
            months.Add(month);
            series = month.Series.Select(s => s.Closing).ToList();
            statement = new DateTime(statement.Year, statement.Month, 1).AddMonths(2).AddDays(-1);
        }

        return months;
    }

    private static ActualPeriod Printed(ProgramPeriod period)
    {
        return new ActualPeriod
        {
            Beginning = period.Pool.Beginning,
            NewReceivables = period.Pool.NewReceivables,
            PrincipalCollections = period.Pool.PrincipalCollections,
            FinanceCollections = period.Pool.FinanceCollections,
            Recoveries = period.Pool.Recoveries,
            ChargeOffs = period.Pool.ChargeOffs,
            Dilution = period.Pool.Dilution,
            Days = period.Dates.Days,
            AllocationBalance = period.Pool.AllocationBalance,
            InterestAccountEarnings = period.InterestAccountEarnings,
            PrincipalAccountEarnings = period.PrincipalAccountEarnings
        };
    }

    /// <summary>Every month's rows, on the long run and on the months cut one by one.</summary>
    public static void RequireSameMonths(RevolvingResult whole, IReadOnlyList<RevolvingResult> months)
    {
        for (var i = 0; i < months.Count; i++)
        {
            const double tiny = 1e-6;
            var because = $"month {i}";
            months[i].Periods[0].TransferorFinance.Should().BeApproximately(whole.Periods[i].TransferorFinance, tiny, because);
            months[i].Periods[0].TransferorPrincipal.Should().BeApproximately(whole.Periods[i].TransferorPrincipal, tiny, because);
            foreach (var series in whole.Series)
            {
                var cut = months[i].Series.Single(s => s.Name == series.Name);
                var want = series.Periods[i];
                var got = cut.Periods[0];
                var named = $"{series.Name} {because}";
                got.Phase.Should().Be(want.Phase, named);
                got.FeesAccrued.Should().BeApproximately(want.FeesAccrued, tiny, named);
                got.FeesPaid.Should().BeApproximately(want.FeesPaid, tiny, named);
                got.CarriedFinance.Should().BeApproximately(want.CarriedFinance, tiny, named);
                got.Reserve.Should().BeApproximately(want.Reserve, tiny, named);
                got.ReserveRequired.Should().BeApproximately(want.ReserveRequired, tiny, named);
                got.FundingAccount.Should().BeApproximately(want.FundingAccount, tiny, named);
                got.FundingDeficit.Should().BeApproximately(want.FundingDeficit, tiny, named);
                got.PrincipalPct.Should().BeApproximately(want.PrincipalPct, tiny, named);
                foreach (var (key, rows) in series.Classes)
                {
                    var row = cut.Classes[key][0];
                    row.InterestPaid.Should().BeApproximately(rows[i].InterestPaid, tiny, named);
                    row.InterestDueUnpaid.Should().BeApproximately(rows[i].InterestDueUnpaid, tiny, named);
                    row.PrincipalPaid.Should().BeApproximately(rows[i].PrincipalPaid, tiny, named);
                    row.EndBalance.Should().BeApproximately(rows[i].EndBalance, tiny, named);
                    row.CumWritedown.Should().BeApproximately(rows[i].CumWritedown, tiny, named);
                }
            }
        }
    }

    public static RevolvingResult Execute(RevolvingRun run)
    {
        return RevolvingEngine.Run(run, new ConstantRateProvider(4.0));
    }
}
