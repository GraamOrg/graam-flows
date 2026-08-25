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
/// Defence in depth for graam-harmony #4476: the engine must not manufacture NaN
/// from an out-of-range annual rate.
///
/// <c>CfCore.BuildAssumptionArray</c> de-annualized with
/// <c>1 - Pow(1 - value/100, 1/12)</c>. Above 100 the base is negative and a
/// twelfth root of a negative is NaN. The amortizer's two guards are both
/// NaN-blind: <c>Math.Clamp(smm, 0, 1)</c> is a complete no-op because every
/// comparison against NaN is false, and <c>if (balance &lt; 1) break</c> never fires
/// once balance is NaN — so a single bad assumption emitted NaN for EVERY period
/// rather than for one.
///
/// The API boundary validator now rejects such a request, so these tests reach the
/// engine directly to pin the second line of defence: whatever the assumption, the
/// emitted cashflows stay finite. They also pin that the in-range behaviour is
/// unchanged, since a saturating clamp that moved ordinary numbers would be worse
/// than the bug it fixes.
/// </summary>
public class NonFiniteHazardTests
{
    private static Asset MakeAsset() => new()
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

    private static CollateralCashflows Run(double cprPct, double cdrPct, double sevPct)
    {
        var firstProjDate = new DateTime(2024, 6, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);
        var assumps = new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, cprPct),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, cdrPct),
            new ConstVector(anchorAbsT, sevPct));

        IRateProvider rateProvider = null!;
        return CfCore.GenerateAssetCashflows(
            new List<IAsset> { MakeAsset() },
            firstProjDate, null, _ => assumps, rateProvider);
    }

    [Theory]
    [InlineData(100.01, 0.5)]
    [InlineData(150.0, 0.5)]
    [InlineData(1000.0, 0.5)]
    [InlineData(6.0, 100.01)]
    [InlineData(6.0, 1000.0)]
    public void GenerateAssetCashflows_RateAboveOneHundred_EmitsOnlyFiniteCashflows(double cprPct, double cdrPct)
    {
        var cashflows = Run(cprPct, cdrPct, sevPct: 40.0);

        cashflows.PeriodCashflows.Should().NotBeEmpty(
            "the projection must still produce periods — the old NaN balance defeated the "
            + "`if (balance < 1) break` termination check as well as the clamp");

        foreach (var cf in cashflows.PeriodCashflows)
        {
            double.IsFinite(cf.UnscheduledPrincipal).Should().BeTrue(
                $"UnscheduledPrincipal must be finite at {cf.CashflowDate:yyyy-MM-dd}; a NaN here is what "
                + "/api/waterfall used to reject two service calls after the real mistake");
            double.IsFinite(cf.DefaultedPrincipal).Should().BeTrue(
                $"DefaultedPrincipal must be finite at {cf.CashflowDate:yyyy-MM-dd}");
            double.IsFinite(cf.Balance).Should().BeTrue(
                $"Balance must be finite at {cf.CashflowDate:yyyy-MM-dd}; Math.Clamp cannot clamp a NaN, "
                + "so the poison used to spread to every subsequent period");
        }
    }

    [Fact]
    public void GenerateAssetCashflows_CprAboveOneHundred_SaturatesToFullPrepayment()
    {
        var cashflows = Run(cprPct: 150.0, cdrPct: 0.0, sevPct: 0.0);
        var first = cashflows.PeriodCashflows[0];

        first.UnscheduledPrincipal.Should().BeGreaterThan(0,
            "a CPR at or above 100 saturates to a monthly hazard of 1.0 — the whole post-scheduled-principal "
            + "balance prepays, which is the meaningful limit rather than NaN");
        first.Balance.Should().BeApproximately(0.0, 1.0,
            "at a saturated prepayment hazard the pool pays off in the first period");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(6.0)]
    [InlineData(99.9)]
    public void GenerateAssetCashflows_InRangeCpr_IsUnchangedByTheClamp(double cprPct)
    {
        var cashflows = Run(cprPct, cdrPct: 0.0, sevPct: 0.0);
        var first = cashflows.PeriodCashflows[0];

        var expectedSmm = 1.0 - Math.Pow(1.0 - cprPct / 100.0, 1.0 / 12.0);
        var expected = expectedSmm * (first.BeginBalance - first.ScheduledPrincipal);

        first.UnscheduledPrincipal.Should().BeApproximately(expected, 1.0,
            "the saturating guard must not touch any in-range rate — this engine has WAL/price tie-out tests "
            + "and the de-annualization expression is deliberately byte-for-byte unchanged");
    }
}
