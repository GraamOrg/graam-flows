using GraamFlows.Assumptions;
using GraamFlows.Domain;
using GraamFlows.Factories;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using GraamFlows.RulesEngine;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall.MarketTranche;

namespace GraamFlows.Tests.Fixtures;

/// <summary>
/// Fluent builder for constructing minimal Deal objects for unit testing
/// the ComposableStructure waterfall engine.
/// </summary>
public class TestDealBuilder
{
    private readonly Deal _deal;
    private readonly DateTime _projectionDate;
    private readonly DateTime _firstPayDate;
    private readonly List<(string Name, double Balance, double Coupon, int SubOrder)> _tranches = new();
    private readonly List<string> _payRuleFormulas = new();
    private List<string>? _executionOrder;
    private string _interestTreatment = "Collateral";
    private double _balanceAtIssuance;
    private WaterfallOrderEnum _waterfallOrder = WaterfallOrderEnum.Standard;
    private OcTargetConfig? _ocTargetConfig;
    private ReinvestmentConfig? _reinvestmentConfig;
    private List<CoverageLevelConfig>? _coverageCascade;

    public TestDealBuilder(
        string dealName = TestConstants.DefaultDealName,
        DateTime? projectionDate = null)
    {
        _projectionDate = projectionDate ?? TestConstants.DefaultProjectionDate;
        _firstPayDate = _projectionDate.AddMonths(1);
        _deal = new Deal(dealName, _projectionDate);
        _deal.CashflowEngine = "ComposableStructure";
        _deal.WaterfallType = "ComposableStructure";
    }

    private string? _firstPeriodCollateralPolicy;
    private DateTime? _collateralAccrualStartDate;

    public TestDealBuilder WithInterestTreatment(string treatment)
    {
        _interestTreatment = treatment;
        return this;
    }

    /// <summary>
    ///     Pay frequency for every tranche added SO FAR (12 monthly, 4 quarterly, 2
    ///     semi-annual), with the matching first pay date. Quarterly matters here: the
    ///     re-timing is monthly-only, so a non-monthly deal is precisely the case where the
    ///     default policy has to fold rather than discard.
    /// </summary>
    public TestDealBuilder WithPayFrequency(int payFrequency, DateTime? firstPayDate = null)
    {
        if (_deal.Tranches.Count == 0)
            throw new InvalidOperationException(
                "WithPayFrequency applies to the tranches added so far — call it AFTER "
                + "WithTranche, or it silently does nothing and the test measures a monthly "
                + "deal while claiming to measure a quarterly one.");

        foreach (var t in _deal.Tranches.OfType<Tranche>())
        {
            t.PayFrequency = payFrequency;
            if (firstPayDate.HasValue)
                t.FirstPayDate = firstPayDate.Value;
        }

        return this;
    }

    /// <summary>
    /// Set the first-period collateral policy and, optionally, an explicit accrual-start
    /// boundary. Leaving both unset is the default the engine has always had.
    /// </summary>
    public TestDealBuilder WithFirstPeriodCollateral(string? policy, DateTime? accrualStart = null)
    {
        _firstPeriodCollateralPolicy = policy;
        _collateralAccrualStartDate = accrualStart;
        return this;
    }

    public TestDealBuilder WithWaterfallOrder(WaterfallOrderEnum order)
    {
        _waterfallOrder = order;
        return this;
    }

    public TestDealBuilder WithExecutionOrder(params string[] steps)
    {
        _executionOrder = steps.ToList();
        return this;
    }

