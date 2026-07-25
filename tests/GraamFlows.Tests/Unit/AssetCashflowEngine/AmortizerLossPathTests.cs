using FluentAssertions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using GraamFlows.Assumptions;
using Xunit;
using Xunit.Abstractions;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// Benchmarks the collateral amortizer's LOSS PATH against the reference
/// cashflow calc standard (graam-harmony #3449).
///
/// The reference standard pays scheduled principal FIRST, then assesses default
/// and prepay IN PARALLEL off the same base — the balance remaining after
/// scheduled principal (bal_post = bal_prev - sched_p):
///
///   bal_post    = bal_prev - sched_p
///   default_bal = bal_post * mdr
///   unsched_p   = bal_post * smm          (same base, parallel to default)
///   recovery    = default_bal * (1 - sev)
///   bal_new     = bal_prev - sched_p - default_bal - unsched_p
///
/// These tests drive the real engine (CfCore -> Amortizer) with direct monthly
/// hazards (SMM/MDR, so the per-period hazard equals input/100) and pin every
/// period to those identities. The no-loss case already ties penny-for-penny;
/// this is the loss-path regime the ORIGMDR default basis was added for.
/// </summary>
public class AmortizerLossPathTests
{
    private readonly ITestOutputHelper _output;

    public AmortizerLossPathTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Direct monthly hazards used across the loss-path cases.
    private const double SmmPct = 1.0;   // -> smm = 0.01 / month
    private const double MdrPct = 0.5;   // -> mdr = 0.005 / month
    private const double SevPct = 40.0;  // -> severity = 0.40

    private const double Smm = SmmPct / 100.0;
    private const double Mdr = MdrPct / 100.0;
    private const double Sev = SevPct / 100.0;

    private static Asset FrmAsset(double balance, double ratePct, int term)
    {
        return new Asset
        {
            AssetName = "LOSSPATH",
            AssetId = "LOSSPATH",
            InterestRateType = InterestRateType.FRM,
            OriginalDate = new DateTime(2026, 6, 1),
            OriginalBalance = balance,
            CurrentBalance = balance,
            BalanceAtIssuance = balance,
            OriginalInterestRate = ratePct,
            CurrentInterestRate = ratePct,
            OriginalAmortizationTerm = term,
            ServiceFee = 0.0,
            GroupNum = "1",
            IsIO = false,
        };
    }

    /// <summary>
    /// SMM prepay + MDR default + severity, no delinquency. Each period's default,
    /// prepay, recovery and ending balance must satisfy the reference standard
    /// identities (default &amp; prepay both off bal_prev - sched_p).
    /// </summary>
    private static IAssetAssumptions LossAssumps(int anchorAbsT)
    {
        return new AssetAssumptions(
            PrepaymentTypeEnum.SMM, new ConstVector(anchorAbsT, SmmPct),
            DefaultTypeEnum.MDR, new ConstVector(anchorAbsT, MdrPct),
            new ConstVector(anchorAbsT, SevPct));
    }

    private static IAssetAssumptions NoLossAssumps(int anchorAbsT)
    {
        return new AssetAssumptions(
            PrepaymentTypeEnum.SMM, new ConstVector(anchorAbsT, 0.0),
            DefaultTypeEnum.MDR, new ConstVector(anchorAbsT, 0.0),
            new ConstVector(anchorAbsT, 0.0));
    }

