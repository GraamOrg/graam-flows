using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// A fee on the residual class's distributions once it has earned a hurdle IRR — a CLO's
/// incentive management fee: the equity keeps everything until its XIRR (actual/365, annual) on
/// the deemed purchase price reaches the hurdle, then the manager takes a share of the rest.
///
/// The engine carries a running present value; these tests pin it with an INDEPENDENT XIRR solved
/// by bisection, so they do not re-derive the implementation's own algebra.
/// </summary>
public class IncentiveFeeHurdleTests
{
    private static readonly DateTime FirstPay = TestConstants.DefaultFirstPayDate;
    private static readonly DateTime Closing = FirstPay.AddMonths(-1);
    private const double SeniorFace = 70_000_000;
    private const double ResidualFace = 30_000_000;
    private const double Pool = SeniorFace + ResidualFace;
    private const int Periods = 60;
    private const double Hurdle = 12.0;
    private const double Share = 20.0;

    /// <summary>A pool that amortizes to zero, with interest well above the senior coupon, so the
    /// residual is paid excess interest every month and the leftover principal at the end.</summary>
    private static CollateralCashflows AmortizingPool()
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var per = Pool / Periods;
        for (var i = 0; i < Periods; i++)
        {
            var begin = Pool - per * i;
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: begin, scheduledPrincipal: per,
                unscheduledPrincipal: 0, interest: begin * 0.09 / 12);
        }

        return b.Build();
    }

    private static IncentiveFeeConfig Config(double investment = 25_000_000, double hurdle = Hurdle,
        IReadOnlyList<(DateTime, double)>? prior = null, string residual = "R", string fee = "Incentive") => new()
    {
        ResidualTranche = residual, FeeExpense = fee, HurdleIrrPct = hurdle, SharePct = Share,
        Investment = investment, InvestmentDate = Closing,
        PriorDistributions = prior ?? Array.Empty<(DateTime, double)>()
    };

    private static DealCashflows Run(IncentiveFeeConfig? cfg)
    {
        var b = new TestDealBuilder()
            .WithTranche("A", SeniorFace, 5.0, subOrder: 0)
            .WithTranche("R", ResidualFace, 0.0, subOrder: 1, cashflowType: "PI", couponType: "Residual")
            .WithExpenseTranche("Incentive", 0, formula: "0")
            // The CLO shape: equity is a plain note paid the excess interest at EXCESS and the
            // leftover principal as the residual.
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('A')))")
            .WithPayRule("ExcessStruct", "SET_EXCESS_STRUCT(SINGLE('R'))");
        if (cfg != null) b = b.WithIncentiveFee(cfg);
        return b.BuildAndRun(AmortizingPool()).Cashflows;
    }

    private static SortedDictionary<DateTime, TrancheCashflow> Rows(DealCashflows cf, string name) =>
        new(cf.TrancheCashflows.First(t => t.Key.TrancheName == name).Value.Cashflows);

    private static double Paid(TrancheCashflow c) => c.Interest + c.ScheduledPrincipal + c.UnscheduledPrincipal;

    /// <summary>XIRR by bisection: the rate at which −investment at closing plus the flows has zero NPV.</summary>
    private static double Xirr(double investment, IEnumerable<(DateTime Date, double Amount)> flows)
    {
        var list = flows.ToList();
        double Npv(double r) => -investment +
                                list.Sum(f => f.Amount / Math.Pow(1 + r, (f.Date - Closing).TotalDays / 365.0));
        double lo = -0.99, hi = 10.0;
        for (var i = 0; i < 200; i++)
        {
            var mid = (lo + hi) / 2;
            if (Npv(mid) > 0) lo = mid; else hi = mid;
        }

        return (lo + hi) / 2;
    }

    private static (List<(DateTime Date, double Without, double With, double Fee)> Rows, int Crossing) Compare(
        IncentiveFeeConfig cfg)
    {
        var without = Rows(Run(null), "R");
        var withFee = Run(cfg);
        var r = Rows(withFee, "R");
        var fee = Rows(withFee, "Incentive");
        var rows = without.Keys.Select(d => (d, Paid(without[d]), r.TryGetValue(d, out var w) ? Paid(w) : 0.0,
            fee.TryGetValue(d, out var f) ? f.Expense : 0.0)).ToList();
        return (rows, rows.FindIndex(x => x.Item4 > 0.005));
    }

    [Fact]
    public void Below_the_hurdle_the_residual_keeps_everything_and_the_IRR_is_under_it()
    {
        var (rows, crossing) = Compare(Config());
        crossing.Should().BeGreaterThan(3, "the residual must earn the hurdle before any fee");
        rows.Take(crossing).Should().OnlyContain(x => Math.Abs(x.With - x.Without) < 0.01 && x.Fee < 0.005);
        Xirr(25_000_000, rows.Take(crossing).Select(x => (x.Date, x.With))).Should().BeLessThan(Hurdle / 100);
    }

    [Fact]
    public void The_crossing_payment_brings_the_residual_exactly_to_the_hurdle()
    {
        var (rows, crossing) = Compare(Config());
        var c = rows[crossing];
        // On the crossing date the residual gets H plus 80% of (R − H), and the fee is 20% of
        // (R − H): so H = with − 4 × fee. That portion, and nothing more, earns exactly the hurdle.
        var hurdlePortion = c.With - 4 * c.Fee;
        hurdlePortion.Should().BeGreaterThan(0).And.BeLessThan(c.Without);
        var flows = rows.Take(crossing).Select(x => (x.Date, x.With)).Append((c.Date, hurdlePortion));
        Xirr(25_000_000, flows).Should().BeApproximately(Hurdle / 100, 1e-9);
    }

    [Fact]
    public void Past_the_hurdle_the_fee_is_exactly_its_share_of_every_distribution()
    {
        var (rows, crossing) = Compare(Config());
        rows.Skip(crossing + 1).Should().NotBeEmpty();
        foreach (var x in rows.Skip(crossing + 1).Where(x => x.Without > 0.01))
        {
            x.Fee.Should().BeApproximately(x.Without * Share / 100, 1e-6);
            x.With.Should().BeApproximately(x.Without * (1 - Share / 100), 1e-6);
        }
    }

    [Fact]
    public void Every_dollar_is_either_the_residuals_or_the_fee()
    {
        foreach (var x in Compare(Config()).Rows)
            (x.With + x.Fee).Should().BeApproximately(x.Without, 1e-6, $"on {x.Date:yyyy-MM-dd}");
    }

    [Fact]
    public void Interest_meets_the_hurdle_before_principal()
    {
        var without = Rows(Run(null), "R");
        var withFee = Run(Config());
        var r = Rows(withFee, "R");
        var fee = Rows(withFee, "Incentive");
        // The last month pays the residual both excess interest and leftover principal; well past
        // the hurdle, each is cut by exactly the share.
        var last = without.Keys.Last(d => without[d].UnscheduledPrincipal + without[d].ScheduledPrincipal > 1);
        (without[last].Interest - r[last].Interest).Should().BeApproximately(without[last].Interest * Share / 100, 1e-6);
        fee[last].Expense.Should().BeGreaterThan(without[last].Interest * Share / 100 + 1);

        // And where the hurdle is met INSIDE a payment's interest, principal pays its full share.
        var (rows, crossing) = Compare(Config());
        var c = rows[crossing];
        var owed = c.With - 4 * c.Fee;
        var interestWithout = without[c.Date].Interest;
        (interestWithout - r[c.Date].Interest).Should().BeApproximately(Share / 100 * Math.Max(interestWithout - owed, 0), 1e-6);
    }

    [Fact]
    public void A_hurdle_met_mid_payment_takes_the_fee_from_interest_before_principal()
    {
        // Find an investment whose crossing lands on a date that pays the residual BOTH interest
        // and principal, and whose interest alone does not reach the hurdle — the only case where
        // the order matters. Fail loudly if the fixture stops producing one.
        var without = Rows(Run(null), "R");
        foreach (var investment in Enumerable.Range(28, 30).Select(m => m * 1_000_000.0))
        {
            var (rows, crossing) = Compare(Config(investment: investment));
            if (crossing < 0) continue;
            var c = rows[crossing];
            var w = without[c.Date];
            var principal = w.ScheduledPrincipal + w.UnscheduledPrincipal;
            var owed = c.With - 4 * c.Fee; // independent of the engine's algebra: see the crossing test
            if (principal < 1 || owed <= w.Interest + 1) continue;

            var r = Rows(Run(Config(investment: investment)), "R")[c.Date];
            (w.Interest - r.Interest).Should().BeApproximately(0, 1e-6,
                "interest is spent on the hurdle first; this date's interest did not reach it");
            (principal - (r.ScheduledPrincipal + r.UnscheduledPrincipal)).Should()
                .BeApproximately(Share / 100 * (principal - (owed - w.Interest)), 1e-6);
            return;
        }

        throw new InvalidOperationException("no investment put the crossing on a mixed interest/principal date");
    }

    [Fact]
    public void An_unreachable_hurdle_charges_nothing()
    {
        var (rows, crossing) = Compare(Config(investment: 90_000_000));
        crossing.Should().Be(-1);
        rows.Should().OnlyContain(x => Math.Abs(x.With - x.Without) < 0.01);
    }

    [Fact]
    public void Distributions_before_the_projection_count_toward_the_hurdle()
    {
        var (_, fresh) = Compare(Config());
        var (_, seasoned) = Compare(Config(prior: new[] { (Closing.AddDays(10), 5_000_000.0) }));
        seasoned.Should().BeLessThan(fresh, "5M already received brings the hurdle closer");
    }

    [Fact]
    public void A_misspelt_residual_or_an_undeclared_fee_fails_instead_of_charging_nothing()
    {
        FluentActions.Invoking(() => Run(Config(residual: "Equity"))).Should().Throw<DealModelingException>()
            .WithMessage("*residualTranche 'Equity'*");
        // Both refused when the run STARTS ("of this deal"), not on the first distribution.
        FluentActions.Invoking(() => Run(Config(fee: "NoSuchFee"))).Should().Throw<DealModelingException>()
            .WithMessage("*feeExpense 'NoSuchFee' is not a declared expense of this deal*");
        FluentActions.Invoking(() => Run(Config(fee: "A"))).Should().Throw<DealModelingException>(
            "a note class is not an expense").WithMessage("*of this deal*");
    }

    [Fact]
    public void The_wire_maps_validates_and_is_absent_by_default()
    {
        IncentiveFeeMapper.Map(null, "D").Should().BeNull();
        var dto = new IncentiveFeeDto
        {
            ResidualTranche = "Sub", FeeExpense = "Fee", HurdleIrrPct = 12, SharePct = 20,
            Investment = 40_000_000, InvestmentDate = new DateTime(2025, 6, 15),
            PriorDistributions = new() { new() { Date = new DateTime(2025, 9, 15), Amount = 1.5 } }
        };
        var cfg = IncentiveFeeMapper.Map(dto, "D")!;
        cfg.Should().BeEquivalentTo(new IncentiveFeeConfig
        {
            ResidualTranche = "Sub", FeeExpense = "Fee", HurdleIrrPct = 12, SharePct = 20, Investment = 40_000_000,
            InvestmentDate = new DateTime(2025, 6, 15),
            PriorDistributions = new[] { (new DateTime(2025, 9, 15), 1.5) }
        });

        void Bad(Action<IncentiveFeeDto> f, string fragment)
        {
            var d = new IncentiveFeeDto
            {
                ResidualTranche = "Sub", FeeExpense = "Fee", HurdleIrrPct = 12, SharePct = 20,
                Investment = 1, InvestmentDate = new DateTime(2025, 6, 15)
            };
            f(d);
            FluentActions.Invoking(() => IncentiveFeeMapper.Map(d, "D")).Should()
                .Throw<InvalidOperationException>().WithMessage($"*{fragment}*");
        }

        Bad(d => d.ResidualTranche = "", "residualTranche");
        Bad(d => d.FeeExpense = " ", "feeExpense");
        Bad(d => d.HurdleIrrPct = 0.12 * 1000, "hurdleIrrPct");
        Bad(d => d.SharePct = 0, "sharePct");
        Bad(d => d.Investment = 0, "investment");
        Bad(d => d.InvestmentDate = default, "investmentDate");
        Bad(d => d.PriorDistributions = new() { new() { Date = new DateTime(2025, 1, 1), Amount = 1 } }, "precedes");
    }
}