    public TestDealBuilder WithTranche(string name, double balance, double couponPct,
        int subOrder = 0, string cashflowType = "PI", string trancheType = "Offered",
        string couponType = "Fixed")
    {
        _tranches.Add((name, balance, couponPct, subOrder));
        _balanceAtIssuance += balance;

        _deal.Tranches.Add(new Tranche
        {
            TrancheName = name,
            DealName = _deal.DealName,
            OriginalBalance = balance,
            Factor = 1.0,
            CouponType = couponType,
            FixedCoupon = couponPct,
            TrancheType = trancheType,
            CashflowType = cashflowType,
            ClassReference = name,
            FirstPayDate = _firstPayDate,
            FirstSettleDate = _firstPayDate.AddMonths(-1),
            LegalMaturityDate = _firstPayDate.AddYears(10),
            StatedMaturityDate = _firstPayDate.AddYears(8),
            PayFrequency = 12,
            PayDelay = 0,
            PayDay = _firstPayDate.Day,
            DayCount = "30/360",
            BusinessDayConvention = "Following",
            HolidayCalendar = "Settlement",
            Deal = _deal
        });

        _deal.DealStructures.Add(new DealStructure
        {
            DealName = _deal.DealName,
            ClassGroupName = name,
            SubordinationOrder = subOrder,
            PayFrom = "Sequential",
            GroupNum = "1"
        });

        return this;
    }

    /// <summary>
    ///     Adds an excess-servicing IO strip (Class A-IO-S) — a notional IO
    ///     Reference tranche whose DealStructure pays from ExcessServicing, so
    ///     ComposableStructure pays it from the period servicing fee.
    /// </summary>
    public TestDealBuilder WithExcessServicingStrip(string name, double notional, int subOrder = 100)
    {
        _deal.Tranches.Add(new Tranche
        {
            TrancheName = name,
            DealName = _deal.DealName,
            OriginalBalance = notional,
            Factor = 1.0,
            CouponType = "None",
            FixedCoupon = 0,
            TrancheType = "Reference",
            CashflowType = "IO",
            ClassReference = name,
            FirstPayDate = _firstPayDate,
            FirstSettleDate = _firstPayDate.AddMonths(-1),
            LegalMaturityDate = _firstPayDate.AddYears(10),
            StatedMaturityDate = _firstPayDate.AddYears(8),
            PayFrequency = 12,
            PayDelay = 0,
            PayDay = _firstPayDate.Day,
            DayCount = "30/360",
            BusinessDayConvention = "Following",
            HolidayCalendar = "Settlement",
            Deal = _deal
        });

        _deal.DealStructures.Add(new DealStructure
        {
            DealName = _deal.DealName,
            ClassGroupName = name,
            SubordinationOrder = subOrder,
            PayFrom = "ExcessServicing",
            GroupNum = "1"
        });

        return this;
    }

    /// <summary>
    ///     Adds an extra tranche into an EXISTING class (shared ClassReference)
    ///     without a new DealStructure — producing a multi-tranche (combined /
    ///     exchangeable) class, so a single recipient resolves to >1
    ///     DynamicTranche. Used to exercise the EXCESS_RELEASE split.
    /// </summary>
    public TestDealBuilder WithTrancheInClass(string className, string trancheName,
        double balance, double couponPct, string cashflowType = "PI",
        string couponType = "Fixed", string trancheType = "Offered")
    {
        _balanceAtIssuance += balance;

        _deal.Tranches.Add(new Tranche
        {
            TrancheName = trancheName,
            DealName = _deal.DealName,
            OriginalBalance = balance,
            Factor = 1.0,
            CouponType = couponType,
            FixedCoupon = couponPct,
            TrancheType = trancheType,
            CashflowType = cashflowType,
            ClassReference = className,
            FirstPayDate = _firstPayDate,
            FirstSettleDate = _firstPayDate.AddMonths(-1),
            LegalMaturityDate = _firstPayDate.AddYears(10),
            StatedMaturityDate = _firstPayDate.AddYears(8),
            PayFrequency = 12,
            PayDelay = 0,
            PayDay = _firstPayDate.Day,
            DayCount = "30/360",
            BusinessDayConvention = "Following",
            HolidayCalendar = "Settlement",
            Deal = _deal
        });

        return this;
    }

