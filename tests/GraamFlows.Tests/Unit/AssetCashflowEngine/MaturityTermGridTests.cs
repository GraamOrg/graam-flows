using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.AssetCashflowEngine;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// A tape's whole-month remaining term is counted from its as-of date: measured on a reference model's
/// tape, every asset's remaining term was the months from that date to its maturity, rounded up. Its
/// asset-level export defaults each loan on that monthly grid: the maturity carries the grid months
/// since the loan's last payment, and a scheduled payment in the grid month the maturity ends is not
/// made — the maturity replaces it. Per-loan maturity-row defaults: 29 of 394 loans off by a month or
/// more -> none; the pool tie-out's residual difference fell by about 90% in each scenario.
/// </summary>
public class MaturityTermGridTests
{
    private static readonly DateTime AsOf = new(2025, 12, 3);
    private static readonly DateTime MonthEnd = new(2025, 12, 31);

    [Fact]
    public void A_maturity_past_the_grid_day_takes_another_month_one_on_it_none()
    {
        // A month-end payer: maturing Aug 4, its Jul 31 payment is made and the maturity adds a month;
        // maturing Aug 3 (the grid month Jul 31 is in), Jul 31 is not made and the maturity carries
        // the one month since Jun 30.
        var proj = new DateTime(2026, 1, 20);
        PaymentSchedule.IsPayment(new DateTime(2027, 7, 31), new DateTime(2027, 8, 4), AsOf).Should().BeTrue();
        PaymentSchedule.MaturityStubMonths(MonthEnd, 1, proj, new DateTime(2027, 8, 4), AsOf).Should().Be(1);
        PaymentSchedule.IsPayment(new DateTime(2027, 7, 31), new DateTime(2027, 8, 3), AsOf).Should().BeFalse();
        PaymentSchedule.MaturityStubMonths(MonthEnd, 1, proj, new DateTime(2027, 8, 3), AsOf).Should().Be(1);
    }

    [Fact]
    public void A_payment_in_the_maturitys_grid_month_is_replaced_by_it()
    {
        // Quarterly on the 6th, maturing on the 18th of a payment month: that month's payment is not made,
        // and the maturity carries the quarter since the one before.
        var proj = new DateTime(2026, 1, 20);
        var stated = new DateTime(2026, 1, 6);
        PaymentSchedule.IsPayment(new DateTime(2027, 7, 6), new DateTime(2027, 7, 18), AsOf).Should().BeFalse();
        PaymentSchedule.IsPayment(new DateTime(2027, 4, 6), new DateTime(2027, 7, 18), AsOf).Should().BeTrue();
        PaymentSchedule.MaturityStubMonths(stated, 3, proj, new DateTime(2027, 7, 18), AsOf).Should().Be(3);
    }

    [Fact]
    public void A_maturity_on_a_payment_date_is_that_payment_and_without_the_grid_every_date_to_it_pays()
    {
        var proj = new DateTime(2026, 1, 20);
        PaymentSchedule.MaturityStubMonths(new DateTime(2026, 1, 6), 3, proj, new DateTime(2027, 7, 6), AsOf)
            .Should().Be(0);
        PaymentSchedule.IsPayment(new DateTime(2027, 7, 6), new DateTime(2027, 7, 6), AsOf).Should().BeTrue();
        PaymentSchedule.IsPayment(new DateTime(2027, 7, 6), new DateTime(2027, 7, 18), null).Should().BeTrue();
        PaymentSchedule.IsPayment(new DateTime(2027, 7, 19), new DateTime(2027, 7, 18), null).Should().BeFalse();
        // No payment before the maturity in the projection: nothing to count from.
        PaymentSchedule.MaturityStubMonths(new DateTime(2026, 7, 6), 12, proj, new DateTime(2026, 3, 1), AsOf)
            .Should().BeNull();
    }

