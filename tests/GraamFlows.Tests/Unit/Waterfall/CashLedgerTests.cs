using System.Text.Json;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// The waterfall carries the period's interest and principal in running totals, and each step takes
/// what it reckons it paid off them. Nothing compared that with what the classes were paid. Measured
/// on stored deals, four things went unseen: interest left over with no class to take it; the
/// built-in excess-spread rule taking excess off the strip for a loss after the notes had retired;
/// a release step with no one to release to; and coupons paid that the pool never collected
/// (guaranteed interest, by design). A run can now ask for the ledger and see each of them. It only
/// reads: the cashflows are the same with it on or off.
/// </summary>
public class CashLedgerTests
{
    private static readonly DateTime FirstPayDate = new(2026, 2, 25);
    private const double Pool = 100_000_000;
    private const double Wac = 9.0;

    // ---- It only reads --------------------------------------------------------------------------

    [Fact]
    public void Unasked_the_response_carries_no_ledger()
    {
        Run(Deal(WithStrip()), ledger: false).CashLedger.Should().BeNull();
    }

    [Fact]
    public void Asked_for_the_cashflows_are_the_same_to_the_byte()
    {
        var pool = Collateral(loss: 600_000);
        var off = Run(Deal(WithStrip(), pool), ledger: false);
        var on = Run(Deal(WithStrip(), pool), ledger: true);

        on.CashLedger.Should().NotBeNull();
        JsonSerializer.Serialize(on.TrancheCashflows).Should().Be(JsonSerializer.Serialize(off.TrancheCashflows));
    }

    // ---- A deal that pays out what it collects --------------------------------------------------

