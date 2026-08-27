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
/// The liquidation pipeline (graam-harmony #4481 §2).
///
/// A default recognised at t liquidates at t + RecoveryLag. Between those two
/// points the defaulted principal sits in a pipeline: recognised, not yet
/// resolved. Two consequences the engine did not model:
///
///   1. The pipeline balance was invisible — `Balance` goes net of the default
///      the moment it is booked, and nothing reported what was in flight.
///   2. A default was booked even when `t + lag` fell beyond the collateral's
///      remaining term, so the loss was recognised and the recovery simply fell
///      off the end. On a 60-month loan at lag 12 that was 227.20 of defaults
///      whose recovery never arrived; at lag 24, 868.40 (16.7% of all defaults).
///
/// The pipeline is reported SEPARATELY and deliberately not summed into
/// `Balance`: reinvestment gap sizing, `oc_amt` and the collateral-value call
/// factor all read `Balance` as PERFORMING collateral, and CLO indentures are
/// explicit that a defaulted obligation leaves the OC numerator at par and
/// returns only at a rating-agency recovery value.
/// </summary>
public class LiquidationPipelineTests
{
    private const double CdrPct = 2.0;
    private const double SevPct = 40.0;
    private const double Sev = SevPct / 100.0;

    /// <summary>A loan that retires well before its contractual term.</summary>
    private static Asset EarlyRetiring(int term) => new()
    {
        AssetName = "PIPE-EARLY",
        AssetId = "PIPE-EARLY",
        InterestRateType = InterestRateType.FRM,
        OriginalDate = new DateTime(2026, 6, 1),
        OriginalBalance = 100_000,
        CurrentBalance = 100_000,
        BalanceAtIssuance = 100_000,
        OriginalInterestRate = 6.0,
        CurrentInterestRate = 6.0,
        OriginalAmortizationTerm = term,
        DebtService = 3000.0,
        ServiceFee = 0.0,
        GroupNum = "1",
        IsIO = false,
    };

    private static Asset Frm(int term) => new()
    {
        AssetName = "PIPE",
        AssetId = "PIPE",
        InterestRateType = InterestRateType.FRM,
        OriginalDate = new DateTime(2026, 6, 1),
        OriginalBalance = 100_000,
        CurrentBalance = 100_000,
        BalanceAtIssuance = 100_000,
        OriginalInterestRate = 6.0,
        CurrentInterestRate = 6.0,
        OriginalAmortizationTerm = term,
        ServiceFee = 0.0,
        GroupNum = "1",
        IsIO = false,
    };