    /// <summary>
    ///     Adds an exchangeable (MACR / combined / recombinable) class: a notional
    ///     Exchanged tranche whose cashflow is the proportional sum of its component
    ///     tranches' cashflows — principal AND interest — wired via a
    ///     PayFrom=Exchange DealStructure + ExchShares (share = full component
    ///     balance, i.e. a 100% combination). The component tranches must already be
    ///     added. Exchanged classes are excluded from the cash-consuming DealClasses,
    ///     so the class mirrors — never double-counts — the primaries.
    /// </summary>
    public TestDealBuilder WithExchangeClass(string name, int subOrder,
        params string[] componentTranches)
        => WithExchangeClass(name, subOrder, wellFormed: true, componentTranches);

    /// <summary>
    ///     An exchangeable class that states its OWN fixed coupon, rather than deriving one from
    ///     the deal (`eff_wac`). That distinction is load-bearing: a stated coupon means the class
    ///     has independent economics and must accrue from it, where a derived one means the class
    ///     is a view of its components and must mirror them (#4572).
    /// </summary>
    public TestDealBuilder WithExchangeClassStatingCoupon(string name, int subOrder, double couponPct,
        params string[] componentTranches)
    {
        WithExchangeClass(name, subOrder, wellFormed: true, componentTranches);
        var t = (Tranche)_deal.Tranches.First(x => x.TrancheName == name);
        t.CouponType = "Fixed";
        t.CouponFormula = null;
        t.FixedCoupon = couponPct;
        return this;
    }

    /// <summary>
    ///     Overload allowing a deliberately malformed Exchanged class for negative
    ///     tests: <paramref name="wellFormed" /> = false omits the ExchangableTranche
    ///     reference (and ExchShares), reproducing a class typed Exchanged with no
    ///     component notes (e.g. a mis-extracted plain debt note).
    /// </summary>
    public TestDealBuilder WithExchangeClass(string name, int subOrder, bool wellFormed,
        params string[] componentTranches)
    {
        var componentBalances = componentTranches
            .Select(c => _deal.Tranches.First(t => t.TrancheName == c).OriginalBalance)
            .ToList();

        _deal.Tranches.Add(new Tranche
        {
            TrancheName = name,
            DealName = _deal.DealName,
            OriginalBalance = componentBalances.Sum(),
            Factor = 1.0,
            CouponType = "Formula",
            CouponFormula = "eff_wac",
            TrancheType = "Exchanged",
            CashflowType = "PI",
            ClassReference = name,
            FirstPayDate = _firstPayDate,
            FirstSettleDate = _firstPayDate.AddMonths(-1),
            LegalMaturityDate = _firstPayDate.AddYears(10),
            StatedMaturityDate = _firstPayDate.AddYears(8),
            PayFrequency = 12,
            PayDelay = 0,
            PayDay = _firstPayDate.Day,
            DayCount = "30/360",
            BusinessDayConvention = "Following",
            HolidayCalendar = "Settlement",
            Deal = _deal
        });

        _deal.DealStructures.Add(new DealStructure
        {
            DealName = _deal.DealName,
            ClassGroupName = name,
            SubordinationOrder = subOrder,
            PayFrom = "Exchange",
            ExchangableTranche = wellFormed ? string.Join(",", componentTranches) : null,
            GroupNum = "1"
        });

        if (wellFormed)
            for (var i = 0; i < componentTranches.Length; i++)
                _deal.ExchShares.Add(new ExchShare
                {
                    DealName = _deal.DealName,
                    ClassGroupName = name,
                    TrancheName = componentTranches[i],
                    Quantity = componentBalances[i]
                });

        return this;
    }

