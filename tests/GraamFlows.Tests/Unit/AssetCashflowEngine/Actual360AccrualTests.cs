using FluentAssertions;
using GraamFlows;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Assumptions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
///     Actual/360 asset accrual (<see cref="AccrualBasis.Actual360" />).
///
///     Every asset used to accrue annual rate / 12 a month (30/360). SOFR-based loans accrue actual
///     days over 360: a year earns 365/360 of the 30/360 coupon (~1.4% more interest) and a 31-day
///     month earns more than February. A collateral pool of such loans projected on 30/360 under-
///     states its interest by that much, every period. The asset (and a reinvestment template) can
///     now say so; absent, accrual is unchanged.
/// </summary>
public class Actual360AccrualTests
{
    private static readonly DateTime Proj = new(2026, 6, 1);
    private const double Face = 1_000_000.0;
    private const double Coupon = 6.0;

    private static AssetDto Loan(string? dayCount, bool interestOnly = true) => new()
    {
        AssetName = "loan", AssetId = "loan", InterestRateType = "FRM",
        OriginalDate = new DateTime(2025, 6, 1), OriginalBalance = Face, CurrentBalance = Face,
        OriginalInterestRate = Coupon, CurrentInterestRate = Coupon, OriginalAmortizationTerm = 60,
        GroupNum = "1", IsIO = interestOnly, IOTerm = interestOnly ? 60 : null, DayCount = dayCount
    };

    private static ActionResult<CalcCollateralResponse> Call(AssetDto loan) =>
        new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(
            new CalcCollateralRequest
            {
                Assets = new List<AssetDto> { loan },
                ProjectionDate = Proj,
                Assumptions = new AssumptionsDto { Cpr = 0, Cdr = 0, Severity = 0 }
            });

    private static List<PeriodCashflowDto> Run(AssetDto loan)
    {
        var ok = Call(loan).Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<CalcCollateralResponse>().Subject
            .Cashflows.OrderBy(c => c.Period).ToList();
    }

    private static double DaysAccrued(int period)
    {
        var start = Proj.AddMonths(period - 1);
        return DateTime.DaysInMonth(start.Year, start.Month);
    }

    [Fact]
    public void An_actual_360_loan_accrues_each_months_actual_days()
    {
        var rows = Run(Loan("Actual/360"));
        rows.Count.Should().BeGreaterThan(24);
        for (var p = 0; p < 24; p++)
            rows[p].Interest.Should().BeApproximately(Face * Coupon / 100 * DaysAccrued(p) / 360.0, 1e-6,
                $"period {p} accrues the {DaysAccrued(p)}-day month ending on its date");
    }

    [Fact]
    public void Over_a_year_actual_360_earns_365_over_360_of_the_30_360_coupon()
    {
        var act = Run(Loan("Actual/360")).Skip(1).Take(12).Sum(c => c.Interest);
        var thirty = Run(Loan(null)).Skip(1).Take(12).Sum(c => c.Interest);
        thirty.Should().BeApproximately(Face * Coupon / 100, 1e-6);
        act.Should().BeApproximately(thirty * 365.0 / 360.0, 1e-6, "June 2026 - May 2027 has 365 days");
    }

    [Fact]
    public void Absent_and_spelled_30_360_are_the_historic_accrual()
    {
        var absent = Run(Loan(null));
        var spelled = Run(Loan("30/360"));
        absent.Select(c => c.Interest).Should().Equal(spelled.Select(c => c.Interest));
        absent.Take(24).Should().OnlyContain(c => Math.Abs(c.Interest - Face * Coupon / 1200) < 1e-9);
    }

    [Fact]
    public void An_unknown_day_count_is_refused_not_accrued_as_30_360()
    {
        var bad = Call(Loan("Actual/365L")).Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().Contain("Actual/365L");
    }

