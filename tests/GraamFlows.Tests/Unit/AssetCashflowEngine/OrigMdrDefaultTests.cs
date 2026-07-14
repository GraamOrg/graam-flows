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
/// ORIGMDR default-basis tests (Intex ICMODEFAULT_VORIGMDR). Consumer-ABS loss
/// tapes (e.g. Pagaya) quote default as a monthly percentage of the ORIGINAL
/// balance, so a flat rate produces constant-dollar defaults every period —
/// unlike <see cref="DefaultTypeEnum.MDR"/>, a hazard on the CURRENT performing
/// balance whose dollar default shrinks as the pool amortizes.
///
/// The amortizer re-expresses ORIGMDR as an effective current-balance hazard
/// (<c>min(rate × originalBalance, performingBalance) / performingBalance</c>)
/// so every downstream use of <c>mdr</c> (scheduled-principal reduction,
/// survival factor, defaulted principal) stays consistent; these tests pin the
/// original-balance basis, the performing-balance cap, and per-asset routing.
/// </summary>
public class OrigMdrDefaultTests
{
    private const double Tolerance = 1.0;
    private const double OrigBalance = 1_000_000;

    private static Asset MakeAsset(string id = "A")
    {
        return new Asset
        {
            AssetName = id,
            AssetId = id,
            InterestRateType = InterestRateType.FRM,
            OriginalDate = new DateTime(2024, 1, 1),
            OriginalBalance = OrigBalance,
            CurrentBalance = OrigBalance,
            BalanceAtIssuance = OrigBalance,
            OriginalInterestRate = 6.0,
            CurrentInterestRate = 6.0,
            OriginalAmortizationTerm = 360,
            ServiceFee = 0.0,
            GroupNum = "1",
            IsIO = false,
        };
    }

