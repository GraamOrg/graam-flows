using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.AssetCashflowEngine;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// The last four differences between a reinvesting CLO's projection and an independent reference
/// model in a stressed scenario (defaults, no prepayments):
///
/// * each payment is collected in the row whose cutoff reaches it — the cutoff moves with weekends
///   and holidays, so a payment on the 5th is collected in its own month one quarter and the next
///   month another — not on a fixed every-third-row cadence (and a row can then collect two);
/// * a loan maturing between payment dates prepays and defaults on its maturity, over the months
///   since its last payment, rather than never;
/// * a coverage test is measured before the Payment Date's purchase: its proceeds count at cash,
///   not at the larger face a discount buys;
/// * a fee the Priority of Payments pays after the notes (a subordinated management fee) is paid
///   from what the notes leave, its unpaid part carried — not at the head of the waterfall.
/// </summary>
public class CollectionTimingAndFeeOrderTests
{
    private static readonly DateTime Proj = new(2026, 1, 20);

    private static List<PeriodCashflowDto> Run(AssetDto loan, double cdr = 0, int cutoffDays = 10, double cpr = 0)
    {
        var ok = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(new CalcCollateralRequest
        {
            Assets = new List<AssetDto> { loan }, ProjectionDate = Proj, CollectionCutoffBusinessDays = cutoffDays,
            InterestAccrualStartDate = new DateTime(2025, 12, 23),
            Assumptions = new AssumptionsDto { Cpr = cpr, Cdr = cdr, Severity = 0 }
        }).Result.Should().BeOfType<OkObjectResult>().Subject;
        return ((CalcCollateralResponse)ok.Value!).Cashflows.OrderBy(c => c.Period).ToList();
    }

    private static AssetDto Loan(int freq, DateTime next, DateTime? maturity = null) => new()
    {
        AssetName = "l", AssetId = "l", InterestRateType = "FRM", OriginalDate = new DateTime(2025, 12, 23),
        OriginalBalance = 1_000_000, CurrentBalance = 1_000_000, OriginalInterestRate = 8, CurrentInterestRate = 8,
        OriginalAmortizationTerm = 60, GroupNum = "1", IsIO = true, IOTerm = 60, DayCount = "Actual/360",
        PaymentFrequency = freq, NextPaymentDate = next, MaturityDate = maturity
    };

    [Fact]
    public void A_payment_past_its_months_cutoff_is_collected_a_row_later()
    {
        // September's cutoff (10 business days before Sunday Sep 20, Labor Day the 7th) is Sep 4, so
        // the Sep 5 payment is collected in October's row — with October's own Oct 5 (cutoff Oct 5).
        var m = Run(Loan(12, new DateTime(2026, 1, 5)), cpr: 10);
        var sep = m.Single(c => c.CashflowDate == new DateTime(2026, 9, 20));
        var oct = m.Single(c => c.CashflowDate == new DateTime(2026, 10, 20));
        var nov = m.Single(c => c.CashflowDate == new DateTime(2026, 11, 20));
        sep.Interest.Should().Be(0);
        sep.UnscheduledPrincipal.Should().Be(0, "it prepays on its payments too");
        oct.Interest.Should().BeApproximately(oct.BeginBalance * 0.08 * (new DateTime(2026, 10, 5) - new DateTime(2026, 8, 5)).TotalDays / 360, 0.01,
            "Aug 5 -> Oct 5: two payments' interest");
        nov.Interest.Should().BeApproximately(nov.BeginBalance * 0.08 * 31 / 360, 0.01, "Oct 5 -> Nov 5: the next one");
        // Placed one payment at a time: with a cutoff on the 6th each 5th is collected in its own
        // month, but Saturday Sep 5 (rolled to Monday the 7th) misses September's and is collected
        // with October's own.
        var counts = PaymentSchedule.PaymentRowCounts(new DateTime(2026, 1, 5), 1, Proj, 12,
            d => d.Month == 9 ? d.AddDays(-16) : d.AddDays(-14));
        counts.Should().Equal(1, 1, 1, 1, 1, 1, 1, 1, 0, 2, 1, 1);
    }

    [Fact]
    public void Without_a_cutoff_every_month_collects_its_own_payment()
    {
        var m = Run(Loan(12, new DateTime(2026, 1, 5)), cutoffDays: 0);
        m.Where(c => c.CashflowDate.Year == 2026).Should().OnlyContain(c => c.Interest > 0);
    }

    [Fact]
    public void A_loan_maturing_between_payment_dates_defaults_on_its_maturity()
    {
        // Quarterly from Jan 15 (each payment past its month's cutoff: collected the next month, so
        // Oct 15 in November's row), maturing Dec 31, 2026, in January's row: the two months since
        // its last payment row default there, not never.
        var q = Run(Loan(4, new DateTime(2026, 1, 15), new DateTime(2026, 12, 31)), cdr: 10);
        q.Single(c => c.CashflowDate == new DateTime(2026, 11, 20)).DefaultedPrincipal.Should().BeGreaterThan(0);
        var jan = q.Single(c => c.CashflowDate == new DateTime(2027, 1, 20));
        var mdr = 1 - Math.Pow(0.9, 1.0 / 12);
        jan.DefaultedPrincipal.Should().BeApproximately(jan.BeginBalance * (1 - Math.Pow(1 - mdr, 2)), 0.01);
    }

    // --- the waterfall ---------------------------------------------------------------------------

    private static readonly DateTime FirstPay = TestConstants.DefaultFirstPayDate;

