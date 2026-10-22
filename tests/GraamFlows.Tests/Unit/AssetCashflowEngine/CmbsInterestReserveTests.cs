using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.AssetCashflowEngine;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
///     An Actual/360 loan's servicing fee basis, and a CMBS trust's interest reserve.
///
///     A CMBS pass-through rate is set on each loan's Net Mortgage Rate: the coupon less the
///     administrative fee rate, both accruing Actual/360, restated as the 30/360 rate that pays the
///     same month's interest. The fee was taken at 1/12 a month while the coupon earned days/360,
///     so the net of a 31-day month was overstated by fee / 30 (GSMS 2017-GS8's class C: about
///     $32 a month on $56MM). And the trust withholds a day's net interest from the January
///     (except in a leap year) and February payments into its interest reserve and pays it out in
///     March, so the net interest of those three months is a 30-day month's; without the reserve
///     the engine paid C 31 / 30 of its coupon in January and February and 28 / 30 in March.
/// </summary>
public class CmbsInterestReserveTests
{
    private const double Face = 50_000_000.0;
    private const double Coupon = 4.5;
    private const double Fee = 0.02;

    private static AssetDto Loan(string? dayCount = "Actual/360", DateTime? originalDate = null, int term = 120) => new()
    {
        AssetName = "loan", AssetId = "loan", InterestRateType = "FRM",
        OriginalDate = originalDate ?? new DateTime(2026, 10, 10), OriginalBalance = Face, CurrentBalance = Face,
        OriginalInterestRate = Coupon, CurrentInterestRate = Coupon, OriginalAmortizationTerm = term,
        ServiceFee = Fee, GroupNum = "1", IsIO = true, IOTerm = term, DayCount = dayCount
    };

    private static List<PeriodCashflowDto> Run(DateTime projection, bool reserve, params AssetDto[] loans)
    {
        var result = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(
            new CalcCollateralRequest
            {
                Assets = loans.ToList(),
                ProjectionDate = projection,
                InterestReserve = reserve,
                Assumptions = new AssumptionsDto { Cpr = 0, Cdr = 0, Severity = 0 }
            });
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value.Should().BeOfType<CalcCollateralResponse>().Subject
            .Cashflows.OrderBy(c => c.Period).ToList();
    }

    // The days a period dated d accrues: the calendar month before it.
    private static int Days(DateTime d)
    {
        var start = d.AddMonths(-1);
        return DateTime.DaysInMonth(start.Year, start.Month);
    }

    private static double Net(double days) => Face * (Coupon - Fee) / 100 * days / 360;

    [Fact]
    public void An_actual_360_loans_servicing_fee_accrues_the_actual_days_like_its_coupon()
    {
        var rows = Run(new DateTime(2026, 11, 10), false, Loan());
        foreach (var r in rows.Take(14))
        {
            var days = Days(r.CashflowDate);
            r.ServiceFee.Should().BeApproximately(Face * Fee / 100 * days / 360, 1e-6, $"{r.CashflowDate:yyyy-MM}");
            r.NetInterest.Should().BeApproximately(Net(days), 1e-6,
                "the net rate is (coupon - fee) on the actual days, so its 30/360 equivalent is (coupon - fee) x days / 30");
        }
    }

    [Fact]
    public void A_30_360_loans_servicing_fee_is_unchanged()
    {
        var rows = Run(new DateTime(2026, 11, 10), true, Loan(dayCount: null));
        rows.Take(14).Should().OnlyContain(r =>
            Math.Abs(r.ServiceFee - Face * Fee / 1200) < 1e-6 && Math.Abs(r.NetInterest - Face * (Coupon - Fee) / 1200) < 1e-6,
            "a 30/360 loan pays a 30-day month every month, and holds no reserve");
    }

