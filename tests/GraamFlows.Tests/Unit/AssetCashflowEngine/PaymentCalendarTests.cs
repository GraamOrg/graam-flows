using FluentAssertions;
using GraamFlows;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
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
/// Collateral on its own payment calendar, and reinvestment on the deal's.
///
/// A quarterly-pay loan accrues every month but pays interest — and prepays, defaults, recovers —
/// only on its payment dates, so its balance stays whole through the period. Projected monthly it
/// lost balance mid-period, and a pool of such loans (and the collateral reinvested into it, which
/// matured mid-period years later) under-earned by the part of each period after an event. Measured
/// on a reinvesting CLO against an independent reference model: per-loan, the reference books each
/// loan's prepayments, defaults and interest only in its own payment months, and its collections are
/// reinvested on Payment Dates, not monthly.
/// </summary>
public class PaymentCalendarTests
{
    private static readonly DateTime Proj = new(2026, 1, 20);

    // --- the calendar arithmetic ------------------------------------------------------------------

    [Theory]
    [InlineData(12, 1)]
    [InlineData(4, 3)]
    [InlineData(2, 6)]
    [InlineData(1, 12)]
    public void Frequencies_map_to_months_between_payments(int freq, int months) =>
        PaymentSchedule.MonthsBetween(freq).Should().Be(months);

    [Fact]
    public void An_unknown_frequency_is_refused() =>
        FluentActions.Invoking(() => PaymentSchedule.MonthsBetween(5)).Should().Throw<ArgumentException>();

    [Fact]
    public void A_payment_date_lands_in_the_first_row_whose_cutoff_reaches_it()
    {
        // Rows on the 20th; a cutoff 15 days before each row's date.
        DateTime Cut(DateTime row) => row.AddDays(-15);
        Asset A(DateTime next) => new() { AssetId = "x", PaymentFrequency = 4, NextPaymentDate = next };
        var startT = DateUtil.CalcAbsT(Proj);
        PaymentSchedule.FirstPaymentAbsT(A(new DateTime(2026, 1, 5)), Proj, 24, Cut).Should().Be(startT, "Jan 5 is on Jan's cutoff");
        PaymentSchedule.FirstPaymentAbsT(A(new DateTime(2026, 1, 6)), Proj, 24, Cut).Should().Be(startT + 1, "a day past it is February's");
        PaymentSchedule.FirstPaymentAbsT(A(new DateTime(2026, 1, 6)), Proj, 24, null).Should().Be(startT, "no cutoff: the row date itself");
        PaymentSchedule.FirstPaymentAbsT(new Asset { AssetId = "x", PaymentFrequency = 4, FirstPaymentAbsT = startT + 2 }, Proj, 24, Cut)
            .Should().Be(startT + 2, "a placed first payment wins");
        FluentActions.Invoking(() => PaymentSchedule.FirstPaymentAbsT(new Asset { AssetId = "x", PaymentFrequency = 4 }, Proj, 24, Cut))
            .Should().Throw<ArgumentException>().WithMessage("*next payment date*");
    }

    [Fact]
    public void Concentrating_a_hazard_moves_it_onto_payment_rows_and_keeps_its_total()
    {
        var h = Enumerable.Repeat(0.01, 12).ToArray();
        var startT = 100;
        PaymentSchedule.Concentrate(h, startT, startT + 1, 3);
        var expected = 1 - Math.Pow(0.99, 3);
        h[0].Should().BeApproximately(0, 1e-12);
        h[1].Should().BeApproximately(1 - Math.Pow(0.99, 2), 1e-12, "the first payment carries the months since the start");
        h[2].Should().Be(0); h[3].Should().Be(0);
        h[4].Should().BeApproximately(expected, 1e-12);
        h.Aggregate(1.0, (s, x) => s * (1 - x)).Should().BeApproximately(Math.Pow(0.99, 11) * (1.0), 1e-12,
            "survival through the last payment row is unchanged; the trailing month waits for the next one");
    }

    // --- a quarterly loan through the API ---------------------------------------------------------

    private static AssetDto Loan(int? freq, DateTime? next) => new()
    {
        AssetName = "q", AssetId = "q", InterestRateType = "FRM", OriginalDate = new DateTime(2025, 12, 23),
        OriginalBalance = 1_000_000, CurrentBalance = 1_000_000, OriginalInterestRate = 8.0, CurrentInterestRate = 8.0,
        OriginalAmortizationTerm = 60, GroupNum = "1", IsIO = true, IOTerm = 60,
        PaymentFrequency = freq, NextPaymentDate = next
    };

    private static List<PeriodCashflowDto> Run(AssetDto loan, double cpr = 20, int? cutoffDays = null)
    {
        var ok = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(new CalcCollateralRequest
        {
            Assets = new List<AssetDto> { loan }, ProjectionDate = Proj, CollectionCutoffBusinessDays = cutoffDays,
            Assumptions = new AssumptionsDto { Cpr = cpr, Cdr = 0, Severity = 0 }
        }).Result.Should().BeOfType<OkObjectResult>().Subject;
        return ((CalcCollateralResponse)ok.Value!).Cashflows.OrderBy(c => c.Period).ToList();
    }

