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

    private static Asset MakeAsset()
    {
        return new Asset
        {
            AssetName = "A",
            AssetId = "A",
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

        IRateProvider rateProvider = null!;
        var result = CfCore.GenerateAssetCashflows(
            new List<IAsset> { MakeAsset() },
            firstProjDate, null, _ => assumps, rateProvider);

        return result.PeriodCashflows[0];
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
}
