using FluentAssertions;
using GraamFlows.AssetCashflowEngine;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// Coverage for the period-length parameterization added for non-monthly-pay
/// deals (graam-flows#46): the amortizer accrues over <c>monthsPerPeriod</c>
/// months per projection period (12 / payment frequency). At the default of 1
/// the monthly path is byte-for-byte unchanged; the rest of the suite is the
/// no-op regression. Here the kernel is driven directly (CfCore still builds a
/// monthly axis) using an interest-only asset — flat balance, interest-only —
/// so the per-period coupon is isolated cleanly.
/// </summary>
public class MonthsPerPeriodTests
{
    private const double Balance = 1_000_000.0;
    private const double RatePct = 8.0;

    private static AssetDataArrays IoAssetData(DateTime firstProjDate)
    {
        var asset = new Asset
        {
            AssetName = "IOONLY",
            AssetId = "IOONLY",
            InterestRateType = InterestRateType.FRM,
            OriginalDate = firstProjDate,      // new origination => seasoning starts at 0
            OriginalBalance = Balance,
            CurrentBalance = Balance,
            BalanceAtIssuance = Balance,
            OriginalInterestRate = RatePct,
            CurrentInterestRate = RatePct,
            OriginalAmortizationTerm = 360,
            ServiceFee = 0.0,
            GroupNum = "1",
            IsIO = true,
            IOTerm = 600                        // interest-only across the whole window
        };
        return new AssetDataArrays(new List<IAsset> { asset });
    }

    private static double[][] Zeros(int periods) => new[] { new double[periods] };

    private static CashflowResultArrays RunIo(int monthsPerPeriod, int periods)
    {
        var firstProjDate = new DateTime(2026, 6, 1);
        var startTime = DateUtil.CalcAbsT(firstProjDate);
        var endTime = startTime + periods - 1;

        return Amortizer.GenerateCashflows(
            IoAssetData(firstProjDate),
            startTime,
            endTime,
            smmTime: Zeros(periods),
            mdrTime: Zeros(periods),
            sevTime: Zeros(periods),
            delTime: Zeros(periods),
            delAdvIntTime: Zeros(periods),
            delAdvPrinTime: Zeros(periods),
            forbRecovPpayTime: Zeros(periods),
            forbRecovMaturityTime: Zeros(periods),
            forbRecovDefaultTime: Zeros(periods),
            allMarketRates: new double[1][],
            monthsPerPeriod: monthsPerPeriod);
    }

    [Fact]
    public void Quarterly_AccruesThreeMonthsOfCouponPerPeriod()
    {
        var r = RunIo(monthsPerPeriod: 3, periods: 20);

        // Quarterly coupon = balance * 8% * 3/12 = 20,000.
        const double quarterlyInterest = Balance * RatePct / 1200.0 * 3;
        quarterlyInterest.Should().Be(20_000.0);

        for (var p = 0; p < 10; p++)
        {
            r.BeginBalance[p].Should().BeApproximately(Balance, 0.01);
            r.Interest[p].Should().BeApproximately(quarterlyInterest, 0.01);
            r.ScheduledPrincipal[p].Should().BeApproximately(0.0, 0.01);
        }

        // Principal (the balance) comes back once at the end of the window; par is preserved.
        var totalPrincipal = r.ScheduledPrincipal.Sum() + r.UnscheduledPrincipal.Sum();
        totalPrincipal.Should().BeApproximately(Balance, 1.0);
    }

    [Fact]
    public void MonthlyDefault_IsTheMonthlyCoupon_NoOp()
    {
        // Same asset at monthsPerPeriod = 1 accrues one month of coupon:
        // 1,000,000 * 8% / 12 = 6,666.67. Demonstrates the divisor reduces to
        // the monthly path exactly.
        var r = RunIo(monthsPerPeriod: 1, periods: 24);

        const double monthlyInterest = Balance * RatePct / 1200.0;
        r.Interest[0].Should().BeApproximately(monthlyInterest, 0.01);
        r.BeginBalance[0].Should().BeApproximately(Balance, 0.01);
        r.ScheduledPrincipal[0].Should().BeApproximately(0.0, 0.01);

        var totalPrincipal = r.ScheduledPrincipal.Sum() + r.UnscheduledPrincipal.Sum();
        totalPrincipal.Should().BeApproximately(Balance, 1.0);
    }
}
