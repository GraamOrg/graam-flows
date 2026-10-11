using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.AssetCashflowEngine;
using GraamFlows.Domain;
using GraamFlows.Objects.Util;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// An asset that states its next payment date is paid, on each payment, the interest accrued from its
/// previous payment date to THAT date — not to the date of the projection row the payment lands in —
/// and its first payment pays only what accrued since the pool began accruing (a deal's Closing Date).
///
/// Paid to the row's date, every loan's coupon ran up to a month ahead of the loan's own: a pool of
/// loans paying on assorted days collected, in a deal's first Collection Period, the interest on every
/// loan up to the row date plus the interest accrued before the deal bought it. Measured on a reinvesting
/// pool against an independent reference model, the reference pays each loan's first coupon from the
/// Closing Date to the loan's first payment date (to the day) and every later coupon from payment date
/// to payment date; the projection over-collected its first Collection Period by about half a percent
/// of the pool.
/// </summary>
public class PayDateAccrualTests
{
    // Row p is dated Proj + p months and covers the month before it: row 0 is Dec 20 -> Jan 20.
    private static readonly DateTime Proj = new(2026, 1, 20);
    private static readonly DateTime Closing = new(2025, 12, 23);
    private const double Bal = 1_000_000, Cpn = 8.0;
    private static double Days(double d) => Bal * Cpn / 100 * d / 360; // Actual/360 interest on the full balance

    private static AssetDto Loan(int? freq, DateTime? next) => new()
    {
        AssetName = "l", AssetId = "l", InterestRateType = "FRM", OriginalDate = new DateTime(2025, 12, 1),
        OriginalBalance = Bal, CurrentBalance = Bal, OriginalInterestRate = Cpn, CurrentInterestRate = Cpn,
        OriginalAmortizationTerm = 24, GroupNum = "1", IsIO = true, IOTerm = 24, DayCount = "Actual/360",
        PaymentFrequency = freq, NextPaymentDate = next
    };

    private static List<PeriodCashflowDto> Run(AssetDto loan, double cpr = 0, DateTime? accrualStart = null,
        int? cutoffDays = null)
    {
        var ok = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(new CalcCollateralRequest
        {
            Assets = new List<AssetDto> { loan }, ProjectionDate = Proj, InterestAccrualStartDate = accrualStart,
            CollectionCutoffBusinessDays = cutoffDays,
            Assumptions = new AssumptionsDto { Cpr = cpr, Cdr = 0, Severity = 0 }
        }).Result.Should().BeOfType<OkObjectResult>().Subject;
        return ((CalcCollateralResponse)ok.Value!).Cashflows.OrderBy(c => c.Period).ToList();
    }

    [Fact]
    public void A_quarterly_loans_first_payment_pays_from_the_accrual_start_to_its_payment_date()
    {
        var q = Run(Loan(4, new DateTime(2026, 1, 15)), accrualStart: Closing);
        q[0].Interest.Should().BeApproximately(Days(23), 0.01, "Dec 23 -> Jan 15, not Dec 20 -> Jan 20 (28 days)");
    }

    [Fact]
    public void Every_later_payment_pays_from_the_previous_payment_date_to_its_own()
    {
        var q = Run(Loan(4, new DateTime(2026, 1, 15)), accrualStart: Closing);
        q[3].Interest.Should().BeApproximately(Days(90), 0.01, "Jan 15 -> Apr 15");
        q[6].Interest.Should().BeApproximately(Days(91), 0.01, "Apr 15 -> Jul 15");
        q[1].Interest.Should().Be(0); q[2].Interest.Should().Be(0);
    }

    [Fact]
    public void A_monthly_loan_that_states_its_payment_date_is_paid_to_it_too()
    {
        var m = Run(Loan(12, new DateTime(2026, 1, 5)), accrualStart: Closing);
        m[0].Interest.Should().BeApproximately(Days(13), 0.01, "Dec 23 -> Jan 5");
        m[1].Interest.Should().BeApproximately(Days(31), 0.01, "Jan 5 -> Feb 5");
        m[2].Interest.Should().BeApproximately(Days(28), 0.01, "Feb 5 -> Mar 5");
    }

