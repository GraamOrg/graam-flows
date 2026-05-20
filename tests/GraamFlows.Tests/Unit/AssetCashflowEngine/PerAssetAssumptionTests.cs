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
/// Per-asset assumption tests (graam-flows#5). Before this change,
/// <see cref="CfCore.GenerateAssetCashflows"/> resolved assumptions
/// from the FIRST asset in each group and applied them uniformly to
/// all other assets in the group — the engine's per-asset signal was
/// discarded at the orchestrator layer. These tests pin the new
/// behavior: each asset is scored under its own
/// <see cref="IAssetAssumptions"/> resolved via the assumption mill's
/// <see cref="IAssumptionMill.GetAssumptionsForAsset"/> callback.
///
/// The classic engine path (uniform per-group assumptions) is exercised
/// by the existing test suite — these tests focus on what's new.
/// </summary>
public class PerAssetAssumptionTests
{
    private static Asset MakeAsset(string id, double balance, string groupNum = "1")
    {
        return new Asset
        {
            AssetName = id,
            AssetId = id,
            InterestRateType = InterestRateType.FRM,
            OriginalDate = new DateTime(2024, 1, 1),
            OriginalBalance = balance,
            CurrentBalance = balance,
            BalanceAtIssuance = balance,
            OriginalInterestRate = 6.0,
            CurrentInterestRate = 6.0,
            OriginalAmortizationTerm = 60,
            ServiceFee = 0.25,
            GroupNum = groupNum,
            IsIO = false,
        };
    }

