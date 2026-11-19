using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
///     A lagged recovery on a deal whose losses are realized on liquidation (a mortgage trust's
///     Realized Loss): the loss is struck when the loan is liquidated, not when it defaults.
///
///     With <c>recoveryLag</c> > 0 a period-t default's recovery lands at t + lag. The loss each
///     period used to be read as that period's defaulted principal less that period's recovery, so
///     the default period wrote the notes down by the GROSS default, and the recovery that came in
///     later was paid out as principal with nothing to offset it. On a pool with no
///     overcollateralization the classes lost the gross default instead of the net loss, and the
///     recovered cash outlived the notes (on a deal with a residual class, it went there).
///
///     A consumer trust charges a receivable off when it defaults and counts later recoveries as
///     collections, so the period loss there stays defaulted less recovered: the deal states which
///     (<c>lossesRealizedOnLiquidation</c>), and the last test pins the charge-off reading.
///
///     The pool here: one loan, a single 10% default in the first month at 40% severity, no
///     prepayment, recovered 12 months later. Three sequential classes sized to the pool, no OC.
///     The net loss is 4% of the pool, and only the bottom class should take it.
/// </summary>
public class LaggedRecoveryLossTests
{
    private static readonly DateTime Proj = new(2026, 1, 1);
    private const int Lag = 12;