    private static CollateralCashflows Pool(double interest, double purchaseDiscount = 0)
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        for (var i = 0; i < 12; i++)
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: 100_000_000, scheduledPrincipal: 0,
                unscheduledPrincipal: 0, interest: interest);
        var pool = b.Build();
        foreach (var p in pool.PeriodCashflows)
            p.PurchaseDiscount = purchaseDiscount;
        return pool;
    }

    private static TestDealBuilder Deal() =>
        new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("E", 15_000_000, 8.0, subOrder: 1)
            .WithTranche("R", 5_000_000, 0.0, subOrder: 2, cashflowType: "PI", couponType: "Residual")
            .WithExpenseTranche("SubFee", 0, formula: "100000")
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A'), SINGLE('E')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A'), SINGLE('E')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A'), SINGLE('E')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A'), SINGLE('E')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('E'), SINGLE('A')))")
            .WithPayRule("ExcessStruct", "SET_EXCESS_STRUCT(SINGLE('R'))");

    private static List<TrancheCashflow> Rows(DealCashflows cf, string name) =>
        cf.TrancheCashflows.First(t => t.Key.TrancheName == name).Value.Cashflows
            .OrderBy(c => c.Key).Select(c => c.Value).ToList();

    [Fact]
    public void A_fee_paid_after_interest_comes_after_the_notes_and_carries_its_unpaid_part()
    {
        // 500k of interest a month against 433,333 of note coupon (A 333,333 + E 100,000): the notes
        // are paid first, the fee gets what is left, and the rest is paid the next month.
        var notes = 80_000_000 * 0.05 / 12 + 15_000_000 * 0.08 / 12;
        var left = 500_000 - notes; // 66,667 a month for a 100,000 fee
        var head = Rows(Deal().BuildAndRun(Pool(500_000)).Cashflows, "E");
        var after = Deal().WithExpensesAfterInterest("SubFee").BuildAndRun(Pool(500_000)).Cashflows;
        Rows(after, "E").Skip(1).Take(3).Should().OnlyContain(r => Math.Abs(r.Interest - 100_000) < 0.01,
            "Class E is paid ahead of the fee");
        head.Skip(1).Take(3).Should().OnlyContain(r => r.Interest < 100_000, "paid at the head, the fee came first");
        var fee = Rows(after, "SubFee").Where(r => r.Expense > 0).ToList();
        fee.Skip(1).Take(4).Should().OnlyContain(r => Math.Abs(r.Expense - left) < 0.01, "all that is left, every month");
        var feeShortfall = Rows(after, "SubFee").Skip(2).First().Expense;
        feeShortfall.Should().BeApproximately(left, 0.01);
        // ... and nothing reaches the residual while the fee is behind.
        Rows(after, "R").Skip(1).Take(4).Should().OnlyContain(r => r.Interest < 0.01);
    }

    [Fact]
    public void A_carried_fee_is_paid_once_interest_allows()
    {
        // Interest falls short one month only: the carried part is paid with the next month's fee.
        var b = new TestCollateralBuilder().WithGroupNum("1");
        for (var i = 0; i < 6; i++)
            b.WithPeriod(date: FirstPay.AddMonths(i), beginBalance: 100_000_000, scheduledPrincipal: 0,
                unscheduledPrincipal: 0, interest: i == 2 ? 450_000 : 800_000);
        var cf = Deal().WithExpensesAfterInterest("SubFee").BuildAndRun(b.Build()).Cashflows;
        var fee = Rows(cf, "SubFee");
        var notes = 80_000_000 * 0.05 / 12 + 15_000_000 * 0.08 / 12;
        fee[2].Expense.Should().BeApproximately(450_000 - notes, 0.01);
        fee[3].Expense.Should().BeApproximately(100_000 + (100_000 - (450_000 - notes)), 0.01);
        fee[4].Expense.Should().BeApproximately(100_000, 0.01);
    }

    [Fact]
    public void An_unknown_expense_name_is_refused()
    {
        var defined = new[] { new ExpenseDto { ExpenseName = "SubFee", Formula = "1" } };
        ExpensesAfterInterestMapper.Map(new List<string> { "subfee" }, defined, "d").Should().Equal("subfee");
        ExpensesAfterInterestMapper.Map(null, defined, "d").Should().BeEmpty();
        FluentActions.Invoking(() => ExpensesAfterInterestMapper.Map(new List<string> { "NoSuchFee" }, defined, "d"))
            .Should().Throw<ArgumentException>().WithMessage("*NoSuchFee*paid nowhere*");
    }

    [Fact]
    public void A_coverage_ratio_counts_the_payment_dates_purchase_at_cash_not_face()
    {
        // The posted pool's balance includes collateral bought that Payment Date at a discount; the
        // test reads the 500k of face over cash out — measured on the Determination Date, before it.
        var test = new InterestDiversionConfig
        {
            Tranches = new[] { "A", "E" }, TriggerPct = 106, MaxPctOfInterest = 50,
            EndDate = FirstPay.AddMonths(12), PurchasePricePct = 99.5
        };
        var cf = Deal().WithInterestDiversion(test).BuildAndRun(Pool(800_000, purchaseDiscount: 500_000)).Cashflows;
        cf.InterestDiversions.Should().NotBeEmpty();
        cf.InterestDiversions.Should().OnlyContain(d => Math.Abs(d.RatioPct - 99_500_000 / 95_000_000.0 * 100) < 1e-9);
    }
}
