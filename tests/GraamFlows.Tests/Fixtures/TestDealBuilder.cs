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

    public TestDealBuilder WithInterestTreatment(string treatment)
    {
        _interestTreatment = treatment;
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

    /// <summary>
    /// Adds an MACR exchangeable class that derives its cashflows from the given
    /// real-tranche components. Each component contributes a Quantity (dollars)
    /// of the real tranche; for a 100% pass-through MACR, Quantity equals the
    /// real tranche's OriginalBalance.
    ///
    /// Wires up:
    ///   - Tranche row with TrancheType="Exchanged", OriginalBalance=sum of quantities
    ///   - DealStructure row with PayFrom="Exchange" and ExchangableTranche=comma-list
    ///   - One ExchShare per (exchange, real) pair with the given Quantity
    /// </summary>
    public TestDealBuilder WithExchangeClass(string name,
        IEnumerable<(string TrancheName, double Quantity)> components,
        int subOrder = 200)
    {
        var comps = components.ToList();
        var totalBalance = comps.Sum(c => c.Quantity);

        _deal.Tranches.Add(new Tranche
        {
            TrancheName = name,
            DealName = _deal.DealName,
            OriginalBalance = totalBalance,
            Factor = 1.0,
            CouponType = "None",
            FixedCoupon = 0,
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
            ExchangableTranche = string.Join(",", comps.Select(c => c.TrancheName)),
            GroupNum = "1"
        });

        foreach (var (trancheName, quantity) in comps)
        {
            _deal.ExchShares.Add(new ExchShare
            {
                DealName = _deal.DealName,
                ClassGroupName = name,
                TrancheName = trancheName,
                Quantity = quantity
            });
        }

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
        _deal.BalanceAtIssuance = _balanceAtIssuance > 0 ? _balanceAtIssuance : 100_000_000;

        if (_executionOrder != null)
            _deal.ExecutionOrder = _executionOrder;

        _deal.WaterfallOrder = _waterfallOrder;

        if (_ocTargetConfig != null)
            _deal.OcTargetConfig = _ocTargetConfig;

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
