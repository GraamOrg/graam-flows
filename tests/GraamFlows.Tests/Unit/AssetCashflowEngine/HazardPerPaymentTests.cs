using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.AssetCashflowEngine;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// A loan on a stated calendar prepays and defaults once per PAYMENT, each payment carrying one payment
/// period's hazard — not the hazard of the projection rows since its last payment. Its payments are
/// collected by a cutoff that moves with weekends and holidays, so one can land a row late (the rows
/// around it then hold 4 and 2 months, not 3 and 3) or two in one row. Measured loan by loan against a
/// reference model's asset-level export (394 loans): per-row default differences 104k -> 23k,
/// prepayment 932k -> 151k, lifetime interest +17k -> +2k.
/// </summary>
public class HazardPerPaymentTests
{
    [Fact]
    public void Each_payment_carries_one_period_however_many_rows_since_the_last()
    {
        // Quarterly, payments in rows 0, 4 (a row late), 6, 9: every one carries 3 months.
        var h = Enumerable.Repeat(0.01, 12).ToArray();
        var rows = new int[12]; rows[0] = rows[4] = rows[6] = rows[9] = 1;
        PaymentSchedule.Concentrate(h, 100, 100, 3, statedCalendar: true, paymentRows: rows);
        var oneQuarter = 1 - Math.Pow(0.99, 3);
        foreach (var p in new[] { 0, 4, 6, 9 })
            h[p].Should().BeApproximately(oneQuarter, 1e-12, $"row {p}");
        h.Where((_, p) => rows[p] == 0).Should().OnlyContain(x => x == 0);
    }

    [Fact]
    public void A_row_collecting_two_payments_carries_two_periods()
    {
        var h = Enumerable.Repeat(0.01, 6).ToArray();
        var rows = new[] { 1, 0, 2, 1, 1, 1 };
        PaymentSchedule.Concentrate(h, 100, 100, 1, statedCalendar: true, paymentRows: rows);
        h[2].Should().BeApproximately(1 - Math.Pow(0.99, 2), 1e-12);
        h[3].Should().BeApproximately(0.01, 1e-12);
    }

    [Fact]
    public void A_quarterly_payment_collected_a_row_late_still_defaults_one_quarter()
    {
        // Pays on the 6th; Nov 6, 2026 is past November's cutoff (10 business days before the 20th:
        // the 5th), so December's row collects it — three months after August's, still one quarter.
        var loan = new AssetDto
        {
            AssetName = "q", AssetId = "q", InterestRateType = "FRM", OriginalDate = new DateTime(2025, 12, 23),
            OriginalBalance = 1_000_000, CurrentBalance = 1_000_000, OriginalInterestRate = 8, CurrentInterestRate = 8,
            OriginalAmortizationTerm = 60, GroupNum = "1", IsIO = true, IOTerm = 60, DayCount = "Actual/360",
            PaymentFrequency = 4, NextPaymentDate = new DateTime(2026, 2, 6)
        };
        var ok = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(new CalcCollateralRequest
        {
            Assets = new List<AssetDto> { loan }, ProjectionDate = new DateTime(2026, 1, 20),
            CollectionCutoffBusinessDays = 10, HolidayCalendar = "FederalReserve",
            Assumptions = new AssumptionsDto { Cpr = 0, Cdr = 10, Severity = 0 }
        }).Result.Should().BeOfType<OkObjectResult>().Subject;
        var rows = ((CalcCollateralResponse)ok.Value!).Cashflows.ToDictionary(c => c.CashflowDate);
        var dec = rows[new DateTime(2026, 12, 20)];
        rows[new DateTime(2026, 11, 20)].DefaultedPrincipal.Should().Be(0);
        var mdr = 1 - Math.Pow(0.9, 1.0 / 12);
        dec.DefaultedPrincipal.Should().BeApproximately(dec.BeginBalance * (1 - Math.Pow(1 - mdr, 3)), 0.01,
            "one quarter, not the four months since August's payment row");
    }
}
