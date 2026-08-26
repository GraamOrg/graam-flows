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
/// Delinquency is INERT at the collateral level (graam-harmony #4481 §1.1, §1.2).
///
/// A delinquent-but-not-defaulted loan is assumed to cure and pay in full, so
/// delinquency alone must not move a single cash column. Loss comes only from the
/// default assumption (CDR/MDR + severity). Servicer advancing is a TIMING
/// reclassification, never incremental cash and never permanently lost cash — so
/// with no defaults it has no effect either.
///
/// The engine previously docked dq% of BOTH scheduled principal and interest,
/// permanently. That left the performing balance above the contractual schedule,
/// and the residual was then booked at maturity as a DEFAULT — so a `dq=4, cdr=0`
/// run on a real deal reported ~2.9MM of "defaults" it had never been asked to
/// model, and with any non-zero severity turned them into a fabricated credit
/// loss. Separately, `advancing` was divided by 1.0 instead of 100, so
/// `advancing = 100` meant 100x: measured at dq=5 that reported interest at
/// 5.95x the true coupon and paid a 360-month loan off at month 333.
///
/// This area had NO behavioural test coverage before this file, which is why
/// both defects survived. Every case here fails against the pre-#4481 engine.
/// </summary>
public class DelinquencyInertTests
{
    private static Asset Frm(double balance, double ratePct, int term, string id = "DQ")
    {
        return new Asset
        {
            AssetName = id,
            AssetId = id,
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

    /// <summary>CPR/CDR/severity plus an explicit delinquency and advancing rate.</summary>
    private static IAssetAssumptions Assumps(
        int anchorAbsT, double cprPct, double cdrPct, double sevPct, double dqPct, double advPct)
    {
        return new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, cprPct),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, cdrPct),
            new ConstVector(anchorAbsT, sevPct),
            DelinqRateTypeEnum.PctCurrBal, new ConstVector(anchorAbsT, dqPct),
            new ConstVector(anchorAbsT, advPct), new ConstVector(anchorAbsT, advPct));
    }

    private static List<PeriodCashflows> Run(
        IList<IAsset> assets, double cprPct, double cdrPct, double sevPct, double dqPct, double advPct)
    {
        var firstProjDate = new DateTime(2026, 7, 1);
        var anchorAbsT = DateUtil.CalcAbsT(firstProjDate);
        IRateProvider rateProvider = null!;
        var result = CfCore.GenerateAssetCashflows(
            assets, firstProjDate, null,
            _ => Assumps(anchorAbsT, cprPct, cdrPct, sevPct, dqPct, advPct), rateProvider);
        return result.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();
    }

    private static List<IAsset> OneLoan(int term = 360) =>
        new() { Frm(100_000, 6.0, term) };

    /// <summary>Every cash column of two runs, period by period.</summary>
    private static void AssertCashIdentical(
        IReadOnlyList<PeriodCashflows> actual, IReadOnlyList<PeriodCashflows> expected, string because)
    {
        actual.Count.Should().Be(expected.Count, because);
        for (var t = 0; t < expected.Count; t++)
        {
            var a = actual[t];
            var e = expected[t];
            a.ScheduledPrincipal.Should().BeApproximately(e.ScheduledPrincipal, 1e-6, $"{because} (period {t}, scheduled principal)");
            a.UnscheduledPrincipal.Should().BeApproximately(e.UnscheduledPrincipal, 1e-6, $"{because} (period {t}, prepay)");
            a.Interest.Should().BeApproximately(e.Interest, 1e-6, $"{because} (period {t}, interest)");
            a.NetInterest.Should().BeApproximately(e.NetInterest, 1e-6, $"{because} (period {t}, net interest)");
            a.DefaultedPrincipal.Should().BeApproximately(e.DefaultedPrincipal, 1e-6, $"{because} (period {t}, defaults)");
            a.RecoveryPrincipal.Should().BeApproximately(e.RecoveryPrincipal, 1e-6, $"{because} (period {t}, recoveries)");
            a.Balance.Should().BeApproximately(e.Balance, 1e-6, $"{because} (period {t}, ending balance)");
        }
    }

    // ---------------------------------------------------------------- §1.1

    [Theory]
    [InlineData(1.0)]
    [InlineData(5.0)]
    [InlineData(20.0)]
    [InlineData(50.0)]
    public void Delinquency_WithNoDefaults_ChangesNoCashflow(double dqPct)
    {
        var clean = Run(OneLoan(), cprPct: 0, cdrPct: 0, sevPct: 0, dqPct: 0, advPct: 0);
        var delinquent = Run(OneLoan(), cprPct: 0, cdrPct: 0, sevPct: 0, dqPct: dqPct, advPct: 0);

        AssertCashIdentical(delinquent, clean,
            $"a {dqPct}% delinquent pool with no default assumption must produce the SAME cash as a " +
            "0% delinquent one — the loans cure and pay in full. The engine used to dock dq% of both " +
            "principal and interest permanently, so a 50% dq run terminated at period 138 of 360");
    }

    [Theory]
    [InlineData(6.0, 2.0, 40.0)]
    [InlineData(30.0, 10.0, 60.0)]
    public void Delinquency_WithPrepaysAndDefaults_StillChangesNoCashflow(
        double cprPct, double cdrPct, double sevPct)
    {
        var clean = Run(OneLoan(), cprPct, cdrPct, sevPct, dqPct: 0, advPct: 0);
        var delinquent = Run(OneLoan(), cprPct, cdrPct, sevPct, dqPct: 20.0, advPct: 0);

        AssertCashIdentical(delinquent, clean,
            "delinquency is inert even alongside a real prepay/default/severity stack — " +
            "the loss path is driven by CDR and severity alone");
    }

    // ---------------------------------------------------------------- §1.2

    [Theory]
    // No 0.0 case: the reference run IS advPct 0, so it would compare a run to
    // itself and pass against any engine, broken or not.
    [InlineData(25.0)]
    [InlineData(50.0)]
    [InlineData(100.0)]
    public void Advancing_WithNoDefaults_ChangesNoCashflow(double advPct)
    {
        var reference = Run(OneLoan(), 0, 0, 0, dqPct: 20.0, advPct: 0.0);
        var advanced = Run(OneLoan(), 0, 0, 0, dqPct: 20.0, advPct: advPct);

        AssertCashIdentical(advanced, reference,
            $"advancing {advPct}% with no defaults is a no-op — an advance is a timing " +
            "reclassification, never incremental cash");
    }

    // ------------------------------------------------- the maturity default

    [Fact]
    public void ALoanReachingMaturityDelinquent_BooksNoDefault()
    {
        var periods = Run(OneLoan(term: 60), 0, 0, 0, dqPct: 4.0, advPct: 0.0);

        periods.Should().HaveCount(60,
            "the loan must amortize over its full contractual term");
        periods.Sum(p => p.DefaultedPrincipal).Should().Be(0.0,
            "CDR is zero, so nothing defaulted. The engine used to carry the un-collected " +
            "dq slice as a residual balance and book it as a default in the loan's final " +
            "period — 92.04 on this loan, and ~2.9MM on obx2025nqm6-fb6e5a");
    }

    [Theory]
    [InlineData(4.0, 25.0)]
    [InlineData(4.0, 50.0)]
    [InlineData(20.0, 50.0)]
    public void ADelinquentPoolWithZeroCdr_FabricatesNoLoss(double dqPct, double sevPct)
    {
        var periods = Run(OneLoan(term: 60), 0, 0, sevPct, dqPct, advPct: 0.0);

        periods.Sum(p => p.CollateralLoss).Should().Be(0.0,
            $"with CDR = 0 there is nothing for a {sevPct}% severity to apply to. The maturity " +
            "residual used to be booked as a default AND severity-adjusted, so this run " +
            "reported a real credit loss on a scenario with no default assumption at all");
    }

    [Fact]
    public void APoolOfStaggeredMaturities_BooksNoDefaultAtAnyCohortMaturity()
    {
        // Three cohorts, three maturity dates — the shape of obx2025nqm6-fb6e5a,
        // whose defaults appeared at exactly three periods (161 / 341 / 462),
        // each one a maturity cohort.
        var pool = new List<IAsset>
        {
            Frm(1_000_000, 6.0, 60, "C60"),
            Frm(2_000_000, 6.0, 120, "C120"),
            Frm(3_000_000, 6.0, 180, "C180"),
        };

        var periods = Run(pool, 0, 0, 0, dqPct: 4.0, advPct: 0.0);

        var defaultPeriods = periods
            .Select((p, i) => (i, p.DefaultedPrincipal))
            .Where(x => x.DefaultedPrincipal > 0.005)
            .ToList();

        defaultPeriods.Should().BeEmpty(
            "no cohort defaults at its maturity when CDR is zero. Before #4481 this same pool " +
            "booked defaults at periods 60, 120 and 180 — one per maturing cohort");
    }

    // ------------------------------------------------------------ the units

    [Fact]
    public void Advancing_IsAPercent_NotAMultiplier()
    {
        var clean = Run(OneLoan(), 0, 0, 0, dqPct: 0, advPct: 0);
        var full = Run(OneLoan(), 0, 0, 0, dqPct: 5.0, advPct: 100.0);

        full[0].Interest.Should().BeApproximately(clean[0].Interest, 1e-6,
            "`advancing = 100` means 100 PERCENT. It was divided by 1.0 rather than 100, so " +
            "`1 - delAdvInt` became `1 - 100 = -99`: reported interest came out at 5.95x the " +
            "true coupon and the loan paid off at month 333 of 360");
        full.Should().HaveCount(clean.Count,
            "a fully-advanced delinquent loan amortizes on exactly the contractual schedule");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(25.0)]
    [InlineData(50.0)]
    [InlineData(100.0)]
    public void AdvancedPlusUnadvanced_IsTheWholeDelinquentSlice(double advPct)
    {
        const double dqPct = 8.0;
        var periods = Run(OneLoan(), 0, 0, 0, dqPct, advPct);

        // Reporting-only disclosure, but it must at least add up: the advanced and
        // unadvanced halves partition the delinquent slice exactly.
        for (var t = 0; t < Math.Min(12, periods.Count); t++)
        {
            var p = periods[t];
            (p.AdvancedPrincipal + p.UnAdvancedPrincipal).Should().BeApproximately(
                p.ScheduledPrincipal * (dqPct / 100.0), 1e-6,
                $"period {t}: advanced + unadvanced principal must equal the delinquent slice");
            p.AdvancedPrincipal.Should().BeApproximately(
                p.ScheduledPrincipal * (dqPct / 100.0) * (advPct / 100.0), 1e-6,
                $"period {t}: the advanced half is the slice times the advancing RATE");
        }
    }
}