    [Fact]
    public void A_monthly_payment_after_a_rows_collection_cutoff_is_collected_in_the_next_row()
    {
        // Jan 18 is after January's cutoff (10 business days before Jan 20): February collects it.
        var m = Run(Loan(12, new DateTime(2026, 1, 18)), accrualStart: Closing, cutoffDays: 10);
        m[0].Interest.Should().Be(0, "nothing is paid by January's cutoff");
        m[1].Interest.Should().BeApproximately(Days(26), 0.01, "Dec 23 -> Jan 18");
        m[2].Interest.Should().BeApproximately(Days(31), 0.01, "Jan 18 -> Feb 18");
    }

    [Fact]
    public void A_loan_that_states_no_payment_date_is_paid_to_the_row_date_as_before()
    {
        var m = Run(Loan(null, null));
        m[0].Interest.Should().BeApproximately(Days(31), 0.01, "Dec 20 -> Jan 20");
        var started = Run(Loan(null, null), accrualStart: Closing);
        started[0].Interest.Should().BeApproximately(Days(28), 0.01, "the accrual start still applies: Dec 23 -> Jan 20");
        started[1].Interest.Should().BeApproximately(Days(31), 0.01);
    }

    [Fact]
    public void Without_an_accrual_start_the_first_payment_accrues_from_the_start_of_the_projection()
    {
        var q = Run(Loan(4, new DateTime(2026, 1, 15)));
        q[0].Interest.Should().BeApproximately(Days(26), 0.01, "Dec 20 -> Jan 15");
    }

    [Fact]
    public void Nothing_accrued_is_lost_the_last_payment_pays_through_the_payoff()
    {
        var q = Run(Loan(4, new DateTime(2026, 1, 15)), accrualStart: Closing);
        var last = q.Last(c => c.Interest > 0);
        q.Sum(c => c.Interest).Should().BeApproximately(Days((last.CashflowDate - Closing).TotalDays), 0.05,
            "the interest is the loan's accrual from the Closing Date to its payoff row, only re-timed");
    }

    [Fact]
    public void Prepayments_leave_on_the_payment_date_so_the_carried_days_accrue_on_what_is_left()
    {
        var q = Run(Loan(4, new DateTime(2026, 1, 15)), cpr: 20, accrualStart: Closing);
        q[0].UnscheduledPrincipal.Should().BeGreaterThan(0);
        // Jan 15 -> Apr 15 on the balance the January payment left: the 5 days after Jan 15 in row 0
        // are carried on it, not on the balance before the prepayment.
        q[3].Interest.Should().BeApproximately(q[0].Balance * Cpn / 100 * 90 / 360, 0.01);
    }

    [Fact]
    public void A_payment_date_already_gone_by_is_rolled_to_the_next_one()
    {
        // A tape's Oct 15 "next" date on a Jan 20 projection: the loan's next payment is Jan 15.
        PaymentSchedule.PaymentDate(new DateTime(2025, 10, 15), 3, Proj, 0).Should().Be(new DateTime(2026, 1, 15));
        PaymentSchedule.PaymentDate(new DateTime(2025, 10, 15), 3, Proj, 2).Should().Be(new DateTime(2026, 7, 15));
        PaymentSchedule.PaymentDate(new DateTime(2026, 1, 31), 1, Proj, 1).Should().Be(new DateTime(2026, 2, 28));
        PaymentSchedule.PaymentDate(new DateTime(2026, 1, 31), 1, Proj, 2).Should().Be(new DateTime(2026, 3, 31),
            "counted from the anchor, a month-end date stays on month ends");
        var startT = DateUtil.CalcAbsT(Proj);
        PaymentSchedule.FirstPaymentAbsT(new Asset { AssetId = "x", PaymentFrequency = 4, NextPaymentDate = new DateTime(2025, 10, 15) },
            Proj, 24, null).Should().Be(startT);
        var stale = Run(Loan(4, new DateTime(2025, 10, 15)), accrualStart: Closing);
        stale[0].Interest.Should().BeApproximately(Days(23), 0.01, "the rolled date, Jan 15");
    }
}
