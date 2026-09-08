using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// graam-harmony#4883 — a FUNDED residual was paid its entitlement TWICE.
///
/// The principal steps measure how much they just allocated as a balance delta:
/// <c>before = dynGroup.Balance(); PaySp(...); paid = before - dynGroup.Balance();</c>.
/// <c>Balance()</c> sums <c>DealClasses</c>, which deliberately excludes a
/// <c>CouponType.Residual</c> class as non-economic — correct for OC, wrong as a measurement.
/// Principal paid to a funded residual registered as ZERO, so the remainder stayed inflated by
/// exactly that payment and <c>CreditResidual</c> credited the same money to the same class a
/// second time.
///
/// Measured on AMMC CLO 33 at 2 CDR / 20 CPR / 40 severity: the Subordinated notes took
/// 55,864,986 against a 27,932,493 entitlement — exactly 2x — and the stack was paid 27,932,493
/// more principal than the collateral ever produced.
///
/// The guardrail below is the one that matters: a genuinely NOTIONAL Class R (InterestOnly,
/// balance reset to the pool each period) has no principal to pay down, is still excluded, and
/// must still receive the leftover catch-all. `CreditResidual` exists for that case and this fix
/// must not disable it.
/// </summary>
public class FundedResidualDoublePayTests
{
    private static readonly DateTime FirstPayDate = TestConstants.DefaultFirstPayDate;

    private const double SeniorFace = 60_000_000;
    private const double ResidualFace = 40_000_000;
    private const double PoolBalance = SeniorFace + ResidualFace;
    private const int Periods = 6;

    /// <summary>A pool that fully amortizes, so there is more than enough principal to expose
    /// an over-payment: the senior retires and the residual is next in the cascade.</summary>
    private static CollateralCashflows AmortizingPool()
    {
        var builder = new TestCollateralBuilder().WithGroupNum("1");
        var per = PoolBalance / Periods;
        for (var i = 0; i < Periods; i++)
            builder.WithPeriod(date: FirstPayDate.AddMonths(i),
                beginBalance: PoolBalance - per * i,
                scheduledPrincipal: per, unscheduledPrincipal: 0, interest: 500_000);
        return builder.Build();
    }

    private static (double principal, double face) Residual(string residualCashflowType)
    {
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", SeniorFace, 6.0, subOrder: 0)
            .WithTranche("R", ResidualFace, 0.0, subOrder: 1,
                cashflowType: residualCashflowType, couponType: "Residual")
            .WithSequentialWaterfall("A", "R")
            .BuildAndRun(AmortizingPool());

        var match = cf.TrancheCashflows.FirstOrDefault(t => t.Key.TrancheName == "R");
        var flows = match.Value?.Cashflows.Values ?? Enumerable.Empty<TrancheCashflow>();
        var paid = flows.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal);
        return (paid, ResidualFace);
    }

    [Fact]
    public void AFundedResidualIsNotPaidTwice()
    {
        var (paid, face) = Residual(residualCashflowType: "PI");

        // The invariant, not a golden number: a FUNDED class cannot be paid more principal than
        // its face. Before the fix this came back at ~2x.
        paid.Should().BeLessThanOrEqualTo(face + 1.0,
            "a funded residual's principal is capped by its balance, like any other funded class");
        paid.Should().BeGreaterThan(0, "it is still next in the cascade and must be paid");
    }

    [Fact]
    public void TheStackIsNotPaidMorePrincipalThanTheCollateralProduced()
    {
        var (_, cf) = new TestDealBuilder()
            .WithTranche("A", SeniorFace, 6.0, subOrder: 0)
            .WithTranche("R", ResidualFace, 0.0, subOrder: 1,
                cashflowType: "PI", couponType: "Residual")
            .WithSequentialWaterfall("A", "R")
            .BuildAndRun(AmortizingPool());

        var paidToStack = cf.TrancheCashflows
            .SelectMany(t => t.Value.Cashflows.Values)
            .Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal);

        // Conservation. This is the invariant the AMMC run broke by $27.9M, and it holds for
        // any deal — the waterfall cannot distribute principal that does not exist.
        paidToStack.Should().BeLessThanOrEqualTo(PoolBalance + 1.0,
            "the stack cannot be paid more principal than the collateral produced");
    }

    [Fact]
    public void ANotionalResidualStillReceivesTheLeftoverCatchAll()
    {
        // The guardrail. A genuine REMIC Class R is InterestOnly — its balance is a pool notional
        // reset every period, it has no principal to pay down, and `CreditResidual` is exactly
        // how it receives anything left unallocated. That path must survive this fix.
        var (paid, _) = Residual(residualCashflowType: "IO");

        paid.Should().BeGreaterThan(0,
            "a notional Class R is not in the principal cascade, so the catch-all is the ONLY "
            + "way it ever sees principal — disabling that would be a different bug");
    }
}