    [Fact]
    public void The_reserve_pays_a_30_day_month_of_net_interest_in_January_February_and_March()
    {
        var with = Run(new DateTime(2026, 11, 10), true, Loan());
        var without = Run(new DateTime(2026, 11, 10), false, Loan());

        foreach (var r in with.Take(14))
        {
            var quarter = r.CashflowDate.Month is 1 or 2 or 3;
            r.NetInterest.Should().BeApproximately(quarter ? Net(30) : Net(Days(r.CashflowDate)), 1e-6,
                $"{r.CashflowDate:yyyy-MM}");
            r.ServiceFee.Should().BeApproximately(Face * Fee / 100 * Days(r.CashflowDate) / 360, 1e-6,
                "the servicer is paid in full; only the certificates' interest is withheld");
            r.Interest.Should().BeApproximately(r.NetInterest + r.ServiceFee, 1e-6);
        }

        with.Sum(r => r.NetInterest).Should().BeApproximately(without.Sum(r => r.NetInterest), 1e-4,
            "the reserve moves interest between months, it does not keep any");
    }

    [Fact]
    public void In_a_leap_year_only_February_withholds_and_March_pays_the_one_day_back()
    {
        var rows = Run(new DateTime(2027, 11, 10), true, Loan(originalDate: new DateTime(2027, 10, 10)));
        var jan = rows.Single(r => r.CashflowDate == new DateTime(2028, 1, 10));
        var feb = rows.Single(r => r.CashflowDate == new DateTime(2028, 2, 10));
        var mar = rows.Single(r => r.CashflowDate == new DateTime(2028, 3, 10));

        jan.NetInterest.Should().BeApproximately(Net(31), 1e-6, "January 2028 pays December's 31 days in full");
        feb.NetInterest.Should().BeApproximately(Net(30), 1e-6);
        mar.NetInterest.Should().BeApproximately(Net(30), 1e-6, "February 2028 has 29 days, plus the day withheld");
    }

    [Fact]
    public void Without_the_reserve_the_actual_days_are_paid_every_month()
    {
        var rows = Run(new DateTime(2026, 11, 10), false, Loan());
        rows.Single(r => r.CashflowDate == new DateTime(2027, 1, 10)).NetInterest.Should().BeApproximately(Net(31), 1e-6);
        rows.Single(r => r.CashflowDate == new DateTime(2027, 3, 10)).NetInterest.Should().BeApproximately(Net(28), 1e-6);
    }

    [Fact]
    public void A_loan_repaid_before_March_has_its_withheld_days_paid_out_with_the_pools_March()
    {
        // Matures February 2027; the other loan runs on.
        var early = Loan(originalDate: new DateTime(2022, 2, 10), term: 60);
        early.AssetId = early.AssetName = "early";
        var rows = Run(new DateTime(2026, 11, 10), true, early, Loan());

        var feb = rows.Single(r => r.CashflowDate == new DateTime(2027, 2, 10));
        var mar = rows.Single(r => r.CashflowDate == new DateTime(2027, 3, 10));
        feb.ScheduledPrincipal.Should().BeApproximately(Face, 1e-6, "the early loan repays in February");
        feb.NetInterest.Should().BeApproximately(2 * Net(30), 1e-6);
        mar.NetInterest.Should().BeApproximately(Net(28) + Net(1) * 2 + Net(1) * 2, 1e-6,
            "March pays the running loan's 28 days plus both loans' two withheld days");
    }

    [Fact]
    public void When_the_pool_ends_before_March_its_last_period_pays_the_reserve_out()
    {
        var with = Run(new DateTime(2026, 11, 10), true, Loan(originalDate: new DateTime(2022, 2, 10), term: 60));
        var without = Run(new DateTime(2026, 11, 10), false, Loan(originalDate: new DateTime(2022, 2, 10), term: 60));

        with.Last().CashflowDate.Should().Be(new DateTime(2027, 2, 10));
        with.Last().NetInterest.Should().BeApproximately(Net(31) + Net(1), 1e-6,
            "the final distribution pays January's day back with February's");
        with.Sum(r => r.NetInterest).Should().BeApproximately(without.Sum(r => r.NetInterest), 1e-6);
    }

    [Theory]
    [InlineData(2027, 1, true)]
    [InlineData(2027, 2, true)]
    [InlineData(2028, 1, false)]
    [InlineData(2028, 2, true)]
    [InlineData(2027, 3, false)]
    [InlineData(2027, 12, false)]
    public void The_withholding_months(int year, int month, bool withholds) =>
        Amortizer.WithholdsInterestReserve(new DateTime(year, month, 10)).Should().Be(withholds);
}