    [Fact]
    public void LossPath_DefaultAndPrepay_AssessedOffBalanceAfterScheduledPrincipal()
    {
        var firstProjDate = new DateTime(2026, 7, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);
        IRateProvider rateProvider = null!;

        var asset = FrmAsset(balance: 1_000_000, ratePct: 6.0, term: 360);

        var result = CfCore.GenerateAssetCashflows(
            new List<IAsset> { asset },
            firstProjDate, null, _ => LossAssumps(anchorAbsT), rateProvider);

        var periods = result.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();
        periods.Should().NotBeEmpty();

        _output.WriteLine("Period\tBegin\tSched\tDefault\tPrepay\tRecov\tEnd");

        // Test the healthy, mid-life periods: begin balance well above the
        // near-maturity cleanup threshold and not the final horizon period
        // (which truncates any residual as a prepay spike).
        var tested = 0;
        for (var t = 0; t < periods.Count - 1; t++)
        {
            var cf = periods[t];
            if (cf.BeginBalance < 5_000) break;

            var schedP = cf.ScheduledPrincipal;
            var balPost = cf.BeginBalance - schedP;

            var expectedDefault = balPost * Mdr;
            var expectedPrepay = balPost * Smm;
            var expectedRecovery = expectedDefault * (1 - Sev);
            var expectedEnd = cf.BeginBalance - schedP - expectedDefault - expectedPrepay;

            // Tolerance scales with balance; the identities are exact up to
            // floating-point noise.
            var tol = Math.Max(0.01, 1e-7 * cf.BeginBalance);

            cf.DefaultedPrincipal.Should().BeApproximately(expectedDefault, tol,
                $"period {t}: default = mdr * (begin - sched)");
            cf.UnscheduledPrincipal.Should().BeApproximately(expectedPrepay, tol,
                $"period {t}: prepay = smm * (begin - sched), parallel to default");
            cf.RecoveryPrincipal.Should().BeApproximately(expectedRecovery, tol,
                $"period {t}: recovery = default * (1 - sev)");
            cf.Balance.Should().BeApproximately(expectedEnd, tol,
                $"period {t}: end = begin - sched - default - prepay");

            if (tested < 6)
                _output.WriteLine(
                    $"{t}\t{cf.BeginBalance:N0}\t{schedP:N0}\t{cf.DefaultedPrincipal:N0}\t" +
                    $"{cf.UnscheduledPrincipal:N0}\t{cf.RecoveryPrincipal:N0}\t{cf.Balance:N0}");

            tested++;
        }

        tested.Should().BeGreaterThan(24, "several years of healthy periods should be validated");
    }

    [Fact]
    public void LossPath_ScheduledPrincipal_PaidInFull_NotHaircutByMdr()
    {
        // The standard pays scheduled principal in FULL; the previous engine
        // convention haircut it by (1 - mdr). With no delinquency the reported
        // scheduled principal must equal the contractual amortization, so the
        // begin-balance rolldown is exactly sched + default + prepay.
        var firstProjDate = new DateTime(2026, 7, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);
        IRateProvider rateProvider = null!;

        var asset = FrmAsset(balance: 1_000_000, ratePct: 6.0, term: 360);

        var result = CfCore.GenerateAssetCashflows(
            new List<IAsset> { asset },
            firstProjDate, null, _ => LossAssumps(anchorAbsT), rateProvider);

        var periods = result.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();

        for (var t = 0; t < periods.Count - 1; t++)
        {
            var cf = periods[t];
            if (cf.BeginBalance < 5_000) break;

            // Conservation with scheduled principal paid in full.
            var rolldown = cf.ScheduledPrincipal + cf.DefaultedPrincipal + cf.UnscheduledPrincipal;
            (cf.BeginBalance - cf.Balance).Should().BeApproximately(rolldown, Math.Max(0.01, 1e-7 * cf.BeginBalance),
                $"period {t}: begin - end = sched + default + prepay");
        }
    }

    [Fact]
    public void NoLoss_TiesOut_BalanceRolldownEqualsScheduledPrincipal()
    {
        // Regression guard: with zero hazards the loss-path rework must not
        // perturb the no-loss case — begin - end == scheduled principal every
        // period, and no defaults/prepays are emitted.
        var firstProjDate = new DateTime(2026, 7, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);
        IRateProvider rateProvider = null!;

        var asset = FrmAsset(balance: 1_000_000, ratePct: 6.0, term: 360);

        var result = CfCore.GenerateAssetCashflows(
            new List<IAsset> { asset },
            firstProjDate, null, _ => NoLossAssumps(anchorAbsT), rateProvider);

        var periods = result.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();
        periods.Should().NotBeEmpty();

        for (var t = 0; t < periods.Count - 1; t++)
        {
            var cf = periods[t];
            if (cf.BeginBalance < 5_000) break;

            cf.DefaultedPrincipal.Should().BeApproximately(0, 0.01, $"period {t}: no defaults with mdr=0");
            cf.UnscheduledPrincipal.Should().BeApproximately(0, 0.01, $"period {t}: no prepays with smm=0");
            (cf.BeginBalance - cf.Balance).Should().BeApproximately(cf.ScheduledPrincipal,
                Math.Max(0.01, 1e-7 * cf.BeginBalance),
                $"period {t}: begin - end = scheduled principal");
        }
    }
}