    private static List<PeriodCashflows> Run(int lag, int term = 60, bool earlyRetiring = false)
    {
        var firstProjDate = new DateTime(2026, 7, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);
        IRateProvider rateProvider = null!;

        var assumps = new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, 0.0),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, CdrPct),
            new ConstVector(anchorAbsT, SevPct))
        { RecoveryLag = lag };

        var result = CfCore.GenerateAssetCashflows(
            new List<IAsset> { earlyRetiring ? EarlyRetiring(term) : Frm(term) },
            firstProjDate, null, _ => assumps, rateProvider);
        return result.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(12)]
    public void PipelineBalance_IsExactlyTheDefaultsAwaitingLiquidation(int lag)
    {
        var periods = Run(lag);

        for (var t = 0; t < periods.Count; t++)
        {
            // Defaults recognised in (t - lag, t] have not yet liquidated.
            var expected = 0.0;
            for (var q = Math.Max(0, t - lag + 1); q <= t; q++)
                expected += periods[q].DefaultedPrincipal;

            periods[t].LiquidationPipelineBalance.Should().BeApproximately(expected, 1e-6,
                $"period {t}: the pipeline holds exactly the defaults recognised in the " +
                $"last {lag} periods and not yet liquidated");
        }
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(12, false)]
    // An asset that RETIRES EARLY is the case that matters: the amortization loop
    // breaks the moment it does, while its recoveries keep arriving at t + lag.
    // Draining inside that loop reported an empty pipeline for exactly those
    // periods — "drains to zero" was satisfied by the writer stopping, not by the
    // pipeline emptying.
    [InlineData(12, true)]
    [InlineData(24, true)]
    public void PipelineDrainsToZeroByTheEnd(int lag, bool earlyRetiring)
    {
        var periods = Run(lag, earlyRetiring: earlyRetiring);

        periods.Should().Contain(p => p.LiquidationPipelineBalance > 0,
            "the fixture must actually put something in the pipeline");
        periods[^1].LiquidationPipelineBalance.Should().BeApproximately(0.0, 1e-6,
            "every recognised default must liquidate before the projection ends");

        // Every period that receives a recovery must have had something in flight
        // in the period before it — the property that broke when the pipeline was
        // accumulated inside the amortization loop.
        for (var t = 1; t < periods.Count; t++)
            if (periods[t].RecoveryPrincipal > 0.005)
                periods[t - 1].LiquidationPipelineBalance.Should().BeGreaterThan(0.0,
                    $"period {t} receives a recovery, so period {t - 1} must show it in flight");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(12, false)]
    [InlineData(12, true)]
    [InlineData(24, true)]
    public void PrincipalIsFullyAccountedFor(int lag, bool earlyRetiring)
    {
        var periods = Run(lag, earlyRetiring: earlyRetiring);

        // Every dollar of original balance leaves as scheduled principal, prepayment
        // or default, and the pool ends at zero. A reporting change must not strand
        // principal — an earlier revision of this branch suppressed `defPrin` without
        // also zeroing `mdr`, leaving the scheduled-principal haircut with no
        // counterparty and up to 34.7bp permanently unaccounted for.
        var accounted = periods.Sum(p =>
            p.ScheduledPrincipal + p.UnscheduledPrincipal + p.DefaultedPrincipal);

        accounted.Should().BeApproximately(100_000.0, 1.0,
            "scheduled + prepaid + defaulted principal must equal the original balance");
        periods[^1].Balance.Should().BeApproximately(0.0, 1.0,
            "the pool must fully retire");
    }

    [Fact]
    public void PipelineIsNotIncludedInBalance()
    {
        var periods = Run(12);

        // Balance is PERFORMING collateral: it goes net of a default the moment the
        // default is booked, and the pipeline sits beside it rather than inside it.
        // The period recurrence is the direct test — if the pipeline were folded
        // into Balance, Balance would exceed this by the in-flight amount.
        //
        // (Comparing lagged vs no-lag Balance would NOT test this: the suppression
        // rule legitimately raises the lagged balance by removing tail defaults.)
        var sawPipeline = false;
        for (var t = 0; t < periods.Count - 1; t++)
        {
            var p = periods[t];
            if (p.LiquidationPipelineBalance > 0) sawPipeline = true;

            var expected = p.BeginBalance
                           - p.ScheduledPrincipal
                           - p.UnscheduledPrincipal
                           - p.DefaultedPrincipal;

            p.Balance.Should().BeApproximately(expected, 0.01,
                $"period {t}: Balance is begin minus scheduled, prepaid and DEFAULTED " +
                $"principal. The pipeline ({p.LiquidationPipelineBalance:N2} here) is reported " +
                "alongside it, never inside it — reinvestment sizing, oc_amt and the call " +
                "factor all read Balance as performing collateral");
        }

        sawPipeline.Should().BeTrue("the fixture must actually exercise a non-empty pipeline");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(12)]
    public void EveryBookedDefaultRecoversItsFullShare(int lag)
    {
        var periods = Run(lag);

        periods.Sum(p => p.RecoveryPrincipal).Should().BeApproximately(
            periods.Sum(p => p.DefaultedPrincipal) * (1 - Sev), 0.01,
            "suppressing un-liquidatable defaults must not strand a recovery: what is " +
            "booked recovers (1 - severity), and what is not booked recovers nothing");
    }
}
