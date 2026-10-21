using FluentAssertions;
using GraamFlows.AssetCashflowEngine;
using GraamFlows.Assumptions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// A balloon loan: a 10-year commercial mortgage, interest-only for its first 5 years, then
/// amortizing on a 30-year schedule, with the remaining balance repaid at maturity. Without
/// <see cref="IAsset.BalloonAmortizationTerm" /> the engine re-amortizes over the 60 months left
/// to maturity when the IO period ends and the loan fully amortizes — no balloon at all.
/// </summary>
public class BalloonAmortizationTests
{
    private const double Balance = 10_000_000.0;
    private const double RatePct = 4.8;
    private const int TermMonths = 120;
    private const int IoMonths = 60;
    private const int AmortMonths = 360;

    private static Asset Loan(int? balloonAmortTerm) => new()
    {
        AssetName = "CRE",
        AssetId = "CRE",
        InterestRateType = InterestRateType.FRM,
        OriginalDate = new DateTime(2026, 6, 1),
        OriginalBalance = Balance,
        CurrentBalance = Balance,
        BalanceAtIssuance = Balance,
        OriginalInterestRate = RatePct,
        CurrentInterestRate = RatePct,
        OriginalAmortizationTerm = TermMonths,
        ServiceFee = 0.0,
        GroupNum = "1",
        IsIO = true,
        IOTerm = IoMonths,
        BalloonAmortizationTerm = balloonAmortTerm
    };

    private static List<PeriodCashflows> Run(Asset asset)
    {
        var firstProjDate = new DateTime(2026, 6, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);
        IAssetAssumptions NoLoss(IAsset _) => new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, 0.0),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, 0.0),
            new ConstVector(anchorAbsT, 0.0));
        var result = CfCore.GenerateAssetCashflows(
            new List<IAsset> { asset }, firstProjDate, null, NoLoss, null!);
        return result.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();
    }

    private static double LevelPayment(double balance, double ratePct, int n)
    {
        var r = ratePct / 1200.0;
        return balance * r / (1 - Math.Pow(1 + r, -n));
    }

    [Fact]
    public void AfterTheIoPeriod_ItAmortizesOnItsOwnSchedule_AndBalloonsAtMaturity()
    {
        var periods = Run(Loan(AmortMonths));
        var principal = periods.Select(p => p.ScheduledPrincipal).ToList();

        principal.Take(IoMonths).Should().OnlyContain(p => Math.Abs(p) < 0.01, "no principal while IO");

        // The first amortizing payment is the 30-year level payment on the full balance.
        var pmt = LevelPayment(Balance, RatePct, AmortMonths);
        var firstAmortizing = periods[IoMonths];
        (firstAmortizing.ScheduledPrincipal + firstAmortizing.Interest).Should().BeApproximately(pmt, 1.0);

        // The balloon is what 59 level payments on a 30-year schedule leave unpaid going into the
        // 120th, which repays it: B·(1+r)^59 − pmt·((1+r)^59 − 1)/r, plus that month's principal.
        var r = RatePct / 1200.0;
        var owedAtMaturity = Balance * Math.Pow(1 + r, 59) - pmt * (Math.Pow(1 + r, 59) - 1) / r;
        var balloonRow = periods.Last(p => p.ScheduledPrincipal > 0.01);
        balloonRow.ScheduledPrincipal.Should().BeApproximately(owedAtMaturity, 5.0);
        owedAtMaturity.Should().BeGreaterThan(Balance * 0.9, "a 30-year schedule pays down < 10% in 5 years");
        principal.Sum().Should().BeApproximately(Balance, 1.0);
        periods.IndexOf(balloonRow).Should().Be(TermMonths - 1, "the balloon repays at maturity");
    }

    [Fact]
    public void WithoutIt_TheIoLoanStillFullyAmortizesOverTheMonthsLeft()
    {
        var periods = Run(Loan(null));
        var pmt = LevelPayment(Balance, RatePct, TermMonths - IoMonths);
        var firstAmortizing = periods[IoMonths];
        (firstAmortizing.ScheduledPrincipal + firstAmortizing.Interest).Should().BeApproximately(pmt, 1.0);
        periods.Max(p => p.ScheduledPrincipal).Should().BeLessThan(Balance * 0.05, "no balloon");
    }
}
