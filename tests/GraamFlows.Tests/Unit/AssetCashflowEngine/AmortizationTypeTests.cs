using FluentAssertions;
using GraamFlows.Assumptions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// Coverage for the non-amortizing repayment styles added for revolving /
/// reinvesting-pool support (graam-flows#47):
///   * Bullet — no scheduled principal until maturity, full balloon at maturity.
///   * PIK    — the coupon capitalizes into the balance (no cash interest) and
///              principal repays at maturity.
/// Exercised through CfCore (the monthly integration path). Loss and prepay
/// assumptions are zeroed so the tests isolate the amortization mechanics.
/// </summary>
public class AmortizationTypeTests
{
    private const double Balance = 1_000_000.0;
    private const double RatePct = 6.0;      // 6% annual → 0.5%/month
    private const int TermMonths = 60;       // 5-year term

    private static Asset TermLoan(AmortizationType amortType)
    {
        var origination = new DateTime(2026, 6, 1);
        return new Asset
        {
            AssetName = "TERMLOAN",
            AssetId = "TERMLOAN",
            InterestRateType = InterestRateType.FRM,
            AmortizationType = amortType,
            OriginalDate = origination,
            OriginalBalance = Balance,
            CurrentBalance = Balance,
            BalanceAtIssuance = Balance,
            OriginalInterestRate = RatePct,
            CurrentInterestRate = RatePct,
            OriginalAmortizationTerm = TermMonths,
            ServiceFee = 0.0,               // isolate coupon: netInterest == interest
            GroupNum = "1",
            IsIO = false
        };
    }

    private static IAssetAssumptions NoLossAssumps(int anchorAbsT)
    {
        // CPR=0, CDR=0, severity=0 — isolate amortization from prepay/default.
        return new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, 0.0),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, 0.0),
            new ConstVector(anchorAbsT, 0.0));
    }

    private static List<PeriodCashflows> Run(Asset asset)
    {
        var firstProjDate = new DateTime(2026, 6, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);
        IRateProvider rateProvider = null!;

        var result = CfCore.GenerateAssetCashflows(
            new List<IAsset> { asset },
            firstProjDate, null, _ => NoLossAssumps(anchorAbsT), rateProvider);

        return result.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();
    }

    [Fact]
    public void Bullet_PaysInterestOnly_ThenBalloonsPrincipalAtMaturity()
    {
        var periods = Run(TermLoan(AmortizationType.Bullet));
        periods.Should().NotBeEmpty();

        // Pre-maturity: balance holds flat, interest is the full coupon, and
        // there is no scheduled principal.
        foreach (var cf in periods.Take(40))
        {
            cf.Balance.Should().BeApproximately(Balance, 0.01);
            cf.ScheduledPrincipal.Should().BeApproximately(0.0, 0.01);
            cf.Interest.Should().BeApproximately(Balance * RatePct / 1200.0, 0.01); // 5,000/mo
        }

        // The entire principal comes back exactly once (balloon), and it sums to par.
        var totalPrincipal = periods.Sum(p => p.ScheduledPrincipal + p.UnscheduledPrincipal);
        totalPrincipal.Should().BeApproximately(Balance, 1.0);

        // The balloon lands near the 60-month maturity, not early.
        var balloon = periods.First(p => p.ScheduledPrincipal + p.UnscheduledPrincipal > 1.0);
        var balloonMonth = (balloon.CashflowDate.Year - 2026) * 12 + balloon.CashflowDate.Month - 6;
        balloonMonth.Should().BeInRange(58, 61);
    }

    [Fact]
    public void Pik_CapitalizesCoupon_NoCashInterest_BalanceCompounds()
    {
        var periods = Run(TermLoan(AmortizationType.Pik));
        periods.Should().NotBeEmpty();

        var monthlyRate = RatePct / 1200.0; // 0.005

        // No cash interest is ever paid while PIK-ing.
        foreach (var cf in periods.Take(40))
            cf.Interest.Should().BeApproximately(0.0, 0.01);

        // The balance compounds by the coupon each period.
        periods[0].Balance.Should().BeApproximately(Balance * (1 + monthlyRate), 0.01); // 1,005,000
        periods[1].Balance.Should().BeApproximately(Balance * Math.Pow(1 + monthlyRate, 2), 0.01);

        // Monotonic growth up to maturity.
        for (var i = 1; i < 40; i++)
            periods[i].Balance.Should().BeGreaterThan(periods[i - 1].Balance);

        // Principal returned equals the fully compounded balance (all capitalized
        // interest is repaid as principal). No cash interest anywhere.
        var totalPrincipal = periods.Sum(p => p.ScheduledPrincipal + p.UnscheduledPrincipal);
        var expectedCompounded = Balance * Math.Pow(1 + monthlyRate, TermMonths);
        totalPrincipal.Should().BeApproximately(expectedCompounded, expectedCompounded * 0.01);
        periods.Sum(p => p.Interest).Should().BeApproximately(0.0, 0.01);
    }
}