    private static List<PeriodCashflowDto> Pool(int recoveryLag)
    {
        var request = new CalcCollateralRequest
        {
            Assets = new List<AssetDto>
            {
                new()
                {
                    AssetName = "loan_1", AssetId = "loan_1", InterestRateType = "FRM",
                    OriginalDate = new DateTime(2025, 1, 1), OriginalBalance = 10_000_000,
                    CurrentBalance = 10_000_000, OriginalInterestRate = 6.0, CurrentInterestRate = 6.0,
                    OriginalAmortizationTerm = 120, ServiceFee = 0.0, GroupNum = "1", IsIO = false
                }
            },
            ProjectionDate = Proj,
            Assumptions = new AssumptionsDto
            {
                Cpr = 0, PrepaymentType = "CPR", DefaultType = "MDR",
                CdrVector = new[] { 10.0, 0.0 }, Severity = 40.0, RecoveryLag = recoveryLag
            }
        };
        var action = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance)
            .Calculate(request);
        return action.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<CalcCollateralResponse>().Subject.Cashflows
            .OrderBy(c => c.Period).ToList();
    }

    private static PayableStructureDto Seq(params string[] t) => new() { Type = "SEQ", Tranches = t.ToList() };

    private static WaterfallResponse Waterfall(List<PeriodCashflowDto> pool, string? writedownSize = null,
        bool onLiquidation = true)
    {
        var begin = pool[0].BeginBalance;
        TrancheDto Note(string name, double share, int order) => new()
        {
            TrancheName = name, OriginalBalance = begin * share, TrancheType = "Offered",
            CashflowType = "PI", CouponType = "Fixed", FixedCoupon = 5.0,
            SubordinationOrder = order, FirstPayDate = pool[0].CashflowDate
        };
        var deal = new DealDto
        {
            DealName = "LAGGED_RECOVERY_TEST",
            WaterfallType = "Sequential",
            Tranches = new List<TrancheDto> { Note("A", 0.70, 1), Note("B", 0.20, 2), Note("C", 0.10, 3) },
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = new List<string>
                {
                    "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY",
                    "WRITEDOWN"
                },
                Steps = new List<WaterfallStepDto>
                {
                    new() { Type = "INTEREST", Structure = Seq("A", "B", "C") },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A", "B", "C") },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A", "B", "C") },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A", "B", "C") },
                    new() { Type = "WRITEDOWN", Structure = Seq("C", "B", "A"), Size = writedownSize }
                },
                LossesRealizedOnLiquidation = onLiquidation
            }
        };
        var action = new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(
            new WaterfallRequest { Deal = deal, CollateralCashflows = pool, ProjectionDate = Proj });
        if (action.Result is BadRequestObjectResult bad)
            throw new Xunit.Sdk.XunitException($"Waterfall 400: {bad.Value}");
        return action.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<WaterfallResponse>().Subject;
    }

    private static double NetLoss(List<PeriodCashflowDto> pool) =>
        pool.Sum(c => c.DefaultedPrincipal) - pool.Sum(c => c.RecoveryPrincipal);

    private static double Writedown(WaterfallResponse r, string? tranche = null) =>
        r.TrancheCashflows.Where(kv => tranche == null || kv.Key == tranche)
            .SelectMany(kv => kv.Value).Sum(c => c.Writedown);

    private static double NotesPrincipal(WaterfallResponse r) =>
        r.TrancheCashflows.Values.SelectMany(c => c).Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal);

    private static double PoolPrincipal(List<PeriodCashflowDto> pool) =>
        pool.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal + c.RecoveryPrincipal);

    [Fact]
    public void The_classes_are_written_down_by_the_net_loss_not_the_gross_default()
    {
        var pool = Pool(Lag);
        var defaulted = pool.Sum(c => c.DefaultedPrincipal);
        defaulted.Should().BeGreaterThan(900_000, "the shock defaults about 10% of the pool");
        pool.Sum(c => c.RecoveryPrincipal).Should().BeApproximately(0.6 * defaulted, 1.0,
            "the recovery lands inside the projection, so the whole net loss is realized in it");

        var wf = Waterfall(pool);

        Writedown(wf).Should().BeApproximately(NetLoss(pool), 1.0,
            "on a pool with no overcollateralization the classes lose what the collateral lost");
        Writedown(wf, "A").Should().Be(0);
        Writedown(wf, "B").Should().Be(0, "the net loss is 4% of the pool and C is 10% of it");
    }

    [Fact]
    public void A_lag_moves_when_the_loss_is_taken_not_how_much()
    {
        Writedown(Waterfall(Pool(Lag))).Should().BeApproximately(Writedown(Waterfall(Pool(0))), 1.0);
    }

    [Fact]
    public void Every_dollar_of_collateral_principal_reaches_the_notes_and_none_beyond_it()
    {
        var pool = Pool(Lag);
        var wf = Waterfall(pool);

        // Before the fix the notes were written down by the gross default and the recovery came in
        // after they had nothing left to be paid: the notes received less principal than the pool
        // produced, by the recovered amount.
        NotesPrincipal(wf).Should().BeApproximately(PoolPrincipal(pool), 1.0);
        (NotesPrincipal(wf) + Writedown(wf)).Should().BeApproximately(pool[0].BeginBalance, 1.0);
    }

    [Fact]
    public void The_loss_is_realized_in_the_month_the_loan_is_liquidated()
    {
        var pool = Pool(Lag);
        var wf = Waterfall(pool);
        var c = wf.TrancheCashflows["C"];

        c.Take(Lag).Sum(r => r.Writedown).Should().Be(0,
            "the defaulted loan is awaiting liquidation: no loss is realized yet");
        c[Lag].Writedown.Should().BeApproximately(NetLoss(pool), 1.0);
    }

    [Fact]
    public void The_stream_reports_the_principal_liquidated_in_the_month_its_recovery_arrives()
    {
        var pool = Pool(Lag);
        var defaulted = pool.Sum(c => c.DefaultedPrincipal);

        pool[0].LiquidatedPrincipal.Should().Be(0);
        pool[Lag].LiquidatedPrincipal.Should().BeApproximately(defaulted, 0.01);
        pool.Sum(c => c.LiquidatedPrincipal ?? 0).Should().BeApproximately(defaulted, 0.01);
        pool[Lag].RecoveryPrincipal.Should().BeApproximately(0.6 * defaulted, 0.01);
    }

    [Fact]
    public void Without_a_lag_the_stream_and_the_writedowns_are_as_before()
    {
        var pool = Pool(0);
        foreach (var c in pool)
            c.LiquidatedPrincipal.Should().Be(c.DefaultedPrincipal);
        Writedown(Waterfall(pool), "C").Should().BeApproximately(NetLoss(pool), 1.0);
        Waterfall(pool).TrancheCashflows["C"][0].Writedown.Should().BeApproximately(NetLoss(pool), 1.0,
            "a recovery in the month of default realizes the loss that month");
    }

    [Fact]
    public void Sized_over_the_pool_the_classes_still_lose_only_the_net_loss()
    {
        var pool = Pool(Lag);
        var wf = Waterfall(pool, "overPool");

        // The defaulted loan is still the trust's until it is liquidated: the notes do not stand
        // above the pool by it in the meantime.
        Writedown(wf).Should().BeApproximately(NetLoss(pool), 1.0);
        wf.TrancheCashflows["C"].Take(Lag).Sum(r => r.Writedown).Should().Be(0);
        NotesPrincipal(wf).Should().BeApproximately(PoolPrincipal(pool), 1.0);
    }

    [Fact]
    public void A_deal_that_does_not_state_it_reads_the_loss_as_a_charge_off()
    {
        // The default month takes the gross default, and the recovery a year later is collections.
        var pool = Pool(Lag);
        var wf = Waterfall(pool, onLiquidation: false);
        wf.TrancheCashflows["C"][0].Writedown.Should().BeApproximately(pool.Sum(c => c.DefaultedPrincipal), 1.0);
    }

    [Fact]
    public void A_posted_stream_that_states_no_liquidation_is_read_as_liquidated_on_default()
    {
        // A caller that posts its own collateral and does not send liquidatedPrincipal is read as
        // it always was: what defaulted in a period is liquidated in it.
        var pool = Pool(Lag);
        foreach (var c in pool)
            c.LiquidatedPrincipal = null;
        var wf = Waterfall(pool);
        wf.TrancheCashflows["C"][0].Writedown.Should().BeApproximately(pool.Sum(c => c.DefaultedPrincipal), 1.0);
    }
}