    [Fact]
    public void A_deal_that_pays_out_what_it_collects_balances_at_every_step()
    {
        // Scheduled, prepaid and recovery principal all move, so each running total is exercised.
        var pool = Collateral(loss: 600_000, prepaid: 400_000, recoveryRate: 0.5);
        var ledger = Run(Deal(WithStrip(), pool)).CashLedger!;

        var collected = CashIn(pool);
        ledger.Collected.Should().BeApproximately(collected, 0.01);
        ledger.Steps.Should().OnlyContain(s => s.Periods == pool.Count);
        ledger.Steps.Single(s => s.Step == "PRINCIPAL_UNSCHEDULED").Paid.Should().BeApproximately(12 * 400_000, 0.01);
        ledger.Steps.Single(s => s.Step == "PRINCIPAL_RECOVERY").Paid.Should().BeApproximately(12 * 300_000, 0.01);
        ledger.Steps.Should().OnlyContain(s => s.TakenNotPaid < 0.01 && s.PaidNotTaken < 0.01);
        ledger.Steps.Sum(s => s.Paid).Should().BeApproximately(collected, 0.05, "every dollar collected is paid to a class");
        (ledger.LeftInterest + ledger.LeftPrincipal + ledger.LeftPrincipalBesideCertificate).Should().BeApproximately(0, 0.01);
        ledger.PeriodsLeft.Should().Be(0);
        ledger.Steps.Select(s => s.Step).Should().StartWith(new[]
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN"
        });
    }

    // ---- Cash no class takes --------------------------------------------------------------------

    [Fact]
    public void Interest_left_with_no_class_to_take_it_is_reported()
    {
        var pool = Collateral();
        var r = Run(Deal(NotesOnly(), pool));
        var ledger = r.CashLedger!;

        var coupons = r.TrancheCashflows["A1"].Sum(c => c.Interest) + r.TrancheCashflows["B1"].Sum(c => c.Interest);
        ledger.LeftInterest.Should().BeApproximately(pool.Sum(p => p.NetInterest) - coupons, 0.05);
        ledger.LeftInterest.Should().BeGreaterThan(1_000_000, "the fixture: the pool earns more than the coupons");
        ledger.PeriodsLeft.Should().Be(pool.Count);
        ledger.Steps.Should().OnlyContain(s => s.TakenNotPaid < 0.01 && s.PaidNotTaken < 0.01);
    }

    [Fact]
    public void A_residual_class_takes_the_leftover_and_nothing_is_left()
    {
        var ledger = Run(Deal(NotesOnly(withResidual: true), Collateral())).CashLedger!;

        ledger.LeftInterest.Should().BeApproximately(0, 0.01);
        ledger.PeriodsLeft.Should().Be(0);
        ledger.Steps.Single(s => s.Step == "RESIDUAL").Paid.Should().BeGreaterThan(1_000_000);
    }

    [Fact]
    public void A_release_step_with_no_one_to_release_to_is_reported_on_that_step()
    {
        var request = Deal(NotesOnly(), Collateral());
        request.Deal.UnifiedWaterfall!.ExecutionOrder!.Add("EXCESS_RELEASE");
        var ledger = Run(request).CashLedger!;

        var release = ledger.Steps.Single(s => s.Step == "EXCESS_RELEASE");
        release.TakenNotPaid.Should().BeGreaterThan(1_000_000, "the step took the excess and paid no one");
        release.PeriodsOff.Should().Be(12);
        release.FirstPeriodOff.Should().Be(FirstPayDate);
        ledger.LeftInterest.Should().BeApproximately(0, 0.01, "the step had already taken it");
    }

    // ---- The built-in excess-spread rule after the notes retire ----------------------------------

    [Fact]
    public void Excess_taken_off_the_strip_for_a_loss_once_the_notes_are_gone_is_reported()
    {
        // 40M of notes on a 100M pool repaying 10M a month: the note is retired in month 4. The
        // pool keeps losing 500k a month, and the rule keeps taking it off the strip's interest
        // with no note left to pay it to.
        var pool = Collateral(loss: 500_000, scheduledShare: 0.08, prepaid: 1_000_000, recoveryRate: 0.2);
        var r = Run(Deal(new Stack
        {
            Tranches = { Note("A1", 40_000_000, 0), Strip() },
            Interest = { "A1", "XS" }, Principal = { "A1" }, Writedown = { "A1" }
        }, pool));
        var ledger = r.CashLedger!;

        var retired = r.TrancheCashflows["A1"].First(c => c.Balance < 0.01).CashflowDate;
        var writedown = ledger.Steps.Single(s => s.Step == "WRITEDOWN");
        writedown.TakenNotPaid.Should().BeGreaterThan(1_000_000);
        writedown.FirstPeriodOff.Should().BeAfter(retired.AddDays(-1), "while the note stood, the excess paid it down");
        writedown.LargestGap.Should().BeInRange(300_000, 400_000,
            "at most a month's net loss is taken off the strip, and in these months none of it reaches a note");

        // Every dollar collected is paid to a class, lost at a named step, or left at the end.
        // Scheduled, prepaid and recovery principal are all left once the note is gone.
        var collected = CashIn(pool);
        ledger.Collected.Should().BeApproximately(collected, 0.01);
        (ledger.Steps.Sum(s => s.Paid) + ledger.Steps.Sum(s => s.TakenNotPaid - s.PaidNotTaken)
         + ledger.LeftInterest + ledger.LeftPrincipal).Should().BeApproximately(collected, 0.05);
        ledger.LeftPrincipal.Should().BeGreaterThan(10_000_000, "the pool outlives the note and nothing takes its principal");
    }

    // ---- Coupons the pool never collected ---------------------------------------------------------

    [Fact]
    public void Guaranteed_interest_the_pool_did_not_collect_is_reported_as_paid_and_not_taken()
    {
        var request = Deal(NotesOnly(), Collateral(wac: 2.0));
        request.Deal.InterestTreatment = "Guaranteed";
        var ledger = Run(request).CashLedger!;

        var interest = ledger.Steps.Single(s => s.Step == "INTEREST");
        interest.PaidNotTaken.Should().BeGreaterThan(1_000_000, "5% coupons on a pool earning 2%");
        interest.TakenNotPaid.Should().BeApproximately(0, 0.01);
    }

    // ---- Cash that is recorded somewhere else -----------------------------------------------------

    [Fact]
    public void An_expense_is_seen_where_it_is_recorded_and_its_step_balances()
    {
        var request = Deal(WithStrip(), Collateral());
        request.Deal.Expenses = new List<ExpenseDto> { new() { ExpenseName = "Trustee", Formula = "25000" } };
        request.Deal.UnifiedWaterfall!.ExecutionOrder!.Insert(0, "EXPENSE");
        var ledger = Run(request).CashLedger!;

        var expense = ledger.Steps.Single(s => s.Step == "EXPENSE");
        expense.Paid.Should().BeApproximately(12 * 25_000, 0.01);
        expense.Taken.Should().BeApproximately(12 * 25_000, 0.01);
        ledger.Steps.Should().OnlyContain(s => s.TakenNotPaid < 0.01 && s.PaidNotTaken < 0.01);
    }

    [Fact]
    public void Principal_held_back_for_the_OC_release_is_taken_in_the_turbo_step_that_pays_it()
    {
        // 80M of notes on a 100M pool with a 10% OC target: principal is held back at the start
        // of each period and the turbo step pays it to the certificate. It left the running
        // totals before any step ran, so the ledger carries it until the turbo pays it. Without
        // that, every release would read as cash the certificate was paid and no one gave up.
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 100_000_000.0;
        for (var i = 0; i < 6; i++)
        {
            b.WithPeriod(date: TestConstants.DefaultFirstPayDate.AddMonths(i), beginBalance: bal,
                scheduledPrincipal: 2_000_000, unscheduledPrincipal: 0, interest: 750_000);
            bal -= 2_000_000;
        }

        var cf = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithCertificateTranche()
            .WithSequentialWaterfall("A")
            .WithPayRule("ReleaseStruct", "SET_RELEASE_STRUCT(SEQ(SINGLE('Certificates')))")
            .WithOcTarget(0.10, 0)
            .WithCashLedger()
            .BuildAndRun(b.Build()).Cashflows;
        var steps = cf.CashLedger!.Steps.ToDictionary(s => s.Key, s => s.Value);
        var turbo = steps["EXCESS_TURBO"];

        var paidToNotes = steps["PRINCIPAL_SCHEDULED"].Paid;
        turbo.Taken.Should().BeApproximately(6 * 2_000_000 - paidToNotes, 0.01,
            "the principal the notes did not get is what was held back for the release");
        turbo.PaidNotTaken.Should().BeApproximately(0, 0.01);
        steps["RESIDUAL"].Paid.Should().BeApproximately(0, 0.01,
            "there is no residual class, and the certificate's balance being reset is not a payment");
        steps.Where(s => s.Key != "EXCESS_TURBO").Should()
            .OnlyContain(s => s.Value.TakenNotPaid < 0.01 && s.Value.PaidNotTaken < 0.01);

        // The turbo step itself is left out of the check above: on a certificate sent with no
        // balance the first period's release reaches no one, which the ledger reports here until
        // the certificate is opened at the pool less the notes (#160).
    }

    // The default order ends in a release step, which on these deals has no one to release to.
    private static readonly string[] NoReleaseStep =
    {
        "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN"
    };

    private static CollateralCashflows SixMonths(double serviceFee = 0)
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 100_000_000.0;
        for (var i = 0; i < 6; i++)
        {
            b.WithPeriod(date: TestConstants.DefaultFirstPayDate.AddMonths(i), beginBalance: bal,
                scheduledPrincipal: 2_000_000, unscheduledPrincipal: 0, interest: 750_000, serviceFee: serviceFee);
            bal -= 2_000_000;
        }

        return b.Build();
    }

    [Fact]
    public void A_certificates_balance_falling_with_the_pool_is_not_read_as_a_payment()
    {
        // The pool loses 1M a month and nothing writes the note down, so the certificate's balance
        // (pool less notes) falls by it. The engine books that fall in the certificate's principal
        // column at the end of the period. It is a balance moving, not cash paid to anyone.
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = 100_000_000.0;
        for (var i = 0; i < 6; i++)
        {
            b.WithPeriod(date: TestConstants.DefaultFirstPayDate.AddMonths(i), beginBalance: bal,
                scheduledPrincipal: 2_000_000, unscheduledPrincipal: 0, interest: 750_000,
                defaultedPrincipal: 1_000_000);
            bal -= 3_000_000;
        }

        var cf = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithCertificateTranche()
            .WithSequentialWaterfall("A")
            .WithExecutionOrder("INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY")
            .WithCashLedger()
            .BuildAndRun(b.Build()).Cashflows;
        var ledger = cf.CashLedger!;

        var certificate = cf.ClassCashflows.First(c => c.Key.TrancheName == "Certificates").Value.Cashflows.Values;
        certificate.Sum(c => c.ScheduledPrincipal).Should().BeGreaterThan(1_000_000, "the fixture: the fall is booked");
        ledger.Steps.Single(s => s.Key == "RESIDUAL").Value.Paid.Should().BeApproximately(0, 0.01);
        ledger.Steps.Should().OnlyContain(s => s.Value.TakenNotPaid < 0.01 && s.Value.PaidNotTaken < 0.01);
    }

    [Fact]
    public void A_strip_paid_from_the_servicing_fee_is_a_source_and_a_payment_of_its_own()
    {
        // The fee never enters the running totals, so the strip's payment has to be entered as
        // collected too, or it would read as cash a class was paid that no one gave up.
        var ledger = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithExcessServicingStrip("AIOS", 100_000_000)
            .WithSequentialWaterfall("A", "B")
            .WithExecutionOrder(NoReleaseStep)
            .WithCashLedger()
            .BuildAndRun(SixMonths(serviceFee: 20_000)).Cashflows.CashLedger!;
        var steps = ledger.Steps.ToDictionary(s => s.Key, s => s.Value);

        steps["SERVICING_STRIP"].Paid.Should().BeApproximately(6 * 20_000, 0.01);
        steps["SERVICING_STRIP"].Taken.Should().BeApproximately(6 * 20_000, 0.01);
        ledger.Collected.Should().BeApproximately(6 * (750_000 + 2_000_000), 0.01,
            "the pool's interest net of the fee, its principal, and the fee the strip was paid");
        steps.Values.Should().OnlyContain(s => s.TakenNotPaid < 0.01 && s.PaidNotTaken < 0.01);
    }

    [Fact]
    public void An_exchanged_class_is_paid_after_the_steps_and_does_not_disturb_them()
    {
        var ledger = new TestDealBuilder()
            .WithTranche("A", 60_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 40_000_000, 6.0, subOrder: 1)
            .WithExchangeClass("AB", subOrder: 2, "A", "B")
            .WithSequentialWaterfall("A", "B")
            .WithExecutionOrder(NoReleaseStep)
            .WithCashLedger()
            .BuildAndRun(SixMonths()).Cashflows.CashLedger!;

        ledger.Steps.Sum(s => s.Value.Paid).Should().BeGreaterThan(10_000_000);
        ledger.Steps.Should().OnlyContain(s => s.Value.TakenNotPaid < 0.01 && s.Value.PaidNotTaken < 0.01);
    }

    // ---- Cash that reaches no reader -------------------------------------------------------------

    [Theory]
    [InlineData("CapFundsReserve", true)]
    [InlineData("Offered", false)]
    public void Excess_released_to_a_class_the_response_drops_is_taken_and_not_paid(string stripType, bool dropped)
    {
        // A class typed as the funds account is reported from its class row; its tranche rows,
        // where a release lands, never reach the response. The ledger reads what a reader sees.
        var request = Deal(NotesOnly(), Collateral());
        var strip = Strip();
        strip.TrancheType = stripType;
        request.Deal.Tranches.Add(strip);
        request.Deal.UnifiedWaterfall!.ExecutionOrder!.Add("EXCESS_RELEASE");
        request.Deal.UnifiedWaterfall.Steps.Add(new WaterfallStepDto
            { Type = "EXCESS_RELEASE", Structure = Seq(new List<string> { "XS" }) });
        var r = Run(request);
        var release = r.CashLedger!.Steps.Single(s => s.Step == "EXCESS_RELEASE");

        var shown = r.TrancheCashflows.TryGetValue("XS", out var rows) ? rows.Sum(c => c.Interest) : 0.0;
        release.Taken.Should().BeGreaterThan(1_000_000);
        if (dropped)
        {
            shown.Should().BeApproximately(0, 0.01, "the fixture: the response shows the strip nothing");
            release.TakenNotPaid.Should().BeApproximately(release.Taken, 0.01);
        }
        else
        {
            shown.Should().BeApproximately(release.Taken, 0.01);
            release.TakenNotPaid.Should().BeApproximately(0, 0.01);
        }
    }

    [Fact]
    public void A_reserve_accounts_deposits_are_cash_held_not_cash_lost()
    {
        var request = Deal(NotesOnly(), Collateral());
        request.Deal.Tranches.Add(new TrancheDto
        {
            TrancheName = "RESERVE", OriginalBalance = 0, TrancheType = "CapFundsReserve", CashflowType = "Reserve",
            CouponType = "None", SubordinationOrder = 8, FirstPayDate = FirstPayDate,
            ReserveConfig = new ReserveAccountConfigDto
            {
                TargetPct = 0.02, TargetBase = "CutoffPoolBalance", CutoffPoolBalance = Pool, CapAtNoteBalance = false
            }
        });
        request.Deal.UnifiedWaterfall!.ExecutionOrder!.Add("RESERVE_DEPOSIT");
        request.Deal.UnifiedWaterfall.Steps.Add(new WaterfallStepDto { Type = "RESERVE_DEPOSIT" });
        var ledger = Run(request).CashLedger!;

        var deposit = ledger.Steps.Single(s => s.Step == "RESERVE_DEPOSIT");
        deposit.Taken.Should().BeGreaterThan(100_000, "the fixture: the reserve is built from excess interest");
        deposit.Paid.Should().BeApproximately(deposit.Taken, 0.01, "what the account holds counts as paid into it");
        deposit.TakenNotPaid.Should().BeApproximately(0, 0.01);
    }

    [Fact]
    public void A_class_paid_on_its_own_pay_day_is_read_on_that_day()
    {
        // The pool's cashflows are dated the 25th and the classes pay on the 15th: their rows are
        // keyed by the pay date, not by the collateral date the step was run for.
        var request = Deal(WithStrip(), Collateral(loss: 600_000));
        foreach (var t in request.Deal.Tranches)
            t.PayDay = 15;
        var r = Run(request);
        var ledger = r.CashLedger!;

        // The first row sits on the first pay date itself; every later one on the pay day.
        r.TrancheCashflows["A1"].Skip(1).Should()
            .OnlyContain(c => c.CashflowDate.Day == 15, "the fixture: rows carry the pay day");

        ledger.Steps.Sum(s => s.Paid).Should().BeGreaterThan(10_000_000);
        ledger.Steps.Should().OnlyContain(s => s.TakenNotPaid < 0.01 && s.PaidNotTaken < 0.01);
    }

    // ---- Principal held back for a release that nothing pays -------------------------------------

    private static TestDealBuilder OcDeal() => new TestDealBuilder()
        .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
        .WithSequentialWaterfall("A")
        .WithOcTarget(0.10, 0)
        .WithCashLedger();

    [Fact]
    public void Held_back_principal_with_no_release_structure_is_left_beside_the_certificate()
    {
        // The OC target holds 11.2M of principal back from the note over six months and there is
        // no release structure to pay it through. It reached no class; the certificate's balance
        // carries it.
        var ledger = OcDeal().WithCertificateTranche().BuildAndRun(SixMonths()).Cashflows.CashLedger!;

        ledger.LeftPrincipalBesideCertificate.Should().BeApproximately(11_200_000, 0.01);
        ledger.LeftPrincipal.Should().BeApproximately(0, 0.01);
        ledger.PeriodsLeft.Should().Be(6);
        ledger.Steps.Should().OnlyContain(s => s.Value.TakenNotPaid < 0.01 && s.Value.PaidNotTaken < 0.01);
        (ledger.Steps.Sum(s => s.Value.Paid) + ledger.LeftInterest + ledger.LeftPrincipalBesideCertificate)
            .Should().BeApproximately(ledger.Collected, 0.05, "collected is paid or left");
    }

    [Fact]
    public void Held_back_principal_with_no_certificate_and_no_turbo_step_is_left()
    {
        // A residual class takes what the last step leaves, but the held-back principal never
        // came back to the running totals, so it is not the residual's either.
        var ledger = OcDeal()
            .WithTranche("R", 0, 0.0, subOrder: 9, cashflowType: "PI", couponType: "Residual")
            .WithExecutionOrder(NoReleaseStep)
            .BuildAndRun(SixMonths()).Cashflows.CashLedger!;

        ledger.LeftPrincipal.Should().BeApproximately(11_200_000, 0.01);
        ledger.LeftPrincipalBesideCertificate.Should().BeApproximately(0, 0.01);
        ledger.LeftInterest.Should().BeApproximately(0, 0.01, "the residual took the interest");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Principal_the_notes_leave_goes_to_a_residual_class_unless_a_certificate_carries_it(bool certificate)
    {
        // 40M of notes on a 100M pool repaying 12M over six months... the note takes it all here,
        // so shrink the note: 5M of notes is retired in month 3 and the rest of the principal is left.
        var builder = new TestDealBuilder()
            .WithTranche("A", 5_000_000, 5.0, subOrder: 0)
            .WithTranche("R", 0, 0.0, subOrder: 9, cashflowType: "PI", couponType: "Residual")
            .WithSequentialWaterfall("A")
            .WithExecutionOrder(NoReleaseStep)
            .WithCashLedger();
        if (certificate)
            builder.WithCertificateTranche();
        var ledger = builder.BuildAndRun(SixMonths()).Cashflows.CashLedger!;
        var residual = ledger.Steps.Single(s => s.Key == "RESIDUAL").Value;

        if (certificate)
        {
            ledger.LeftPrincipalBesideCertificate.Should().BeApproximately(7_000_000, 0.01);
            ledger.LeftPrincipal.Should().BeApproximately(0, 0.01);
        }
        else
        {
            ledger.LeftPrincipalBesideCertificate.Should().BeApproximately(0, 0.01);
            ledger.LeftPrincipal.Should().BeApproximately(0, 0.01, "the residual took it");
            residual.Paid.Should().BeGreaterThan(7_000_000);
        }
    }

    // ---- Cash that moves outside the steps ---------------------------------------------------------

    [Fact]
    public void Interest_diverted_after_the_last_step_gets_a_line_of_its_own()
    {
        // A reinvestment-period diversion test with an order that has no excess step: the
        // diversion runs after the last step and buys collateral, so no class is paid it.
        var cf = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("E", 15_000_000, 8.0, subOrder: 1)
            .WithTranche("R", 5_000_000, 0.0, subOrder: 2, cashflowType: "PI", couponType: "Residual")
            .WithPayRule("InterestStruct", "SET_INTEREST_STRUCT(SEQ(SINGLE('A'), SINGLE('E')))")
            .WithPayRule("SchedStruct", "SET_SCHED_STRUCT(SEQ(SINGLE('A'), SINGLE('E')))")
            .WithPayRule("PrepayStruct", "SET_PREPAY_STRUCT(SEQ(SINGLE('A'), SINGLE('E')))")
            .WithPayRule("RecovStruct", "SET_RECOV_STRUCT(SEQ(SINGLE('A'), SINGLE('E')))")
            .WithPayRule("WritedownStruct", "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('E'), SINGLE('A')))")
            .WithInterestDiversion(new InterestDiversionConfig
            {
                Tranches = new[] { "A", "E" }, TriggerPct = 106.0, MaxPctOfInterest = 50.0,
                EndDate = TestConstants.DefaultFirstPayDate.AddMonths(12), PurchasePricePct = 99.5
            })
            .WithExecutionOrder(NoReleaseStep)
            .WithCashLedger()
            .BuildAndRun(FlatPool()).Cashflows;
        var ledger = cf.CashLedger!;

        var diverted = cf.InterestDiversions.Sum(d => d.Diverted);
        diverted.Should().BeGreaterThan(1_000_000, "the fixture: the test fails and diverts");
        ledger.Steps.Single(s => s.Key == "INTEREST_DIVERSION").Value.TakenNotPaid.Should().BeApproximately(diverted, 0.01);
        (ledger.Steps.Sum(s => s.Value.Paid) + ledger.Steps.Sum(s => s.Value.TakenNotPaid)).Should()
            .BeApproximately(ledger.Collected, 0.05);
    }

    private static CollateralCashflows FlatPool()
    {
        var b = new TestCollateralBuilder().WithGroupNum("1");
        for (var i = 0; i < 24; i++)
            b.WithPeriod(date: TestConstants.DefaultFirstPayDate.AddMonths(i), beginBalance: 100_000_000,
                scheduledPrincipal: 0, unscheduledPrincipal: 0, interest: 800_000);
        return b.Build();
    }

    [Fact]
    public void A_termination_payoff_is_entered_as_collected_and_as_paid()
    {
        // The deal ends in month 4 and the classes are redeemed out of the pool's remaining
        // balance, which never passes through the running totals.
        var cf = new TestDealBuilder()
            .WithTranche("A", 80_000_000, 5.0, subOrder: 0)
            .WithTranche("B", 20_000_000, 6.0, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithExecutionOrder(NoReleaseStep)
            .WithDateTermination(TestConstants.DefaultFirstPayDate.AddMonths(3))
            .WithCashLedger()
            .BuildAndRun(SixMonths()).Cashflows;
        var ledger = cf.CashLedger!;

        var shown = cf.TrancheCashflows.Values.SelectMany(t => t.Cashflows.Values)
            .Sum(c => c.Interest + c.ScheduledPrincipal + c.UnscheduledPrincipal + c.Expense);
        var payoff = ledger.Steps.Single(s => s.Key == "TERMINATION").Value;
        payoff.Paid.Should().BeGreaterThan(50_000_000);
        payoff.Taken.Should().BeApproximately(payoff.Paid, 0.01);
        ledger.Steps.Sum(s => s.Value.Paid).Should().BeApproximately(shown, 0.05,
            "the ledger's paid is what the response shows the classes");
        (ledger.Steps.Sum(s => s.Value.Paid) + ledger.LeftInterest + ledger.LeftPrincipal).Should()
            .BeApproximately(ledger.Collected, 0.05);
    }

    [Fact]
    public void The_response_carries_every_ledger_field()
    {
        // The API mapping, on a deal that fills the fields the others leave at zero.
        var request = Deal(new Stack
        {
            Tranches =
            {
                Note("A1", 80_000_000, 0),
                new TrancheDto
                {
                    TrancheName = "CERT", OriginalBalance = 0, TrancheType = "Certificate", CashflowType = "PI",
                    CouponType = "None", SubordinationOrder = 9, FirstPayDate = FirstPayDate
                }
            },
            Interest = { "A1" }, Principal = { "A1" }, Writedown = { "A1" }
        }, Collateral());
        request.Deal.UnifiedWaterfall!.ExecutionOrder!.Add("EXCESS_TURBO");
        request.Deal.UnifiedWaterfall.Steps.Add(new WaterfallStepDto
            { Type = "EXCESS_TURBO", OcTarget = new OcTargetDto { TargetPct = 0.10 } });
        var ledger = Run(request).CashLedger!;

        ledger.LeftPrincipalBesideCertificate.Should().BeGreaterThan(1_000_000,
            "the OC target holds principal back and nothing pays it");
        ledger.PeriodsLeft.Should().BeGreaterThan(0);
        ledger.Steps.Should().OnlyContain(s => s.Periods == 12);
    }

    [Fact]
    public void The_request_field_binds_under_its_stated_name()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true
        };
        JsonSerializer.Deserialize<WaterfallRequest>("""{"reportCashLedger":true}""", options)!
            .ReportCashLedger.Should().BeTrue();
        JsonSerializer.Deserialize<WaterfallRequest>("{}", options)!.ReportCashLedger.Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------

    private sealed class Stack
    {
        public List<TrancheDto> Tranches { get; } = new();
        public List<string> Interest { get; } = new();
        public List<string> Principal { get; } = new();
        public List<string> Writedown { get; } = new();
    }

    /// <summary>A1/B1 with an excess-spread strip swept inside INTEREST: the built-in rule's shape.</summary>
    private static Stack WithStrip() => new()
    {
        Tranches = { Note("A1", 70_000_000, 0), Note("B1", 30_000_000, 1), Strip() },
        Interest = { "A1", "B1", "XS" }, Principal = { "A1", "B1" }, Writedown = { "B1", "A1" }
    };

    /// <summary>A1/B1 and nothing to take what the coupons leave, unless a residual class is added.</summary>
    private static Stack NotesOnly(bool withResidual = false)
    {
        var s = new Stack
        {
            Tranches = { Note("A1", 70_000_000, 0), Note("B1", 30_000_000, 1) },
            Interest = { "A1", "B1" }, Principal = { "A1", "B1" }, Writedown = { "B1", "A1" }
        };
        if (withResidual)
            s.Tranches.Add(new TrancheDto
            {
                TrancheName = "R", OriginalBalance = 0, TrancheType = "Offered", CashflowType = "PI",
                CouponType = "Residual", FixedCoupon = 0.0, SubordinationOrder = 9, FirstPayDate = FirstPayDate,
                PayFrequency = 12, PayDay = FirstPayDate.Day
            });
        return s;
    }

    private static WaterfallResponse Run(WaterfallRequest request, bool ledger = true)
    {
        request.ReportCashLedger = ledger ? true : null;
        var result = new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(request).Result;
        (result as BadRequestObjectResult)?.Value.Should().BeNull("the waterfall request should succeed");
        return (WaterfallResponse)((OkObjectResult)result!).Value!;
    }

    private static WaterfallRequest Deal(Stack stack, List<PeriodCashflowDto>? pool = null) => new()
    {
        ProjectionDate = FirstPayDate.AddMonths(-1),
        CollateralCashflows = pool ?? Collateral(),
        Deal = new DealDto
        {
            DealName = "CASH_LEDGER_TEST",
            WaterfallType = "ComposableStructure",
            ClosingDate = FirstPayDate.AddMonths(-1),
            Tranches = stack.Tranches,
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = new List<string>
                {
                    "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN"
                },
                Steps = new List<WaterfallStepDto>
                {
                    new() { Type = "INTEREST", Structure = Seq(stack.Interest) },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq(stack.Principal) },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq(stack.Principal) },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq(stack.Principal) },
                    new() { Type = "WRITEDOWN", Structure = Seq(stack.Writedown) }
                }
            }
        }
    };

    private static TrancheDto Note(string name, double balance, int subOrder) => new()
    {
        TrancheName = name, OriginalBalance = balance, TrancheType = "Offered", CashflowType = "PI",
        CouponType = "Fixed", FixedCoupon = 5.0, SubordinationOrder = subOrder, FirstPayDate = FirstPayDate,
        PayFrequency = 12, PayDay = FirstPayDate.Day
    };

    private static TrancheDto Strip() => new()
    {
        TrancheName = "XS", OriginalBalance = Pool, TrancheType = "Offered", CashflowType = "IO",
        CouponType = "ExcessInterest", FixedCoupon = 0.0, ClassReference = "XS", SubordinationOrder = 5,
        FirstPayDate = FirstPayDate, PayFrequency = 12, PayDay = FirstPayDate.Day
    };

    private static PayableStructureDto Seq(List<string> tranches) => new() { Type = "SEQ", Tranches = tranches.ToList() };

    /// <summary>Twelve months; a share of the opening balance repaid each month; an optional flat loss.</summary>
    /// <summary>
    /// <paramref name="loss" /> is the principal defaulted each month, of which
    /// <paramref name="recoveryRate" /> comes back as recovery principal.
    /// </summary>
    private static List<PeriodCashflowDto> Collateral(double loss = 0, double scheduledShare = 0.01, double wac = Wac,
        double prepaid = 0, double recoveryRate = 0)
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = Pool;
        var scheduled = Pool * scheduledShare;
        for (var i = 0; i < 12 && balance > scheduled + prepaid + loss; i++)
        {
            var interest = balance * wac / 100 / 12;
            var end = balance - scheduled - prepaid - loss;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = "1",
                BeginBalance = balance, Balance = end, ScheduledPrincipal = scheduled,
                UnscheduledPrincipal = prepaid, Interest = interest, NetInterest = interest,
                DefaultedPrincipal = loss, RecoveryPrincipal = loss * recoveryRate,
                CollateralLoss = loss * (1 - recoveryRate), Wac = wac
            });
            balance = end;
        }

        return periods;
    }

    private static double CashIn(IEnumerable<PeriodCashflowDto> pool) =>
        pool.Sum(p => p.NetInterest + p.ScheduledPrincipal + p.UnscheduledPrincipal + p.RecoveryPrincipal);
}
