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
/// Benchmarks the collateral amortizer's LOSS PATH against the byte-validated
/// reference-engine oracle (graam-harmony #3449).
///
/// Per the oracle, each period:
///
///   default_bal = mdr * bal_prev                 (default on the BEGIN balance)
///   sched_paid  = sched_p * (1 - mdr)            (scheduled principal haircut by mdr)
///   unsched_p   = smm * (bal_prev - sched_p)     (prepay PARALLEL to default,
///                                                 default NOT subtracted)
///   recovery    = default_bal * (1 - sev)
///   bal_new     = bal_prev - sched_paid - default_bal - unsched_p
///
/// These tests drive the real engine (CfCore -> Amortizer) with direct monthly
/// hazards (SMM/MDR, so the per-period hazard equals input/100) and pin every
/// period to those identities. The no-loss case ties penny-for-penny.
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
    public void LossPath_DefaultOnBeginBalance_PrepayParallel_MatchesOracle()
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

        _output.WriteLine("Period\tBegin\tSchedRpt\tDefault\tPrepay\tRecov\tEnd");

        // Test the healthy, mid-life periods: begin balance well above the
        // near-maturity cleanup threshold and not the final horizon period
        // (which truncates any residual as a prepay spike).
        var tested = 0;
        for (var t = 0; t < periods.Count - 1; t++)
        {
            var cf = periods[t];
            if (cf.BeginBalance < 5_000) break;

            var tol = Math.Max(0.01, 1e-7 * cf.BeginBalance);

            // Default is assessed on the BEGIN performing balance.
            var expectedDefault = cf.BeginBalance * Mdr;
            cf.DefaultedPrincipal.Should().BeApproximately(expectedDefault, tol,
                $"period {t}: default = mdr * begin (oracle base)");

            // Reported scheduled principal is haircut by (1 - mdr); recover the
            // full contractual scheduled amount to check the prepay base.
            var fullSched = cf.ScheduledPrincipal / (1 - Mdr);

            // Prepay runs in PARALLEL with default off the balance after (full)
            // scheduled principal — the period's default is NOT subtracted.
            var expectedPrepay = (cf.BeginBalance - fullSched) * Smm;
            cf.UnscheduledPrincipal.Should().BeApproximately(expectedPrepay, tol,
                $"period {t}: prepay = smm * (begin - sched), default NOT subtracted");

            cf.RecoveryPrincipal.Should().BeApproximately(expectedDefault * (1 - Sev), tol,
                $"period {t}: recovery = default * (1 - sev)");

            // Conservation: begin - end = reported scheduled + default + prepay.
            var rolldown = cf.ScheduledPrincipal + cf.DefaultedPrincipal + cf.UnscheduledPrincipal;
            (cf.BeginBalance - cf.Balance).Should().BeApproximately(rolldown, tol,
                $"period {t}: begin - end = scheduled(reported) + default + prepay");

            if (tested < 6)
                _output.WriteLine(
                    $"{t}\t{cf.BeginBalance:N0}\t{cf.ScheduledPrincipal:N0}\t{cf.DefaultedPrincipal:N0}\t" +
                    $"{cf.UnscheduledPrincipal:N0}\t{cf.RecoveryPrincipal:N0}\t{cf.Balance:N0}");

            tested++;
        }

        tested.Should().BeGreaterThan(24, "several years of healthy periods should be validated");
    }

    [Fact]
    public void LossPath_ScheduledPrincipal_HaircutByMdr()
    {
        // The oracle haircuts scheduled principal by (1 - mdr): the defaulted
        // fraction of the loan does not also pay its scheduled principal. Run the
        // same asset with and without default and confirm the reported scheduled
        // principal scales by (1 - mdr) each period (no delinquency).
        var firstProjDate = new DateTime(2026, 7, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);
        IRateProvider rateProvider = null!;

        List<PeriodCashflows> RunP(IAssetAssumptions a) => CfCore.GenerateAssetCashflows(
                new List<IAsset> { FrmAsset(1_000_000, 6.0, 360) },
                firstProjDate, null, _ => a, rateProvider)
            .PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();

        // Prepay off in both so the scheduled schedule is identical period-0.
        var noDefault = RunP(new AssetAssumptions(
            PrepaymentTypeEnum.SMM, new ConstVector(anchorAbsT, 0.0),
            DefaultTypeEnum.MDR, new ConstVector(anchorAbsT, 0.0),
            new ConstVector(anchorAbsT, SevPct)));
        var withDefault = RunP(new AssetAssumptions(
            PrepaymentTypeEnum.SMM, new ConstVector(anchorAbsT, 0.0),
            DefaultTypeEnum.MDR, new ConstVector(anchorAbsT, MdrPct),
            new ConstVector(anchorAbsT, SevPct)));

        // Period 0: same begin balance, so reported scheduled is haircut by (1-mdr).
        withDefault[0].ScheduledPrincipal.Should().BeApproximately(
            noDefault[0].ScheduledPrincipal * (1 - Mdr), 0.01,
            "scheduled principal is haircut by (1 - mdr)");
    }

    /// <summary>
    /// Recovery lag (graam-harmony #3449 divergence #3): with a lag of L months,
    /// the recovery on a period-t default lands at period t + L. Compared against a
    /// zero-lag run of the identical asset, only the recovery timing shifts — every
    /// other series (default, prepay, interest, ending balance) is byte-identical,
    /// and the recovery curve is the zero-lag curve shifted forward by L.
    /// </summary>
    [Fact]
    public void RecoveryLag_ShiftsRecoveryForward_LeavingEverythingElseUnchanged()
    {
        const int lag = 3;
        var noLag = RunPeriods(recoveryLag: 0);
        var lagged = RunPeriods(recoveryLag: lag);

        noLag.Count.Should().BeGreaterThan(lag + 12);
        lagged.Count.Should().BeGreaterThanOrEqualTo(noLag.Count,
            "the lag pushes the last recovery beyond the no-lag horizon");

        // The first `lag` periods carry no recovery — nothing has been liquidated
        // yet — even though defaults start in period 0.
        for (var t = 0; t < lag; t++)
        {
            lagged[t].DefaultedPrincipal.Should().BeGreaterThan(0, $"period {t}: defaults still occur");
            lagged[t].RecoveryPrincipal.Should().BeApproximately(0, 0.01,
                $"period {t}: recovery has not arrived within the {lag}-month lag");
        }

        // Default / prepay / interest / ending balance are unaffected by the lag,
        // and the recovery curve is simply shifted forward by `lag`.
        for (var t = 0; t < noLag.Count - 1; t++)
        {
            var tol = Math.Max(0.01, 1e-7 * noLag[t].BeginBalance);
            lagged[t].DefaultedPrincipal.Should().BeApproximately(noLag[t].DefaultedPrincipal, tol,
                $"period {t}: defaults unchanged by recovery lag");
            lagged[t].UnscheduledPrincipal.Should().BeApproximately(noLag[t].UnscheduledPrincipal, tol,
                $"period {t}: prepays unchanged by recovery lag");
            lagged[t].Balance.Should().BeApproximately(noLag[t].Balance, tol,
                $"period {t}: ending balance unchanged by recovery lag");

            // Recovery at t in the no-lag run appears at t + lag in the lagged run.
            lagged[t + lag].RecoveryPrincipal.Should().BeApproximately(noLag[t].RecoveryPrincipal, tol,
                $"period {t}: recovery is shifted forward by {lag} months");
        }

        // Lifetime recovery is conserved (nothing lost to the shift, within horizon).
        lagged.Sum(p => p.RecoveryPrincipal).Should().BeApproximately(
            noLag.Sum(p => p.RecoveryPrincipal), 1.0,
            "the lag re-times recoveries, it does not create or destroy them");
    }

    private static List<PeriodCashflows> RunPeriods(int recoveryLag)
    {
        var firstProjDate = new DateTime(2026, 7, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);
        IRateProvider rateProvider = null!;

        var assumps = new AssetAssumptions(
            PrepaymentTypeEnum.SMM, new ConstVector(anchorAbsT, SmmPct),
            DefaultTypeEnum.MDR, new ConstVector(anchorAbsT, MdrPct),
            new ConstVector(anchorAbsT, SevPct))
        {
            RecoveryLag = recoveryLag,
        };

        var result = CfCore.GenerateAssetCashflows(
            new List<IAsset> { FrmAsset(balance: 1_000_000, ratePct: 6.0, term: 360) },
            firstProjDate, null, _ => assumps, rateProvider);

        return result.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();
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
