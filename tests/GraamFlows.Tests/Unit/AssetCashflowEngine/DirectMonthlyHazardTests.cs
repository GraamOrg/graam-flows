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
/// Direct-monthly-hazard tests (harmony #1226). A loan-level performance model
/// emits per-period monthly prepay (SMM) and default (MDR) hazards and needs to
/// feed them straight through the engine, NOT have them treated as annual
/// CPR/CDR and de-annualized.
///
/// Before this change, <see cref="CfCore.GenerateAssetCashflows"/> always built
/// the smm/mdr assumption arrays with <c>convertToMonthly=true</c>, so:
///   - <see cref="PrepaymentTypeEnum.SMM"/> was a no-op (input was de-annualized
///     exactly like plain CPR), and
///   - there was no way to request MDR at all (the input was always de-annualized).
///
/// These tests pin the new behavior — SMM/MDR flow through as direct monthly
/// fractions — and guard the regression that plain CPR/CDR still de-annualize.
/// The amortizer math (<c>defPrin = mdr * schedBal</c>, prepay
/// <c>= smm * (balance - schedPrin)</c>) is unchanged.
/// </summary>
public class DirectMonthlyHazardTests
{
    private const double Tolerance = 0.5;

    private static Asset MakeAsset(string id = "A")
    {
        return new Asset
        {
            AssetName = id,
            AssetId = id,
            InterestRateType = InterestRateType.FRM,
            OriginalDate = new DateTime(2024, 1, 1),
            OriginalBalance = 1_000_000,
            CurrentBalance = 1_000_000,
            BalanceAtIssuance = 1_000_000,
            OriginalInterestRate = 6.0,
            CurrentInterestRate = 6.0,
            OriginalAmortizationTerm = 360,
            ServiceFee = 0.0,
            GroupNum = "1",
            IsIO = false,
        };
    }

    private static PeriodCashflows RunPeriodZero(
        PrepaymentTypeEnum prepaymentType, double cprPct,
        DefaultTypeEnum defaultType, double cdrPct, double sevPct)
    {
        var firstProjDate = new DateTime(2024, 6, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);

        var assumps = new AssetAssumptions(
            prepaymentType, new ConstVector(anchorAbsT, cprPct),
            defaultType, new ConstVector(anchorAbsT, cdrPct),
            new ConstVector(anchorAbsT, sevPct));

        return Run(assumps).PeriodCashflows[0];
    }

    /// <summary>
    /// Run the engine with arbitrary per-period prepay/default curves and return
    /// every period's cashflow, so vector tests can pin hazards at multiple
    /// distinct periods.
    /// </summary>
    private static IList<PeriodCashflows> RunAll(
        PrepaymentTypeEnum prepaymentType, double[] cprVector,
        DefaultTypeEnum defaultType, double[] cdrVector, double sevPct)
    {
        var firstProjDate = new DateTime(2024, 6, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);

        var assumps = new AssetAssumptions(
            prepaymentType, new ArrayVector(anchorAbsT, cprVector),
            defaultType, new ArrayVector(anchorAbsT, cdrVector),
            new ConstVector(anchorAbsT, sevPct));

        return Run(assumps).PeriodCashflows;
    }

    private static CollateralCashflows Run(AssetAssumptions assumps)
    {
        var firstProjDate = new DateTime(2024, 6, 1);
        IRateProvider rateProvider = null!;
        return CfCore.GenerateAssetCashflows(
            new List<IAsset> { MakeAsset() },
            firstProjDate, null, _ => assumps, rateProvider);
    }

    /// <summary>
    /// SMM=12% is a direct monthly prepay hazard: period-0 unscheduled principal
    /// is 0.12 × (1,000,000 − scheduledPrincipal) ≈ 119,880. Before the fix this
    /// was de-annualized to a ~1.06% monthly rate (≈ 10,585.69).
    /// </summary>
    [Fact]
    public void Smm_Prepay_Is_Direct_Monthly_Not_DeAnnualized()
    {
        var cf = RunPeriodZero(
            PrepaymentTypeEnum.SMM, cprPct: 12.0,
            DefaultTypeEnum.CDR, cdrPct: 0.0, sevPct: 100.0);

        cf.UnscheduledPrincipal.Should().BeApproximately(119_880.05, 1.0,
            "SMM=12% must be applied as a direct 12% monthly prepay on the " +
            "post-scheduled-principal balance, not de-annualized to ~1.06%");
    }

    /// <summary>
    /// MDR=12% is a direct monthly default hazard: period-0 defaulted principal
    /// is 0.12 × 1,000,000 = 120,000, and at 100% severity collateral loss is the
    /// full 120,000. Before the fix MDR could not be requested and the input was
    /// de-annualized (≈ 10,596.24).
    /// </summary>
    [Fact]
    public void Mdr_Default_Is_Direct_Monthly_Not_DeAnnualized()
    {
        var cf = RunPeriodZero(
            PrepaymentTypeEnum.CPR, cprPct: 0.0,
            DefaultTypeEnum.MDR, cdrPct: 12.0, sevPct: 100.0);

        cf.DefaultedPrincipal.Should().BeApproximately(120_000.00, Tolerance,
            "MDR=12% must be applied as a direct 12% monthly default on the " +
            "beginning balance, not de-annualized to ~1.06%");
        cf.CollateralLoss.Should().BeApproximately(120_000.00, Tolerance,
            "at 100% severity, the full defaulted principal is lost");
    }