    private static IList<PeriodCashflows> RunAll(
        DefaultTypeEnum defaultType, double cdrPct, double sevPct = 100.0)
    {
        var firstProjDate = new DateTime(2024, 6, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);

        var assumps = new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, 0.0),
            defaultType, new ConstVector(anchorAbsT, cdrPct),
            new ConstVector(anchorAbsT, sevPct));

        IRateProvider rateProvider = null!;
        return CfCore.GenerateAssetCashflows(
            new List<IAsset> { MakeAsset() },
            firstProjDate, null, _ => assumps, rateProvider).PeriodCashflows;
    }

    /// <summary>
    /// ORIGMDR=1% is a monthly default on the ORIGINAL balance: every period's
    /// defaulted principal is 0.01 × 1,000,000 = 10,000 — a constant dollar
    /// amount that does NOT decay as the pool amortizes. Pinned at period 0 and
    /// a later period to prove the original-balance (not current-balance) basis.
    /// </summary>
    [Fact]
    public void OrigMdr_Default_Is_Percent_Of_Original_Not_Current()
    {
        var cfs = RunAll(DefaultTypeEnum.ORIGMDR, cdrPct: 1.0);

        cfs[0].DefaultedPrincipal.Should().BeApproximately(10_000.00, Tolerance,
            "ORIGMDR=1% is 1% of the ORIGINAL 1,000,000 balance");
        cfs[6].DefaultedPrincipal.Should().BeApproximately(10_000.00, Tolerance,
            "ORIGMDR stays a constant 1% of ORIGINAL balance even after the pool " +
            "has amortized — it does not shrink with the current balance");
        cfs[0].CollateralLoss.Should().BeApproximately(10_000.00, Tolerance,
            "at 100% severity the full defaulted principal is lost");
    }

    /// <summary>
    /// Contrast with MDR at the SAME 1% rate: MDR defaults 1% of the CURRENT
    /// balance, so once the pool has amortized its period-6 default is strictly
    /// below the ORIGMDR constant 10,000. This is the property that distinguishes
    /// the two bases — a bug that routed ORIGMDR through the MDR path would make
    /// these equal.
    /// </summary>
    [Fact]
    public void OrigMdr_ExceedsMdr_AsBalanceAmortizes()
    {
        var origmdr = RunAll(DefaultTypeEnum.ORIGMDR, cdrPct: 1.0);
        var mdr = RunAll(DefaultTypeEnum.MDR, cdrPct: 1.0);

        origmdr[6].DefaultedPrincipal.Should().BeApproximately(10_000.00, Tolerance);
        mdr[6].DefaultedPrincipal.Should().BeLessThan(9_900.00,
            "MDR=1% is 1% of the DECLINED current balance at period 6, so it is " +
            "meaningfully below the ORIGMDR constant of 10,000");
    }

    /// <summary>
    /// The default is capped at the performing balance: a rate high enough that
    /// rate × originalBalance would exceed the remaining balance must never
    /// default more than what is left, and the balance must never go negative.
    /// </summary>
    [Fact]
    public void OrigMdr_CapsAtPerformingBalance()
    {
        // 40%/mo of original = 400,000/period; the ~1MM pool is exhausted in a
        // handful of periods, where the last default is capped at the remainder.
        var cfs = RunAll(DefaultTypeEnum.ORIGMDR, cdrPct: 40.0);

        double cumDefault = 0;
        foreach (var cf in cfs)
        {
            cf.DefaultedPrincipal.Should().BeLessThanOrEqualTo(cf.BeginBalance + Tolerance,
                "a period can never default more than its performing balance");
            cf.Balance.Should().BeGreaterThanOrEqualTo(-Tolerance,
                "the balance must never amortize past zero");
            cumDefault += cf.DefaultedPrincipal;
        }

        cumDefault.Should().BeLessThanOrEqualTo(OrigBalance + Tolerance,
            "cumulative defaults can never exceed the original balance");
    }

    /// <summary>
    /// Per-asset routing (graam-flows#15): two assets in the same group, one MDR
    /// and one ORIGMDR at the same 1% rate. At period 6 they must diverge —
    /// ORIGMDR still 10,000, MDR below it — proving the ORIGMDR series is applied
    /// only to the asset that requested it and the lazily-allocated per-asset
    /// matrix leaves the MDR asset on the current-balance path.
    /// </summary>
    [Fact]
    public void MixedDefaultTypes_InOneGroup_RoutePerAsset()
    {
        var firstProjDate = new DateTime(2024, 6, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);

        var mdrAssumps = new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, 0.0),
            DefaultTypeEnum.MDR, new ConstVector(anchorAbsT, 1.0),
            new ConstVector(anchorAbsT, 100.0));
        var origMdrAssumps = new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, 0.0),
            DefaultTypeEnum.ORIGMDR, new ConstVector(anchorAbsT, 1.0),
            new ConstVector(anchorAbsT, 100.0));

        IRateProvider rateProvider = null!;
        var mdrOnly = CfCore.GenerateAssetCashflows(
            new List<IAsset> { MakeAsset("A") }, firstProjDate, null,
            _ => mdrAssumps, rateProvider).PeriodCashflows;

        // Group of two: A=MDR (asset[0], never allocates the ORIGMDR matrix),
        // B=ORIGMDR (forces lazy allocation at asset[1]).
        var mixed = CfCore.GenerateAssetCashflows(
            new List<IAsset> { MakeAsset("A"), MakeAsset("B") }, firstProjDate, null,
            asset => asset.AssetId == "A" ? mdrAssumps : origMdrAssumps, rateProvider).PeriodCashflows;

        // The group's period-6 default = MDR asset (== the MDR-only run) + the
        // ORIGMDR asset's constant 10,000. If B were mis-routed through the MDR
        // path, the group total would be 2× the MDR-only value instead.
        var expected = mdrOnly[6].DefaultedPrincipal + 10_000.00;
        mixed[6].DefaultedPrincipal.Should().BeApproximately(expected, 2.0,
            "asset B must default 1% of ORIGINAL (10,000) while asset A stays on " +
            "the current-balance MDR path");
    }
}
