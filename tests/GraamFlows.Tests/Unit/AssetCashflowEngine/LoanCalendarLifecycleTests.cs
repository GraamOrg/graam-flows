using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.AssetCashflowEngine;
using GraamFlows.Assumptions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// A loan that states its payment calendar, from its first payment to its maturity.
///
/// Measured loan by loan against an independent reference model's asset-level export (394 loans, two
/// scenarios), each of these moved a pool's interest by 0.1-0.5%; with all of them the pool's
/// lifetime interest ties to within a few thousand dollars:
///
/// * the FIRST payment ends a payment period that began before the projection did, so it carries a
///   whole period's prepayment and default — and nothing prepays before it;
/// * a stated MATURITY DATE is the maturity: the loan repays in the row that collects it and its last
///   payment accrues only up to it (a term counted in whole months paid up to a month past it);
/// * the part of a loan that DEFAULTS on a payment date does not pay the coupon due on it;
/// * a floating coupon is fixed for each interest period: it RESETS on payment dates, so the current
///   coupon holds to the first payment.
/// </summary>
public class LoanCalendarLifecycleTests
{
    // Row p is dated Proj + p months and covers the month before it: row 0 is Dec 20 -> Jan 20.
    private static readonly DateTime Proj = new(2026, 1, 20);
    private static readonly DateTime Closing = new(2025, 12, 23);
    private const double Bal = 1_000_000, Cpn = 8.0;
    private static double Smm(double cpr) => 1 - Math.Pow(1 - cpr / 100, 1.0 / 12);

    private static AssetDto Loan(int? freq, DateTime? next, DateTime? maturity = null, int term = 24) => new()
    {
        AssetName = "l", AssetId = "l", InterestRateType = "FRM", OriginalDate = Closing,
        OriginalBalance = Bal, CurrentBalance = Bal, OriginalInterestRate = Cpn, CurrentInterestRate = Cpn,
        OriginalAmortizationTerm = term, GroupNum = "1", IsIO = true, IOTerm = term, DayCount = "Actual/360",
        PaymentFrequency = freq, NextPaymentDate = next, MaturityDate = maturity
    };

    private static List<PeriodCashflowDto> Run(AssetDto loan, double cpr = 0, double cdr = 0, int? cutoffDays = null,
        Dictionary<string, List<double[]>>? rates = null)
    {
        var ok = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(new CalcCollateralRequest
        {
            Assets = new List<AssetDto> { loan }, ProjectionDate = Proj, InterestAccrualStartDate = Closing,
            CollectionCutoffBusinessDays = cutoffDays, MarketRates = rates,
            Assumptions = new AssumptionsDto { Cpr = cpr, Cdr = cdr, Severity = 0 }
        }).Result.Should().BeOfType<OkObjectResult>().Subject;
        return ((CalcCollateralResponse)ok.Value!).Cashflows.OrderBy(c => c.Period).ToList();
    }

    // --- the first payment ---------------------------------------------------------------------

    [Fact]
    public void A_quarterly_loan_paying_in_the_first_month_prepays_a_whole_quarter_at_it()
    {
        var q = Run(Loan(4, new DateTime(2025, 12, 31)), cpr: 15);
        q[0].Balance.Should().BeApproximately(Bal * Math.Pow(1 - Smm(15), 3), 0.01,
            "its quarter began in October; one month's prepayment kept 2.7% too much of the loan");
    }

    [Fact]
    public void A_monthly_loan_prepays_nothing_before_its_first_payment_and_one_month_at_it()
    {
        // Jan 22 is past January's cutoff (10 business days before Jan 20): February collects it.
        var m = Run(Loan(12, new DateTime(2026, 1, 22)), cpr: 15, cutoffDays: 10);
        m[0].Balance.Should().Be(Bal, "it has not paid yet");
        m[1].Balance.Should().BeApproximately(Bal * (1 - Smm(15)), 0.01);
    }

    [Fact]
    public void A_first_payment_late_in_the_projection_still_carries_exactly_one_period()
    {
        // Apr 15 is past April's cutoff: May's row (row 4) collects it — one quarter, not five months.
        var q = Run(Loan(4, new DateTime(2026, 4, 15)), cpr: 15, cutoffDays: 10);
        q[3].Balance.Should().Be(Bal);
        q[4].Balance.Should().BeApproximately(Bal * Math.Pow(1 - Smm(15), 3), 0.01);
    }

    [Fact]
    public void Collateral_placed_by_its_purchase_keeps_the_months_since_it_was_bought()
    {
        var h = Enumerable.Repeat(0.01, 8).ToArray();
        PaymentSchedule.Concentrate(h, 100, 101, 3);
        h[1].Should().BeApproximately(1 - Math.Pow(0.99, 2), 1e-12, "no stated calendar: the months since the start");
        var stated = Enumerable.Repeat(0.01, 8).ToArray();
        PaymentSchedule.Concentrate(stated, 100, 101, 3, statedCalendar: true);
        stated[0].Should().Be(0);
        stated[1].Should().BeApproximately(1 - Math.Pow(0.99, 3), 1e-12, "a stated calendar: one whole period");
    }

    // --- the maturity date ---------------------------------------------------------------------