    [Fact]
    public void A_quarterly_loan_pays_and_prepays_only_on_its_payment_rows()
    {
        var q = Run(Loan(4, new DateTime(2026, 1, 15)));
        for (var p = 0; p < 12; p++)
        {
            var pays = p % 3 == 0;
            (q[p].Interest > 0).Should().Be(pays, $"row {p}");
            (q[p].UnscheduledPrincipal > 0).Should().Be(pays, $"row {p}");
        }
    }

    [Fact]
    public void Its_balance_stays_whole_through_the_period_so_it_earns_more_than_a_monthly_payer()
    {
        var q = Run(Loan(4, new DateTime(2026, 1, 15)));
        var m = Run(Loan(null, null));
        // Same prepayment through a payment row (concentrated, not reduced): rows 0..12 cover 13
        // months either way — the quarterly loan's rows 0, 3, 6, 9, 12 carry 1 + 3 + 3 + 3 + 3.
        q.Take(13).Sum(c => c.UnscheduledPrincipal).Should().BeApproximately(m.Take(13).Sum(c => c.UnscheduledPrincipal), 1.0,
            "the same CPR over the same months, only on payment dates");
        // ... but interest on a balance that is not reduced mid-quarter.
        q.Take(13).Sum(c => c.Interest).Should().BeGreaterThan(m.Take(13).Sum(c => c.Interest));
        // Between payments the quarter's interest is exactly the monthly accrual on the whole balance.
        q[3].Interest.Should().BeApproximately(q[1].BeginBalance * 0.08 / 12 * 3, 1e-6);
    }

    [Fact]
    public void The_collection_cutoff_places_the_first_payment_on_business_days()
    {
        // Ten US business days before 2026-01-20 (MLK day is the 19th) is Jan 5; before Feb 20
        // (Presidents' Day the 16th) it is Feb 5.
        var onCutoff = Run(Loan(4, new DateTime(2026, 1, 5)), cutoffDays: 10);
        var dayAfter = Run(Loan(4, new DateTime(2026, 1, 6)), cutoffDays: 10);
        onCutoff[0].Interest.Should().BeGreaterThan(0, "Jan 5 is collected for the Jan 20 row");
        dayAfter[0].Interest.Should().Be(0);
        dayAfter[1].Interest.Should().BeGreaterThan(0, "Jan 6 is collected for the Feb 20 row");
    }