    private static Dictionary<DateTime, PeriodCashflowDto> Run(AssetDto loan, DateTime? asOf, double cdr = 10)
    {
        loan.RemainingTermAsOfDate = asOf;
        var ok = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(new CalcCollateralRequest
        {
            Assets = new List<AssetDto> { loan }, ProjectionDate = new DateTime(2026, 1, 20),
            CollectionCutoffBusinessDays = 10, HolidayCalendar = "FederalReserve",
            Assumptions = new AssumptionsDto { Cpr = 0, Cdr = cdr, Severity = 0 }
        }).Result.Should().BeOfType<OkObjectResult>().Subject;
        return ((CalcCollateralResponse)ok.Value!).Cashflows.ToDictionary(c => c.CashflowDate);
    }

    private static AssetDto Loan(DateTime next, int frequency, DateTime maturity) => new()
    {
        AssetName = "l", AssetId = "l", InterestRateType = "FRM", OriginalDate = new DateTime(2025, 12, 23),
        OriginalBalance = 1_000_000, CurrentBalance = 1_000_000, OriginalInterestRate = 8, CurrentInterestRate = 8,
        OriginalAmortizationTerm = 60, GroupNum = "1", IsIO = true, IOTerm = 60, DayCount = "Actual/360",
        PaymentFrequency = frequency, NextPaymentDate = next, MaturityDate = maturity
    };

    private static readonly double Mdr = 1 - Math.Pow(0.9, 1.0 / 12);

    [Fact]
    public void End_to_end_a_month_end_payer_maturing_on_the_4th_defaults_two_months_in_its_last_row()
    {
        // Jul 31 and Aug 4 are both collected by August's row (cutoff Aug 6).
        var loan = Loan(MonthEnd, 12, new DateTime(2027, 8, 4));
        var aug = new DateTime(2027, 8, 20);
        var grid = Run(loan, AsOf)[aug];
        grid.DefaultedPrincipal.Should().BeApproximately(grid.BeginBalance * (1 - Math.Pow(1 - Mdr, 2)), 0.01);
        var plain = Run(loan, null)[aug];
        plain.DefaultedPrincipal.Should().BeApproximately(plain.BeginBalance * Mdr, 0.01,
            "without the as-of date, the row's one payment, as before");
    }

    [Fact]
    public void A_date_after_the_maturity_is_no_payment_with_the_grid_and_counts_as_before_without_it()
    {
        // Monthly on the 6th, maturing Feb 7: March's row collects Feb 6, Feb 7 and Mar 6. With the
        // grid, Feb 6 is replaced by the maturity and Mar 6 is past it: one month, the reference
        // model's. Without it the row's two scheduled dates count, as before.
        var loan = Loan(new DateTime(2026, 1, 6), 12, new DateTime(2029, 2, 7));
        var mar = new DateTime(2029, 3, 20);
        var grid = Run(loan, AsOf)[mar];
        grid.DefaultedPrincipal.Should().BeApproximately(grid.BeginBalance * Mdr, 0.01);
        var plain = Run(loan, null)[mar];
        plain.DefaultedPrincipal.Should().BeApproximately(plain.BeginBalance * (1 - Math.Pow(1 - Mdr, 2)), 0.01);
    }

    [Fact]
    public void End_to_end_the_replaced_payment_pays_nothing_and_the_maturity_a_quarter_a_row_later()
    {
        // July's row collects the Jul 6 date (cutoff Jul 6); the Jul 18 maturity is August's.
        var loan = Loan(new DateTime(2026, 1, 6), 4, new DateTime(2027, 7, 18));
        var rows = Run(loan, AsOf);
        var jul = rows[new DateTime(2027, 7, 20)];
        var aug = rows[new DateTime(2027, 8, 20)];
        jul.DefaultedPrincipal.Should().Be(0);
        jul.Interest.Should().Be(0, "no payment is made on Jul 6");
        aug.DefaultedPrincipal.Should().BeApproximately(aug.BeginBalance * (1 - Math.Pow(1 - Mdr, 3)), 0.01);
        aug.Interest.Should().BeGreaterThan(0, "the last quarter's coupon comes with the maturity");

        var plain = Run(loan, null);
        plain[new DateTime(2027, 7, 20)].DefaultedPrincipal.Should().BeGreaterThan(0, "as before: Jul 6 is paid");
    }
}