    [Fact]
    public void A_stated_maturity_date_is_the_maturity_and_the_last_payment_accrues_only_to_it()
    {
        // A 24-month term from Dec 23 would mature in the Dec 2027 row; the stated date is Nov 12, 2027,
        // collected (past November's cutoff) in December's row anyway — but paid only to Nov 12.
        var q = Run(Loan(4, new DateTime(2025, 12, 31), new DateTime(2027, 11, 12)), cutoffDays: 10);
        var last = q.Last(c => c.Interest > 0);
        last.CashflowDate.Should().Be(new DateTime(2027, 12, 20));
        last.Interest.Should().BeApproximately(Bal * Cpn / 100 * (new DateTime(2027, 11, 12) - new DateTime(2027, 9, 30)).TotalDays / 360, 0.01,
            "Sep 30 -> Nov 12 (43 days), not to the row's Dec 20 (81)");
        last.ScheduledPrincipal.Should().BeApproximately(Bal, 0.01);
    }

    [Fact]
    public void A_maturity_date_moves_the_payoff_to_the_row_that_collects_it()
    {
        // Matures Oct 2, 2027 — before October's cutoff — so October's row repays it, though a 24-month
        // term from closing would run to December.
        var q = Run(Loan(4, new DateTime(2025, 12, 31), new DateTime(2027, 10, 2)), cutoffDays: 10);
        q.Single(c => c.ScheduledPrincipal > 0).CashflowDate.Should().Be(new DateTime(2027, 10, 20));
    }

    [Fact]
    public void A_monthly_loan_without_a_calendar_is_paid_to_its_maturity_date_too()
    {
        var m = Run(Loan(null, null, new DateTime(2027, 12, 5)));
        var last = m.Last(c => c.Interest > 0);
        last.CashflowDate.Should().Be(new DateTime(2027, 12, 20));
        last.Interest.Should().BeApproximately(Bal * Cpn / 100 * 15 / 360, 0.01, "Nov 20 -> Dec 5");
    }

    // --- a default on a payment date -----------------------------------------------------------

    [Fact]
    public void The_part_defaulting_on_a_payment_date_does_not_pay_its_coupon()
    {
        var q = Run(Loan(4, new DateTime(2026, 1, 15)), cdr: 10);
        var clean = Run(Loan(4, new DateTime(2026, 1, 15)));
        var defaulted = q[3].DefaultedPrincipal / q[2].Balance;
        defaulted.Should().BeGreaterThan(0);
        // Jan 15 -> Apr 15 on what January's default left, less the part defaulting on Apr 15.
        q[3].Interest.Should().BeApproximately(q[0].Balance * Cpn / 100 * 90 / 360 * (1 - defaulted), 0.01);
        clean[3].Interest.Should().BeApproximately(Bal * Cpn / 100 * 90 / 360, 0.01);
    }

    [Fact]
    public void Bought_collateral_paying_quarterly_loses_the_defaulting_coupon_too()
    {
        // Collateral bought by the reinvestment loop: quarterly, placed by its purchase (no stated
        // date). It pays the quarter's interest on its payment row less the part defaulting on it.
        PeriodCashflows[] RunBought(double cdr)
        {
            var startT = DateUtil.CalcAbsT(Proj);
            var bought = new Asset
            {
                AssetName = "b", AssetId = "b", InterestRateType = InterestRateType.FRM, OriginalDate = Proj,
                OriginalBalance = Bal, CurrentBalance = Bal, BalanceAtIssuance = Bal, OriginalInterestRate = Cpn,
                CurrentInterestRate = Cpn, OriginalAmortizationTerm = 24, AmortizationType = AmortizationType.Bullet,
                GroupNum = "1", PaymentFrequency = 4, FirstPaymentAbsT = startT + 3
            };
            IAssetAssumptions a = new AssetAssumptions(PrepaymentTypeEnum.CPR, new ConstVector(startT, 0.0),
                DefaultTypeEnum.CDR, new ConstVector(startT, cdr), new ConstVector(startT, 0.0));
            return CfCore.GenerateAssetCashflows(new List<IAsset> { bought }, Proj, null, _ => a, null!)
                .PeriodCashflows.OrderBy(c => c.CashflowDate).ToArray();
        }

        var clean = RunBought(0);
        var stressed = RunBought(10);
        var defaulted = stressed[3].DefaultedPrincipal / stressed[3].BeginBalance;
        defaulted.Should().BeGreaterThan(0);
        stressed[3].Interest.Should().BeApproximately(clean[3].Interest * (1 - defaulted), 0.01);
    }

    [Fact]
    public void A_monthly_loan_without_a_calendar_keeps_its_coupon_on_the_month_it_defaults()
    {
        var m = Run(Loan(null, null), cdr: 10);
        m[1].Interest.Should().BeApproximately(m[0].Balance * Cpn / 100 * 31 / 360, 0.01,
            "the reference-calc standard for monthly collateral is unchanged");
    }

    // --- a floating coupon resets on payment dates ---------------------------------------------

    [Fact]
    public void A_floating_coupon_holds_to_the_first_payment_and_resets_on_it()
    {
        var floater = Loan(4, new DateTime(2026, 3, 23));
        floater.InterestRateType = "ARM";
        floater.CurrentInterestRate = floater.OriginalInterestRate = 3.5;
        floater.IndexName = "Sofr90Avg";
        floater.IndexMargin = 3.5;
        floater.InitialAdjustmentPeriod = 1;
        floater.AdjustmentPeriod = 1;
        var q = Run(floater, rates: new Dictionary<string, List<double[]>> { ["Sofr90Avg"] = new() { new[] { 0.0, 3.659 } } });
        q[3].Interest.Should().BeApproximately(Bal * 3.5 / 100 * 90 / 360, 0.01,
            "Dec 23 -> Mar 23 at the coupon fixed for that period, not the index from February");
        q[6].Interest.Should().BeApproximately(Bal * 7.159 / 100 * 92 / 360, 0.01,
            "Mar 23 -> Jun 23 at the reset coupon, including the days of March after the payment");
    }
}