    [Fact]
    public void A_level_pay_actual_360_loan_keeps_its_payment_and_pays_the_extra_interest_from_principal()
    {
        var act = Run(Loan("Actual/360", interestOnly: false));
        var thirty = Run(Loan(null, interestOnly: false));
        for (var p = 0; p < 12; p++)
        {
            var paymentAct = act[p].Interest + act[p].ScheduledPrincipal;
            var paymentThirty = thirty[p].Interest + thirty[p].ScheduledPrincipal;
            paymentAct.Should().BeApproximately(paymentThirty, 0.01, $"period {p}: the payment is contractual");
        }

        // July (31 days) accrues more than 30/360 would, so less of the payment is principal.
        var july = Enumerable.Range(0, 12).First(p => DaysAccrued(p) == 31);
        act[july].ScheduledPrincipal.Should().BeLessThan(thirty[july].ScheduledPrincipal);
    }

    [Fact]
    public void A_reinvestment_template_accrues_its_cohort_actual_360()
    {
        // Pool amortizes 10k a month; the first purchase is at period 0 and its cohort's first
        // period is period 1, which accrues the month starting at Proj (June: 30 days) — so use
        // a July start, whose first cohort month is 31 days.
        var proj = new DateTime(2026, 7, 1);
        var pool = Enumerable.Range(0, 24).Select(p => new PeriodCashflows
        {
            CashflowDate = proj.AddMonths(p), GroupNum = "1", BeginBalance = 1e6 - 1e4 * p,
            Balance = 1e6 - 1e4 * (p + 1), ScheduledPrincipal = 1e4, Interest = 0
        }).ToList();
        var anchor = DateUtil.CalcAbsT(proj);
        var zero = new AssetAssumptions(PrepaymentTypeEnum.CPR, new ConstVector(anchor, 0.0),
            DefaultTypeEnum.CDR, new ConstVector(anchor, 0.0), new ConstVector(anchor, 0.0));

        ReinvestmentResult Loop(AccrualBasis basis) => CfCore.BuildReinvestment(pool, new ReinvestmentConfig
        {
            ReinvestStartDate = proj, ReinvestEndDate = proj, ReinvestAllEligibleProceeds = true,
            Templates = new[]
            {
                new ReinvestTemplate
                {
                    AllocationPct = 100, Price = 100, AmortizationType = AmortizationType.Bullet,
                    CouponRate = Coupon, TermMonths = 36, AccrualBasis = basis
                }
            }
        }, proj, zero, null);

        var act = Loop(AccrualBasis.Actual360);
        var thirty = Loop(AccrualBasis.Thirty360);
        var face = act.Purchases.Single().FaceBought;
        face.Should().BeApproximately(1e4, 1e-6);

        thirty.Cashflows[1].Interest.Should().BeApproximately(face * Coupon / 1200, 1e-9);
        act.Cashflows[1].Interest.Should().BeApproximately(face * Coupon / 100 * 31 / 360, 1e-9,
            "the cohort's first month is July");
        act.Cashflows[2].Interest.Should().BeApproximately(face * Coupon / 100 * 31 / 360, 1e-9,
            "then August");
        act.Cashflows[3].Interest.Should().BeApproximately(face * Coupon / 100 * 30 / 360, 1e-9,
            "then September");
    }

    [Fact]
    public void The_template_day_count_maps_off_the_wire_and_defaults_off()
    {
        static ReinvestmentDto Dto(string? dayCount) => new()
        {
            ReinvestEndDate = Proj.AddMonths(12), Target = 1e6,
            Templates = new List<ReinvestTemplateDto> { new() { AllocationPct = 100, TermMonths = 36, DayCount = dayCount } }
        };

        ReinvestmentConfigMapper.Map(Dto("Actual/360"), "D")!.Templates[0].AccrualBasis.Should().Be(AccrualBasis.Actual360);
        ReinvestmentConfigMapper.Map(Dto(null), "D")!.Templates[0].AccrualBasis.Should().Be(AccrualBasis.Thirty360);
        FluentActions.Invoking(() => ReinvestmentConfigMapper.Map(Dto("bogus"), "D")).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Actual/360", AccrualBasis.Actual360)]
    [InlineData("ACT/360", AccrualBasis.Actual360)]
    [InlineData("actual360", AccrualBasis.Actual360)]
    [InlineData("30/360", AccrualBasis.Thirty360)]
    [InlineData(null, AccrualBasis.Thirty360)]
    [InlineData("  ", AccrualBasis.Thirty360)]
    public void Spellings_parse(string? s, AccrualBasis expected) =>
        AccrualBasisParser.Parse(s).Should().Be(expected);
}