    /// <summary>
    /// Regression: plain CPR (no prepaymentType override) still de-annualizes the
    /// annual rate to a monthly SMM. CPR=12% → ~1.06% monthly → ≈ 10,585.69.
    /// </summary>
    [Fact]
    public void Cpr_Prepay_Still_DeAnnualizes()
    {
        var cf = RunPeriodZero(
            PrepaymentTypeEnum.CPR, cprPct: 12.0,
            DefaultTypeEnum.CDR, cdrPct: 0.0, sevPct: 100.0);

        cf.UnscheduledPrincipal.Should().BeApproximately(10_585.69, Tolerance,
            "CPR=12% must de-annualize to a ~1.06% monthly prepay (unchanged behavior)");
    }

    /// <summary>
    /// Regression: plain CDR (no defaultType override) still de-annualizes the
    /// annual rate to a monthly MDR. CDR=12% → ~1.06% monthly → ≈ 10,596.24.
    /// </summary>
    [Fact]
    public void Cdr_Default_Still_DeAnnualizes()
    {
        var cf = RunPeriodZero(
            PrepaymentTypeEnum.CPR, cprPct: 0.0,
            DefaultTypeEnum.CDR, cdrPct: 12.0, sevPct: 100.0);

        cf.DefaultedPrincipal.Should().BeApproximately(10_596.24, Tolerance,
            "CDR=12% must de-annualize to a ~1.06% monthly default (unchanged behavior)");
    }

    /// <summary>
    /// Time-varying SMM (graam-flows#14): the harmony#1226 loan-level model emits
    /// a *different* monthly prepay hazard per period, not a scalar. Pin that each
    /// period's unscheduled principal uses THAT period's SMM as a direct fraction —
    /// <c>SMM[N] × (beginningBalance − scheduledPrincipal)</c> — for several
    /// distinct periods. Distinct per-period rates make this a period-INDEXING
    /// test: a one-off in the array→period mapping would change the result.
    /// </summary>
    [Fact]
    public void Smm_Prepay_TimeVarying_Vector_Applies_PerPeriod_Hazard()
    {
        // Distinct SMM each period (percent), all default off.
        var smm = new[] { 5.0, 10.0, 15.0, 8.0, 20.0 };
        var cfs = RunAll(
            PrepaymentTypeEnum.SMM, smm,
            DefaultTypeEnum.CDR, new[] { 0.0 }, sevPct: 100.0);

        foreach (var n in new[] { 0, 1, 2, 3 })
        {
            var cf = cfs[n];
            var expected = smm[n] / 100.0 * (cf.BeginBalance - cf.ScheduledPrincipal);
            cf.UnscheduledPrincipal.Should().BeApproximately(expected, 1.0,
                $"period {n} must apply SMM[{n}]={smm[n]}% directly to the " +
                "post-scheduled-principal balance, not a neighboring period's rate");
        }
    }

    /// <summary>
    /// Time-varying MDR (graam-flows#14): per-period monthly default hazards. Pin
    /// that each period's defaulted principal is <c>MDR[N] × beginningBalance</c>
    /// for several distinct periods. Distinct per-period rates make this a
    /// period-INDEXING test on the default path.
    /// </summary>
    [Fact]
    public void Mdr_Default_TimeVarying_Vector_Applies_PerPeriod_Hazard()
    {
        // Distinct MDR each period (percent), prepay off.
        var mdr = new[] { 2.0, 4.0, 1.0, 6.0, 3.0 };
        var cfs = RunAll(
            PrepaymentTypeEnum.CPR, new[] { 0.0 },
            DefaultTypeEnum.MDR, mdr, sevPct: 100.0);

        foreach (var n in new[] { 0, 1, 2, 3 })
        {
            var cf = cfs[n];
            var expected = mdr[n] / 100.0 * cf.BeginBalance;
            cf.DefaultedPrincipal.Should().BeApproximately(expected, 1.0,
                $"period {n} must apply MDR[{n}]={mdr[n]}% directly to the " +
                "beginning balance, not a neighboring period's rate");
        }
    }

    /// <summary>
    /// Heterogeneous group (graam-flows#15): two assets in the SAME group with
    /// DIFFERENT prepay modes — asset A is CPR=12% (de-annualizes to ~10,585.69),
    /// asset B is SMM=12% (direct monthly ~119,880.05). Each must be converted on
    /// its OWN mode, so the group's period-0 unscheduled principal is the sum
    /// ~130,465.74. Under the old first-asset-wins resolution, B would have been
    /// de-annualized like A (group total ~21,171) — this test fails on that code.
    /// </summary>
    [Fact]
    public void MixedPrepayTypes_InOneGroup_ResolvePerAsset()
    {
        var firstProjDate = new DateTime(2024, 6, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);

        var cprAssumps = new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, 12.0),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, 0.0),
            new ConstVector(anchorAbsT, 100.0));
        var smmAssumps = new AssetAssumptions(
            PrepaymentTypeEnum.SMM, new ConstVector(anchorAbsT, 12.0),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, 0.0),
            new ConstVector(anchorAbsT, 100.0));

        IRateProvider rateProvider = null!;
        var result = CfCore.GenerateAssetCashflows(
            new List<IAsset> { MakeAsset("A"), MakeAsset("B") },
            firstProjDate, null,
            asset => asset.AssetId == "A" ? cprAssumps : smmAssumps,
            rateProvider);

        // A: ~10,585.69 (de-annualized) + B: ~119,880.05 (direct monthly).
        result.PeriodCashflows[0].UnscheduledPrincipal.Should().BeApproximately(130_465.74, 2.0,
            "each asset in the group must use its own PrepaymentType, not asset[0]'s");
    }
}