    public TestDealBuilder WithExpenseTranche(string name, double formulaAmount, int subOrder = 99)
    {
        _deal.Tranches.Add(new Tranche
        {
            TrancheName = name,
            DealName = _deal.DealName,
            OriginalBalance = 0,
            Factor = 1.0,
            CouponType = "None",
            TrancheType = "Offered",
            CashflowType = "Expense",
            ClassReference = name,
            FirstPayDate = _firstPayDate,
            FirstSettleDate = _firstPayDate.AddMonths(-1),
            LegalMaturityDate = _firstPayDate.AddYears(10),
            StatedMaturityDate = _firstPayDate.AddYears(8),
            PayFrequency = 12,
            PayDay = _firstPayDate.Day,
            DayCount = "30/360",
            BusinessDayConvention = "Following",
            HolidayCalendar = "Settlement",
            CouponFormula = formulaAmount.ToString("F2"),
            Deal = _deal
        });

        _deal.DealStructures.Add(new DealStructure
        {
            DealName = _deal.DealName,
            ClassGroupName = name,
            SubordinationOrder = subOrder,
            PayFrom = "Expense",
            GroupNum = "1"
        });

        return this;
    }

    public TestDealBuilder WithCertificateTranche(string name = "Certificates", int subOrder = 99)
    {
        _deal.Tranches.Add(new Tranche
        {
            TrancheName = name,
            DealName = _deal.DealName,
            OriginalBalance = 0,
            Factor = 1.0,
            CouponType = "None",
            TrancheType = "Certificate",
            CashflowType = "PI",
            ClassReference = name,
            FirstPayDate = _firstPayDate,
            FirstSettleDate = _firstPayDate.AddMonths(-1),
            LegalMaturityDate = _firstPayDate.AddYears(10),
            StatedMaturityDate = _firstPayDate.AddYears(8),
            PayFrequency = 12,
            PayDay = _firstPayDate.Day,
            DayCount = "30/360",
            BusinessDayConvention = "Following",
            HolidayCalendar = "Settlement",
            Deal = _deal
        });

        _deal.DealStructures.Add(new DealStructure
        {
            DealName = _deal.DealName,
            ClassGroupName = name,
            SubordinationOrder = subOrder,
            PayFrom = "Sequential",
            GroupNum = "1"
        });

        return this;
    }

    public TestDealBuilder WithOcTarget(double targetPct, double floorAmt)
    {
        _ocTargetConfig = new OcTargetConfig { TargetPct = targetPct, FloorAmt = floorAmt };
        return this;
    }

    public TestDealBuilder WithReinvestment(ReinvestmentConfig config)
    {
        _reinvestmentConfig = config;
        return this;
    }

    /// <summary>
    ///     Configures the CLO per-level OC/IC coverage cascade (senior→junior).
    /// </summary>
    public TestDealBuilder WithCoverageCascade(params CoverageLevelConfig[] levels)
    {
        _coverageCascade = levels.ToList();
        return this;
    }

    /// <summary>
    ///     Adds a scheduled deal variable (e.g. the "ACPA" OC numerator) covering
    ///     [begin, end].
    /// </summary>
    public TestDealBuilder WithScheduledVariable(string name, double value,
        DateTime begin, DateTime end)
    {
        _deal.ScheduledVariables.Add(new ScheduledVariable
        {
            DealName = _deal.DealName,
            ScheduleVariableName = name,
            BeginDate = begin,
            EndDate = end,
            ValueNum = value,
            GroupNum = "1"
        });
        return this;
    }

    public TestDealBuilder WithPayRule(string name, string formula)
    {
        _payRuleFormulas.Add($"{name}|{formula}");
        return this;
    }

    /// <summary>
    /// Adds standard sequential waterfall rules for the given tranche names.
    /// </summary>
    public TestDealBuilder WithSequentialWaterfall(params string[] trancheNames)
    {
        var singles = string.Join(", ", trancheNames.Select(t => $"SINGLE('{t}')"));
        var reverseSingles = string.Join(", ", trancheNames.Reverse().Select(t => $"SINGLE('{t}')"));

        WithPayRule("InterestStruct", $"SET_INTEREST_STRUCT(SEQ({singles}))");
        WithPayRule("SchedStruct", $"SET_SCHED_STRUCT(SEQ({singles}))");
        WithPayRule("PrepayStruct", $"SET_PREPAY_STRUCT(SEQ({singles}))");
        WithPayRule("RecovStruct", $"SET_RECOV_STRUCT(SEQ({singles}))");
        WithPayRule("WritedownStruct", $"SET_WRITEDOWN_STRUCT(SEQ({reverseSingles}))");

        return this;
    }

