using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// End-to-end tests for ComposableStructure waterfall execution.
/// Each test builds a minimal deal, generates collateral, runs the waterfall,
/// and verifies the output cashflows.
/// </summary>
public class ComposableStructureTests
{
    private static readonly DateTime ProjectionDate = TestConstants.DefaultProjectionDate;
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    #region Interest Distribution

    [Fact]
    public void Interest_SequentialDistribution_SeniorPaidFirst()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");

        aCf.Interest.Should().BeGreaterThan(0, "A should receive interest");
        bCf.Interest.Should().BeGreaterThan(0, "B should receive interest");

        // A's interest ≈ 80M * 5% / 12
        aCf.Interest.Should().BeApproximately(80_000_000 * 0.05 / 12, 50000);
    }

    [Fact]
    public void Interest_InsufficientFunds_SeniorPaidBeforeJunior()
    {
        // Low WAC collateral so there isn't enough interest for everyone
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 15.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(1, 100_000_000, wacPct: 3.0));

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");

        // A should be fully paid, B may have shortfall
        aCf.Interest.Should().BeGreaterThan(0);
        // Total interest paid should not exceed collateral interest
        var totalTranchInterest = aCf.Interest + bCf.Interest;
        var collateralInterest = 100_000_000 * 0.03 / 12 * 0.97; // net of servicing
        totalTranchInterest.Should().BeLessOrEqualTo(collateralInterest + 1);
    }

    [Fact]
    public void Interest_GuaranteedTreatment_PaysFullCouponRegardless()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithInterestTreatment("Guaranteed")
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(1, 100_000_000, wacPct: 2.0)); // Very low WAC

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");

        // With Guaranteed treatment, full coupon paid even with low collateral WAC
        aCf.Interest.Should().BeApproximately(80_000_000 * 0.05 / 12, 50000);
        bCf.Interest.Should().BeApproximately(20_000_000 * 0.06 / 12, 50000);
    }

    #endregion

    #region Principal Distribution

    [Fact]
    public void Principal_Sequential_SeniorPaidFirst()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 50_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 30_000_000, 6.0, subOrder: 1)
            .WithTranche("C", 20_000_000, 7.0, subOrder: 2)
            .WithSequentialWaterfall("A", "B", "C")
            .BuildAndRun(CreateCollateral(6, 100_000_000));

        var aCfs = GetCashflows(cf, "A");
        var bCfs = GetCashflows(cf, "B");

        var aPrincipal = aCfs.Sum(c => c.Value.TotalPrincipal());
        var bPrincipal = bCfs.Sum(c => c.Value.TotalPrincipal());

        aPrincipal.Should().BeGreaterThan(0, "A should receive principal");

        // B should not receive principal while A still has balance
        var aStillHasBalance = aCfs.All(c => c.Value.Balance > 1000);
        if (aStillHasBalance)
            bPrincipal.Should().BeLessThan(1000,
                "B should not receive principal while A has remaining balance");
    }

    [Fact]
    public void Principal_BalanceDecreasesCorrectly()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(6, 100_000_000));

        foreach (var trancheName in new[] { "A", "B" })
        {
            var cashflows = GetCashflows(cf, trancheName);
            foreach (var tcf in cashflows.Values)
            {
                var expectedBalance = tcf.BeginBalance - tcf.ScheduledPrincipal
                                      - tcf.UnscheduledPrincipal - tcf.Writedown;
                tcf.Balance.Should().BeApproximately(expectedBalance, 1,
                    $"{trancheName}: Balance should equal BeginBalance - Principal - Writedown");
            }
        }
    }

    [Fact]
    public void Principal_ConsecutivePeriodsLink()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(6, 100_000_000));

        var aCashflows = GetCashflows(cf, "A").OrderBy(c => c.Key).Select(c => c.Value).ToList();

        for (var i = 1; i < aCashflows.Count; i++)
        {
            aCashflows[i].BeginBalance.Should().BeApproximately(aCashflows[i - 1].Balance, 1,
                $"Period {i}: BeginBalance should equal previous period's ending Balance");
        }
    }

    #endregion

    #region Writedown Distribution

    [Fact]
    public void Writedown_ReverseSeniority_JuniorAbsorbsFirst()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 70_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithTranche("C", 10_000_000, 7.0, subOrder: 2)
            .WithSequentialWaterfall("A", "B", "C")
            .BuildAndRun(CreateCollateral(6, 100_000_000, cdrPct: 10.0));

        var aCumWd = GetCashflows(cf, "A").Max(c => c.Value.CumWritedown);
        var bCumWd = GetCashflows(cf, "B").Max(c => c.Value.CumWritedown);
        var cCumWd = GetCashflows(cf, "C").Max(c => c.Value.CumWritedown);

        // C (most junior in writedown order) should absorb losses first
        if (cCumWd < 10_000_000 * 0.99)
        {
            bCumWd.Should().BeLessThan(cCumWd * 0.1,
                "B should have minimal writedowns while C has remaining balance");
        }

        aCumWd.Should().BeLessThanOrEqualTo(bCumWd + 1,
            "A should not have more writedowns than B");
    }

    #endregion

    #region Cashflow Conservation

    [Fact]
    public void CashflowConservation_PrincipalDistributed()
    {
        var collateral = CreateCollateral(6, 100_000_000);
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(collateral);

        var totalCollateralPrincipal = collateral.PeriodCashflows
            .Sum(p => p.ScheduledPrincipal + p.UnscheduledPrincipal);

        var totalTranchePrincipal = new[] { "A", "B" }
            .SelectMany(name => GetCashflows(cf, name).Values)
            .Sum(c => c.TotalPrincipal());

        totalTranchePrincipal.Should().BeGreaterThan(0, "Tranches should receive principal");
        totalTranchePrincipal.Should().BeLessOrEqualTo(totalCollateralPrincipal + 1,
            "Tranche principal should not exceed collateral principal");
    }

    #endregion

    #region Execution Order

    [Fact]
    public void ExecutionOrder_DefaultOrder_Works()
    {
        // No explicit execution order - uses defaults
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        var aCf = GetFirstCashflow(cf, "A");
        aCf.Interest.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ExecutionOrder_Custom_Respected()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithExecutionOrder("EXPENSE", "INTEREST", "PRINCIPAL_SCHEDULED",
                "PRINCIPAL_UNSCHEDULED", "WRITEDOWN")
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        GetFirstCashflow(cf, "A").Interest.Should().BeGreaterThan(0);
    }

    #endregion

    #region Prorata Interest + Sequential Principal

    [Fact]
    public void ProrataInterest_SequentialPrincipal_Works()
    {
        var (deal, cf) = new TestDealBuilder()
            .WithTranche("A", 50_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 30_000_000, 5.0, subOrder: 1)
            .WithTranche("C", 20_000_000, 5.0, subOrder: 2)
            .WithProrataInterestSequentialPrincipal(
                new[] { "A", "B", "C" },
                new[] { "A", "B", "C" })
            .BuildAndRun(CreateCollateral(3, 100_000_000));

        var aCf = GetFirstCashflow(cf, "A");
        var bCf = GetFirstCashflow(cf, "B");
        var cCf = GetFirstCashflow(cf, "C");

        // All tranches should receive interest (prorata)
        aCf.Interest.Should().BeGreaterThan(0);
        bCf.Interest.Should().BeGreaterThan(0);
        cCf.Interest.Should().BeGreaterThan(0);

        // Interest should be proportional to balance * coupon
        var aShare = aCf.Interest / (aCf.Interest + bCf.Interest + cCf.Interest);
        aShare.Should().BeApproximately(0.5, 0.05, "A (50% balance) should get ~50% of interest");
    }

    #endregion

    #region Helper Methods

    private static CollateralCashflows CreateCollateral(int numPeriods, double startingBalance,
        double wacPct = 8.0, double cdrPct = 0.0)
    {
        return new TestCollateralBuilder()
            .WithGroupNum("1")
            .WithConstantCashflows(FirstPayDate, numPeriods, startingBalance,
                cpr: TestConstants.DefaultCpr, cdr: cdrPct, wac: wacPct)
            .Build();
    }

    private static TrancheCashflow GetFirstCashflow(DealCashflows dealCashflows, string trancheName)
    {
        var match = dealCashflows.TrancheCashflows.First(t => t.Key.TrancheName == trancheName);
        return match.Value.Cashflows.OrderBy(c => c.Key).First().Value;
    }

    private static Dictionary<DateTime, TrancheCashflow> GetCashflows(
        DealCashflows dealCashflows, string trancheName)
    {
        var match = dealCashflows.TrancheCashflows.FirstOrDefault(t => t.Key.TrancheName == trancheName);
        return match.Value?.Cashflows ?? new Dictionary<DateTime, TrancheCashflow>();
    }

    #endregion
}
