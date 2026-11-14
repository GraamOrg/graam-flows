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
        var pool = Collateral(loss: 600_000);
        var ledger = Run(Deal(WithStrip(), pool)).CashLedger!;

        var collected = pool.Sum(p => p.NetInterest + p.ScheduledPrincipal + p.RecoveryPrincipal);
        ledger.Collected.Should().BeApproximately(collected, 0.01);
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
        var pool = Collateral(loss: 500_000, scheduledShare: 0.10);
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
        writedown.LargestGap.Should().BeInRange(400_000, 500_000,
            "at most a month's loss is taken off the strip, and in these months none of it reaches a note");

        // Every dollar collected is paid to a class, lost at a named step, or left at the end.
        var collected = pool.Sum(p => p.NetInterest + p.ScheduledPrincipal + p.RecoveryPrincipal);
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
    private static List<PeriodCashflowDto> Collateral(double loss = 0, double scheduledShare = 0.01, double wac = Wac)
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = Pool;
        var scheduled = Pool * scheduledShare;
        for (var i = 0; i < 12 && balance > scheduled + loss; i++)
        {
            var interest = balance * wac / 100 / 12;
            var end = balance - scheduled - loss;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = "1",
                BeginBalance = balance, Balance = end, ScheduledPrincipal = scheduled,
                Interest = interest, NetInterest = interest, DefaultedPrincipal = loss, RecoveryPrincipal = 0,
                CollateralLoss = loss, Wac = wac
            });
            balance = end;
        }

        return periods;
    }
}