    /// <summary>
    /// Adds waterfall rules with prorata interest and sequential principal.
    /// </summary>
    public TestDealBuilder WithProrataInterestSequentialPrincipal(
        string[] interestProrataTranches,
        string[] principalSeqTranches,
        string[]? writedownRevTranches = null)
    {
        var intProrata = string.Join("','", interestProrataTranches);
        var prinSingles = string.Join(", ", principalSeqTranches.Select(t => $"SINGLE('{t}')"));
        var wdTranches = writedownRevTranches ?? principalSeqTranches.Reverse().ToArray();
        var wdSingles = string.Join(", ", wdTranches.Select(t => $"SINGLE('{t}')"));

        WithPayRule("InterestStruct", $"SET_INTEREST_STRUCT(PRORATA('{intProrata}'))");
        WithPayRule("SchedStruct", $"SET_SCHED_STRUCT(SEQ({prinSingles}))");
        WithPayRule("PrepayStruct", $"SET_PREPAY_STRUCT(SEQ({prinSingles}))");
        WithPayRule("RecovStruct", $"SET_RECOV_STRUCT(SEQ({prinSingles}))");
        WithPayRule("WritedownStruct", $"SET_WRITEDOWN_STRUCT(SEQ({wdSingles}))");

        return this;
    }

    public (IDeal Deal, DealCashflows Cashflows) BuildAndRun(
        CollateralCashflows collateral,
        double rateValue = TestConstants.DefaultRate)
    {
        var deal = Build();
        var rateProvider = new ConstantTestRateProvider(rateValue);
        var anchorAbsT = DateUtil.CalcAbsT(_projectionDate);
        var assumps = DealLevelAssumptions.CreateConstAssumptions(_projectionDate, anchorAbsT, 0, 0, 0);
        var waterfallEngine = WaterfallFactory.GetWaterfall(deal.CashflowEngine);
        var firstProjDate = collateral.PeriodCashflows.First().CashflowDate;
        var cashflows = waterfallEngine.Waterfall(deal, rateProvider, firstProjDate, collateral,
            assumps, new TrancheAllocator());
        return (deal, cashflows);
    }

    public IDeal Build()
    {
        _deal.InterestTreatment = _interestTreatment;
        _deal.FirstPeriodCollateralPolicy = _firstPeriodCollateralPolicy;
        _deal.CollateralAccrualStartDate = _collateralAccrualStartDate;
        _deal.BalanceAtIssuance = _balanceAtIssuance > 0 ? _balanceAtIssuance : 100_000_000;

        if (_executionOrder != null)
            _deal.ExecutionOrder = _executionOrder;

        _deal.WaterfallOrder = _waterfallOrder;

        if (_ocTargetConfig != null)
            _deal.OcTargetConfig = _ocTargetConfig;

        if (_reinvestmentConfig != null)
            _deal.ReinvestmentConfig = _reinvestmentConfig;

        if (_coverageCascade != null)
            _deal.CoverageCascade = _coverageCascade;

        // Add pay rules
        for (var i = 0; i < _payRuleFormulas.Count; i++)
        {
            var parts = _payRuleFormulas[i].Split('|', 2);
            _deal.PayRules.Add(new PayRule
            {
                DealName = _deal.DealName,
                RuleName = parts[0],
                ClassGroupName = "GROUP_1",
                Formula = parts[1],
                RuleExecutionOrder = i
            });
        }

        _deal.RuleAssembly = RulesBuilder.CompileRules(_deal);
        return _deal;
    }
}