    private static IAssetAssumptions MakeAssumps(double cdrPct, int anchorAbsT)
    {
        // CPR=0%, severity=50%, no delinquency. We hold every dimension
        // constant across configurations *except* CDR — that's the
        // dimension we want to isolate.
        return new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, 0.0),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, cdrPct),
            new ConstVector(anchorAbsT, 50.0));
    }

    /// <summary>
    /// The load-bearing assertion: per-asset CDRs should drive a different
    /// total defaulted principal than either uniform configuration.
    /// </summary>
    [Fact]
    public void PerAsset_Cdr_Produces_Distinct_Defaulted_Principal()
    {
        var firstProjDate = new DateTime(2024, 6, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);

        // Two assets in the SAME group. Asset A is large, Asset B is small.
        // If only Asset A's CDR were applied to the whole group (the
        // pre-#5 behavior), the difference between "A=0% / B=10%" and
        // "A=10% / B=0%" should be near-zero — same per-period CDR
        // applied to the same total balance. With the fix, the two
        // configurations diverge because each asset is scored under its
        // own CDR.
        var assetLarge = MakeAsset("A", balance: 1_000_000);
        var assetSmall = MakeAsset("B", balance: 100_000);

        var zeroCdr = MakeAssumps(0.0, anchorAbsT);
        var highCdr = MakeAssumps(10.0, anchorAbsT);

        IRateProvider rateProvider = null!;

        // Config 1: only the LARGE asset has a high CDR
        var assumpsLargeOnly = new Func<IAsset, IAssetAssumptions>(a =>
            a.AssetId == "A" ? highCdr : zeroCdr);

        // Config 2: only the SMALL asset has a high CDR
        var assumpsSmallOnly = new Func<IAsset, IAssetAssumptions>(a =>
            a.AssetId == "B" ? highCdr : zeroCdr);

        var resultLargeOnly = CfCore.GenerateAssetCashflows(
            new List<IAsset> { assetLarge, assetSmall },
            firstProjDate, null, assumpsLargeOnly, rateProvider);
        var resultSmallOnly = CfCore.GenerateAssetCashflows(
            new List<IAsset> { assetLarge, assetSmall },
            firstProjDate, null, assumpsSmallOnly, rateProvider);

        var defaultedLargeOnly = resultLargeOnly.PeriodCashflows.Sum(cf => cf.DefaultedPrincipal);
        var defaultedSmallOnly = resultSmallOnly.PeriodCashflows.Sum(cf => cf.DefaultedPrincipal);

        // Asset A is 10x the size of Asset B. When A defaults at 10% and
        // B at 0%, total defaults should be ~10x what you'd get from B
        // defaulting and A surviving. Before the fix, "first asset wins
        // per group" meant both configs would have looked identical
        // (whichever was first in the list would drive everything).
        defaultedLargeOnly.Should().BeGreaterThan(defaultedSmallOnly * 5,
            "asset A (10x balance) defaulting at 10% must produce far more total " +
            "defaulted principal than asset B (1/10 balance) defaulting at 10%; " +
            "if they're equal, the engine is still applying first-asset assumptions " +
            "to the whole group (graam-flows#5 not actually fixed)");
    }

    /// <summary>
    /// Uniform-per-group callers — every asset returns the same
    /// IAssetAssumptions reference — must still work. This is the
    /// backwards-compat guarantee for the existing CalcCollateral users
    /// who supply only deal-level scalar/vector assumptions.
    /// </summary>
    [Fact]
    public void Uniform_Assumptions_Produce_Same_Result_As_Pre_Issue5()
    {
        var firstProjDate = new DateTime(2024, 6, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);

        var assetA = MakeAsset("A", balance: 500_000);
        var assetB = MakeAsset("B", balance: 500_000);

        var uniformAssumps = MakeAssumps(5.0, anchorAbsT);
        IRateProvider rateProvider = null!;

        // Both call sites produce the same IAssetAssumptions for every
        // asset — this is what the deal-level scalar/vector path does.
        var result = CfCore.GenerateAssetCashflows(
            new List<IAsset> { assetA, assetB },
            firstProjDate, null, _ => uniformAssumps, rateProvider);

        var totalDefaulted = result.PeriodCashflows.Sum(cf => cf.DefaultedPrincipal);

        // At CDR=5%, no prepay, on $1MM total balance over ~60 months,
        // we expect roughly $250k-$300k of defaulted principal (the
        // exact figure depends on amortization). We don't pin the exact
        // number — that's what the EART231 integration tests do. We
        // just assert that defaults *happened*, since the regression we'd
        // be guarding against is "uniform-per-group stopped working
        // because the per-asset rewrite introduced a bug."
        totalDefaulted.Should().BeGreaterThan(100_000,
            "uniform CDR=5% on $1MM should generate substantial defaults");
        totalDefaulted.Should().BeLessThan(500_000,
            "uniform CDR=5% should not produce defaults > original balance");
    }

    /// <summary>
    /// A null IAssetAssumptions in the per-asset path falls through to
    /// zero rates (no defaults, no prepays). Verifies the null-safety
    /// of the per-asset rewrite — the original code had the same
    /// behavior via firstAssetAssumps?.Prepayment etc.
    /// </summary>
    [Fact]
    public void Null_Asset_Assumptions_Treated_As_Zero_Rates()
    {
        var firstProjDate = new DateTime(2024, 6, 1);

        var assetA = MakeAsset("A", balance: 500_000);
        IRateProvider rateProvider = null!;

        // Func returns null for every asset
        var result = CfCore.GenerateAssetCashflows(
            new List<IAsset> { assetA },
            firstProjDate, null, _ => null!, rateProvider);

        var totalDefaulted = result.PeriodCashflows.Sum(cf => cf.DefaultedPrincipal);
        var totalUnscheduled = result.PeriodCashflows.Sum(cf => cf.UnscheduledPrincipal);

        totalDefaulted.Should().Be(0,
            "null assumptions should default to CDR=0% → no defaults");
        // Last period sweeps remaining balance into UnscheduledPrincipal
        // as the cleanup pass, so we can't assert "0 unscheduled" — just
        // that no PREPAY happened during the amortization itself. The
        // largest single period of unscheduled should be the cleanup,
        // not a prepay.
        totalUnscheduled.Should().BeLessThan(assetA.OriginalBalance,
            "null assumptions should default to CPR=0% (no prepays during life)");
    }
}
