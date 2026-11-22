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

    private static List<PeriodCashflowDto> Pool(int recoveryLag, double[]? mdr = null, double severity = 40.0)
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
                CdrVector = mdr ?? new[] { 10.0, 0.0 }, Severity = severity, RecoveryLag = recoveryLag
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
        bool onLiquidation = true, Action<DealDto>? edit = null)
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
        edit?.Invoke(deal);
        var action = new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(
            new WaterfallRequest { Deal = deal, CollateralCashflows = pool, ProjectionDate = Proj });
        if (action.Result is BadRequestObjectResult bad)
            throw new Xunit.Sdk.XunitException($"Waterfall 400: {bad.Value}");
        return action.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<WaterfallResponse>().Subject;
    }

    /// <summary>
    ///     The steps a deal states when its documents pay realized losses out of excess and reimburse
    ///     earlier write-downs (graam-harmony#6199): a zero-target turbo ahead of an overPool
    ///     write-down, WRITEUP after it, and the excess-spread strip paid last.
    /// </summary>
    private static WaterfallResponse ExcessCoversLosses(List<PeriodCashflowDto> pool, bool onLiquidation = true,
        double? ocTargetPct = null)
    {
        var begin = pool[0].BeginBalance;
        var first = pool[0].CashflowDate;
        TrancheDto Note(string name, double share, int order) => new()
        {
            TrancheName = name, OriginalBalance = begin * share, TrancheType = "Offered",
            // 2% against the pool's 6%: interest on a loan awaiting liquidation is not advanced, and
            // the excess has to outlast two of them.
            CashflowType = "PI", CouponType = "Fixed", FixedCoupon = 2.0,
            SubordinationOrder = order, FirstPayDate = first, PayFrequency = 12, PayDay = first.Day
        };
        var deal = new DealDto
        {
            DealName = "LAGGED_RECOVERY_EXCESS_TEST",
            WaterfallType = "ComposableStructure",
            ClosingDate = Proj,
            Tranches = new List<TrancheDto>
            {
                Note("A", 0.70, 1), Note("B", 0.20, 2), Note("C", 0.10, 3),
                new()
                {
                    TrancheName = "XS", OriginalBalance = begin, TrancheType = "Offered", CashflowType = "IO",
                    CouponType = "ExcessInterest", FixedCoupon = 0.0, ClassReference = "XS",
                    SubordinationOrder = 4, FirstPayDate = first, PayFrequency = 12, PayDay = first.Day
                }
            },
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = ocTargetPct == null
                    ? new List<string>
                    {
                        "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY",
                        "EXCESS_TURBO", "WRITEDOWN", "WRITEUP", "EXCESS_RELEASE"
                    }
                    : new List<string>
                    {
                        "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY",
                        "WRITEDOWN", "EXCESS_TURBO", "EXCESS_RELEASE"
                    },
                Steps = new List<WaterfallStepDto>
                {
                    new() { Type = "INTEREST", Structure = Seq("A", "B", "C") },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A", "B", "C") },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A", "B", "C") },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A", "B", "C") },
                    ocTargetPct == null
                        ? new WaterfallStepDto { Type = "EXCESS_TURBO", Target = 0 }
                        : new WaterfallStepDto
                        {
                            Type = "EXCESS_TURBO", Structure = Seq("A", "B", "C"),
                            OcTarget = new OcTargetDto { TargetPct = ocTargetPct.Value }
                        },
                    new()
                    {
                        Type = "WRITEDOWN", Structure = Seq("C", "B", "A"),
                        Size = ocTargetPct == null ? "overPool" : null
                    },
                    new() { Type = "WRITEUP" },
                    new() { Type = "EXCESS_RELEASE", Structure = Seq("XS") }
                },
                LossesRealizedOnLiquidation = onLiquidation
            }
        };
        if (ocTargetPct != null)
        {
            deal.UnifiedWaterfall.Steps.RemoveAll(st => st.Type == "WRITEUP");
            // The certificate: its balance is the overcollateralization, the pool less the notes.
            deal.Tranches.Add(new TrancheDto
            {
                TrancheName = "CERT", OriginalBalance = 0, TrancheType = "Certificate", CashflowType = "PI",
                CouponType = "None", FixedCoupon = 0.0, SubordinationOrder = 5, FirstPayDate = first,
                PayFrequency = 12, PayDay = first.Day
            });
        }
        var action = new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(
            new WaterfallRequest { Deal = deal, CollateralCashflows = pool, ProjectionDate = Proj });
        if (action.Result is BadRequestObjectResult bad)
            throw new Xunit.Sdk.XunitException($"Waterfall 400: {bad.Value}");
        return action.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<WaterfallResponse>().Subject;
    }

    /// <summary>Defaulted principal awaiting liquidation at the end of each period.</summary>
    private static double Pending(List<PeriodCashflowDto> pool, int i) =>
        pool.Take(i + 1).Sum(c => c.DefaultedPrincipal - (c.LiquidatedPrincipal ?? c.DefaultedPrincipal));

    private static double NotesAt(WaterfallResponse r, int i) =>
        Notes.Sum(n => r.TrancheCashflows[n][i].Balance);

    private static double NetLoss(List<PeriodCashflowDto> pool) =>
        pool.Sum(c => c.DefaultedPrincipal) - pool.Sum(c => c.RecoveryPrincipal);

    private static double Writedown(WaterfallResponse r, string? tranche = null) =>
        r.TrancheCashflows.Where(kv => tranche == null || kv.Key == tranche)
            .SelectMany(kv => kv.Value).Sum(c => c.Writedown);

    private static readonly string[] Notes = { "A", "B", "C" };

    private static double NotesPrincipal(WaterfallResponse r) =>
        Notes.SelectMany(n => r.TrancheCashflows[n]).Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal);

    private static double NotesPrincipal(WaterfallResponse r, int i) =>
        Notes.Sum(n => r.TrancheCashflows[n][i].ScheduledPrincipal + r.TrancheCashflows[n][i].UnscheduledPrincipal);

    private static double PoolPrincipal(List<PeriodCashflowDto> pool) =>
        pool.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal + c.RecoveryPrincipal);

    private static double PoolPrincipal(List<PeriodCashflowDto> pool, int i) =>
        pool[i].ScheduledPrincipal + pool[i].UnscheduledPrincipal + pool[i].RecoveryPrincipal;

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
    public void Excess_does_not_pay_a_loss_before_it_is_realized()
    {
        // The zero-target turbo pays the notes down to the pool, and the pool still holds the loan
        // awaiting liquidation: until it is liquidated there is no loss for excess to cover, and the
        // strip keeps the excess. Measured against the performing balance alone, the turbo paid the
        // gross default out of excess for a year, and the recovery then paid the notes again.
        var pool = Pool(Lag);
        var wf = ExcessCoversLosses(pool);
        var xs = wf.TrancheCashflows["XS"];

        Pending(pool, 0).Should().BeGreaterThan(900_000, "the fixture: the default awaits liquidation");
        for (var i = 0; i < Lag; i++)
        {
            xs[i].Interest.Should().BeGreaterThan(1_000, $"period {i + 1}: the strip is paid the excess");
            NotesPrincipal(wf, i).Should().BeApproximately(PoolPrincipal(pool, i), 0.01,
                $"period {i + 1}: the notes are paid what the pool paid and nothing out of excess");
        }
        wf.TrancheCashflows["C"].Take(Lag).Sum(c => c.Writedown).Should().Be(0);
    }

    [Fact]
    public void The_notes_stand_level_with_the_pool_and_the_loans_awaiting_liquidation()
    {
        // Every step that measures the notes against the pool (the turbo, the write-down, WRITEUP)
        // reads the same pool, so the notes end each period level with it.
        var pool = Pool(Lag);
        var wf = ExcessCoversLosses(pool);

        wf.TrancheCashflows["C"][Lag].Writedown.Should().BeGreaterThan(100_000,
            "the fixture: the realized loss is more than a month's excess, so some is written down");
        wf.TrancheCashflows["C"].Last().CumWritedown.Should().BeLessThan(
            wf.TrancheCashflows["C"][Lag].CumWritedown - 1_000, "the fixture: later excess writes it back up");
        for (var i = 0; i < pool.Count; i++)
            NotesAt(wf, i).Should().BeApproximately(pool[i].Balance + Pending(pool, i), 0.01, $"period {i + 1}");
    }

    [Fact]
    public void A_write_down_is_written_back_up_while_another_loan_awaits_liquidation()
    {
        // Two defaults, three months apart: the first is liquidated (and written down) while the
        // second still awaits liquidation. WRITEUP restores the first out of excess against the same
        // pool the turbo paid down to; read against the performing balance alone, it restored
        // nothing until the second loan was liquidated, and the excess the turbo had paid stayed off
        // the classes.
        var pool = Pool(Lag, new[] { 10.0, 0.0, 0.0, 10.0, 0.0 });
        var wf = ExcessCoversLosses(pool);
        var c = wf.TrancheCashflows["C"];

        c[Lag].Writedown.Should().BeGreaterThan(100_000, "the fixture: the first loss is written down");
        Pending(pool, Lag + 1).Should().BeGreaterThan(500_000, "the fixture: the second default still awaits liquidation");
        c[Lag + 1].CumWritedown.Should().BeLessThan(c[Lag].CumWritedown - 1_000,
            "the month after, excess writes the first loss back up");
        for (var i = 0; i < pool.Count; i++)
            NotesAt(wf, i).Should().BeApproximately(pool[i].Balance + Pending(pool, i), 0.01, $"period {i + 1}");
    }

    [Fact]
    public void An_OC_target_is_measured_against_the_pool_and_the_loans_awaiting_liquidation()
    {
        // A small OC target, turbo paid out of excess. The loan awaiting liquidation is still the
        // trust's, so it is no OC shortfall: the turbo builds the target and no more, and the strip
        // keeps the rest. Measured against the performing balance alone, the waiting loan read as
        // a shortfall and the turbo spent the excess on it a year before any loss was realized.
        const double target = 0.002;
        var pool = Pool(Lag);
        var wf = ExcessCoversLosses(pool, ocTargetPct: target);
        var cert = wf.TrancheCashflows["CERT"];

        for (var i = 0; i < Lag; i++)
        {
            var poolWithWaiting = pool[i].Balance + Pending(pool, i);
            var oc = poolWithWaiting - NotesAt(wf, i);
            oc.Should().BeApproximately(target * poolWithWaiting, 1.0, $"period {i + 1}: the turbo stops at the target");
            cert[i].Balance.Should().BeApproximately(oc, 1.0, $"period {i + 1}: the certificate is the OC");
        }
        wf.TrancheCashflows["XS"].Take(Lag).Should().OnlyContain(c => c.Interest > 1_000,
            "the strip is paid the excess the target does not need while the loan waits");
        wf.TrancheCashflows["C"].Take(Lag).Sum(c => c.Writedown).Should().Be(0);
    }

    [Fact]
    public void A_loan_liquidated_with_nothing_recovered_is_still_written_down()
    {
        // At 100% severity the liquidation month carries no recovery, no balance and no interest:
        // only the loan leaving the loans awaiting liquidation. Cut off as an empty period, its
        // loss was never realized and the notes stood over a pool that no longer existed.
        var pool = Pool(Lag, new[] { 100.0, 0.0 }, severity: 100.0);
        pool.Should().HaveCountGreaterThan(Lag, "the stream runs to the month the loan is liquidated");
        pool[Lag].LiquidatedPrincipal.Should().BeApproximately(pool[0].BeginBalance, 0.01);

        var wf = Waterfall(pool);
        Writedown(wf).Should().BeApproximately(pool[0].BeginBalance, 1.0);
        Notes.Sum(n => wf.TrancheCashflows[n].Last().Balance).Should().BeApproximately(0, 0.01);
        wf.TrancheCashflows["C"].Take(Lag).Sum(c => c.Writedown).Should().Be(0);
    }

    [Fact]
    public void A_late_default_with_nothing_recovered_is_realized_in_the_stream()
    {
        // Defaults late in the loan's life, liquidated a year later with nothing recovered, after
        // the loan's own term has run out.
        var mdr = Enumerable.Repeat(0.0, 100).Append(20.0).Append(0.0).ToArray();
        var pool = Pool(Lag, mdr, severity: 100.0);
        var defaulted = pool.Sum(c => c.DefaultedPrincipal);
        defaulted.Should().BeGreaterThan(100_000, "the fixture: something defaults in month 101");

        Writedown(Waterfall(pool)).Should().BeApproximately(defaulted, 1.0);
        Writedown(Waterfall(pool)).Should().BeApproximately(Writedown(Waterfall(pool, onLiquidation: false)), 1.0,
            "nothing is recovered, so the two readings lose the same, only later");
    }

    [Fact]
    public void A_posted_stream_that_ends_before_the_liquidation_realizes_the_loss_on_its_last_row()
    {
        // A caller posts the first six months of a lagged stream: the loan is still awaiting
        // liquidation when the collateral ends. It is liquidated there, with nothing recovered in
        // the stream, as the asset engine liquidates a default whose recovery is past its horizon.
        var pool = Pool(Lag).Take(6).ToList();
        var defaulted = pool.Sum(c => c.DefaultedPrincipal);
        Pending(pool, pool.Count - 1).Should().BeApproximately(defaulted, 0.01, "the fixture");

        var wf = Waterfall(pool);
        Writedown(wf).Should().BeApproximately(defaulted, 1.0);
        wf.TrancheCashflows["C"].Take(pool.Count - 1).Sum(c => c.Writedown).Should().Be(0);
    }

    [Fact]
    public void A_dropped_month_s_default_is_still_awaiting_liquidation()
    {
        // The notes first pay a month after the collateral starts, and that first month is
        // dropped. A loan that defaulted in it is still the trust's until it is liquidated: the
        // notes are not written down by it while it waits, and when it is liquidated they lose the
        // net loss. Dropped with its month, the waiting loan was taken out of the pool twice.
        var pool = Pool(Lag);
        void DropFirstMonth(DealDto deal)
        {
            deal.FirstPeriodCollateralPolicy = "Drop";
            deal.UnifiedWaterfall!.WriteNotesDownToPool = true;
            foreach (var t in deal.Tranches)
                t.FirstPayDate = pool[1].CashflowDate;
        }

        var wf = Waterfall(pool, edit: DropFirstMonth);
        var c = wf.TrancheCashflows["C"];
        var droppedPrincipal = pool[0].ScheduledPrincipal;

        // What the notes lose over the run: the dropped month's principal, which they were never
        // paid, and the net loss on the loan, realized when it is liquidated.
        Writedown(wf).Should().BeApproximately(droppedPrincipal + NetLoss(pool), 1.0);
        c[Lag - 1].Writedown.Should().BeApproximately(NetLoss(pool), 1.0,
            "the loss is realized in the month the loan is liquidated (the rows start a month late)");
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
