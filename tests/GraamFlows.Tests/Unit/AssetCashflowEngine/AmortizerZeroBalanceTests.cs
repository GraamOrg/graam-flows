using FluentAssertions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using GraamFlows.Assumptions;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// Regression: an asset supplied with no debtService AND a zero
/// OriginalBalance must not capitalize interest into principal.
///
/// The level-pay branch keyed the payment on OriginalBalance
/// (Amortizer.cs), so OriginalBalance=0 → AmortizingPayment(0,…)=0 →
/// the period payment fell below the accrued interest → scheduled
/// principal went NEGATIVE and the balance GREW every period (negative
/// amortization). Harmony's tape-asset builder ships current-factors
/// assets with OriginalBalance=0 and no debtService, which tripped this
/// (graam-harmony abxsdeal: pool balance grew 1.025B → 1.031B → …).
///
/// The fix amortizes off the current balance when no original balance is
/// supplied, plus a backstop clamping scheduled principal at zero.
/// </summary>
public class AmortizerZeroBalanceTests
{
    private static Asset ZeroOrigBalanceAsset(double currentBalance, double ratePct, int term)
    {
        return new Asset
        {
            AssetName = "ZEROORIG",
            AssetId = "ZEROORIG",
            InterestRateType = InterestRateType.FRM,
            OriginalDate = new DateTime(2026, 5, 1),
            OriginalBalance = 0.0,          // <-- the misconfiguration
            CurrentBalance = currentBalance,
            BalanceAtIssuance = currentBalance,
            OriginalInterestRate = ratePct,
            CurrentInterestRate = ratePct,
            OriginalAmortizationTerm = term,
            ServiceFee = 0.0,
            GroupNum = "1",
            IsIO = false,
        };
    }

    private static IAssetAssumptions ZeroAssumps(int anchorAbsT)
    {
        // CPR=0, CDR=0, severity=0 — isolate amortization.
        return new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, 0.0),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, 0.0),
            new ConstVector(anchorAbsT, 0.0));
    }

    [Fact]
    public void ZeroOriginalBalance_NoDebtService_DoesNotCapitalizeInterest()
    {
        var firstProjDate = new DateTime(2026, 6, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);
        IRateProvider rateProvider = null!;

        var asset = ZeroOrigBalanceAsset(currentBalance: 1_000_000, ratePct: 6.84, term: 360);

        var result = CfCore.GenerateAssetCashflows(
            new List<IAsset> { asset },
            firstProjDate, null, _ => ZeroAssumps(anchorAbsT), rateProvider);

        var periods = result.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();
        periods.Should().NotBeEmpty();

        // 1) Scheduled principal is never negative (no interest capitalization).
        foreach (var cf in periods)
            cf.ScheduledPrincipal.Should().BeGreaterThanOrEqualTo(-0.01,
                $"period {cf.CashflowDate:yyyy-MM} scheduled principal must not be negative " +
                "(negative = interest capitalized into principal)");

        // 2) The pool balance is monotonically non-increasing — it pays down,
        //    it does not grow.
        for (var i = 1; i < periods.Count; i++)
            periods[i].Balance.Should().BeLessThanOrEqualTo(periods[i - 1].Balance + 0.01,
                $"balance must not grow at period {periods[i].CashflowDate:yyyy-MM} " +
                $"({periods[i - 1].Balance:N0} -> {periods[i].Balance:N0})");

        // 3) Some principal is actually amortized over the life (sanity: the
        //    loan is not interest-only by accident).
        periods.Sum(p => p.ScheduledPrincipal).Should().BeGreaterThan(0,
            "a fully-amortizing loan should return principal over its life");
    }
}