    [Fact]
    public void A_monthly_asset_is_unchanged_and_a_quarterly_one_needs_its_next_date()
    {
        Run(Loan(12, null)).Select(c => c.Interest).Should().Equal(Run(Loan(null, null)).Select(c => c.Interest));
        new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(new CalcCollateralRequest
        {
            Assets = new List<AssetDto> { Loan(4, null) }, ProjectionDate = Proj, Assumptions = new AssumptionsDto()
        }).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- reinvestment on the deal's Payment Dates -------------------------------------------------

    private static List<PeriodCashflows> AmortizingPool(int months = 12) =>
        Enumerable.Range(0, months).Select(p => new PeriodCashflows
        {
            CashflowDate = Proj.AddMonths(p), GroupNum = "1", BeginBalance = 1e6 - 1e4 * p,
            Balance = 1e6 - 1e4 * (p + 1), ScheduledPrincipal = 1e4, Interest = 0
        }).ToList();

    private static IAssetAssumptions Zero() => new AssetAssumptions(PrepaymentTypeEnum.CPR,
        new ConstVector(DateUtil.CalcAbsT(Proj), 0.0), DefaultTypeEnum.CDR, new ConstVector(DateUtil.CalcAbsT(Proj), 0.0),
        new ConstVector(DateUtil.CalcAbsT(Proj), 0.0));

    private static ReinvestmentConfig Cfg(bool onPayDates, int templateFreq = 12, int windowMonths = 6, int postMonths = 0) => new()
    {
        ReinvestStartDate = Proj, ReinvestEndDate = Proj.AddMonths(windowMonths), ReinvestAllEligibleProceeds = true,
        ReinvestOnPaymentDatesOnly = onPayDates,
        PostReinvestmentEndDate = postMonths > 0 ? Proj.AddMonths(windowMonths + postMonths) : null,
        PostReinvestmentEligibleProceeds = EligibleProceeds.ScheduledPrincipal | EligibleProceeds.Prepayments,
        Templates = new[]
        {
            new ReinvestTemplate
            {
                AllocationPct = 100, Price = 100, AmortizationType = AmortizationType.Bullet, CouponRate = 6.0,
                TermMonths = 36, PaymentFrequency = templateFreq
            }
        }
    };

    private static bool Quarterly(DateTime d) => PayCalendar.IsPayMonth(d, Proj, 3);

    [Fact]
    public void During_the_window_collections_are_bought_on_payment_dates_with_everything_held()
    {
        var r = CfCore.BuildReinvestment(AmortizingPool(), Cfg(true), Proj, Zero(), null, isPaymentDate: Quarterly);
        r.Purchases.Should().OnlyContain(p => Quarterly(p.CashflowDate), "no purchase between Payment Dates");
        var apr = r.Purchases.Single(p => p.CashflowDate == Proj.AddMonths(3));
        apr.CashSpent.Should().BeApproximately(3e4, 1e-6, "February's, March's and April's proceeds");
        // Each month's share is drawn from its own row.
        foreach (var month in new[] { 1, 2, 3 })
            r.Cashflows.Single(c => c.CashflowDate == Proj.AddMonths(month)).ScheduledPrincipal
                .Should().BeApproximately(-1e4, 1e-6, $"month {month}'s collection was spent on the April purchase");
    }

    [Fact]
    public void Monthly_buying_is_unchanged_and_the_post_window_buys_monthly()
    {
        var monthly = CfCore.BuildReinvestment(AmortizingPool(), Cfg(false), Proj, Zero(), null);
        monthly.Purchases.Should().HaveCount(7, "every month of a 6-month window plus its start");

        var withPost = CfCore.BuildReinvestment(AmortizingPool(), Cfg(true, postMonths: 4), Proj, Zero(), null, isPaymentDate: Quarterly);
        withPost.Purchases.Where(p => p.CashflowDate > Proj.AddMonths(6)).Should().HaveCount(4,
            "past the reinvestment period the pool buys monthly again");
    }

    [Fact]
    public void Proceeds_held_when_the_window_ends_off_a_payment_date_pay_down()
    {
        // A 5-month window ends in June, not a Payment Date (Jan/Apr/Jul/Oct): May's and June's
        // collections never reach a purchase, and stay distributable.
        var r = CfCore.BuildReinvestment(AmortizingPool(), Cfg(true, windowMonths: 5), Proj, Zero(), null, isPaymentDate: Quarterly);
        r.Purchases.Should().NotContain(p => p.CashflowDate > Proj.AddMonths(3));
        r.Cashflows.Where(c => c.CashflowDate is var d && d > Proj.AddMonths(3) && d <= Proj.AddMonths(5))
            .Sum(c => c.ScheduledPrincipal).Should().BeApproximately(0, 1e-6, "nothing drawn from them");

        // With a post-reinvestment window after it, the first monthly purchase spends its own month
        // only — the held months are not swept into it.
        var post = CfCore.BuildReinvestment(AmortizingPool(), Cfg(true, windowMonths: 5, postMonths: 3), Proj, Zero(), null,
            isPaymentDate: Quarterly);
        post.Purchases.Single(p => p.CashflowDate == Proj.AddMonths(6)).CashSpent.Should().BeApproximately(1e4, 1e-6);
    }

    [Fact]
    public void A_quarterly_template_cohort_pays_on_its_purchase_calendar()
    {
        var r = CfCore.BuildReinvestment(AmortizingPool(), Cfg(true, templateFreq: 4), Proj, Zero(), null, isPaymentDate: Quarterly);
        var bought = r.Purchases.First().CashflowDate; // Jan
        r.Cashflows.Where(c => c.Interest > 0 && c.CashflowDate <= bought.AddMonths(9))
            .Select(c => c.CashflowDate).Should().OnlyContain(d => (d.Month - bought.Month + 12) % 3 == 0,
                "a quarterly cohort bought in January pays in April, July and October");
    }

    [Fact]
    public void The_flag_needs_the_calendar_and_maps_off_the_wire()
    {
        FluentActions.Invoking(() => CfCore.BuildReinvestment(AmortizingPool(), Cfg(true), Proj, Zero(), null))
            .Should().Throw<ArgumentException>().WithMessage("*Payment Dates*");
        var dto = new ReinvestmentDto
        {
            ReinvestEndDate = Proj.AddMonths(6), Target = 1e6, ReinvestOnPaymentDatesOnly = true,
            Templates = new List<ReinvestTemplateDto> { new() { AllocationPct = 100, TermMonths = 36, PaymentFrequency = 4 } }
        };
        var cfg = ReinvestmentConfigMapper.Map(dto, "D")!;
        cfg.ReinvestOnPaymentDatesOnly.Should().BeTrue();
        cfg.Templates[0].PaymentFrequency.Should().Be(4);
        dto.ReinvestOnPaymentDatesOnly = null;
        dto.Templates[0].PaymentFrequency = null;
        ReinvestmentConfigMapper.Map(dto, "D")!.Should().Match<ReinvestmentConfig>(
            c => !c.ReinvestOnPaymentDatesOnly && c.Templates[0].PaymentFrequency == 12);
    }
}
