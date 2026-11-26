using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Tests.Fixtures;
using GraamFlows.Tests.Helpers;
using GraamFlows.Waterfall;
using GraamFlows.Waterfall.Structures.PayableStructures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// A principal payment reports what it did, and the step that made it spends what was reported.
///
/// A step used to hand an amount to a structure, get nothing back, and work out what had been paid
/// from how far a set of balances fell. That inference is wrong whenever the cash recorded and
/// the measured balances part ways: a class paid more than it holds (its balance stops at zero),
/// or a class outside the measured set (a certificate at the end of the structure). The cash was
/// recorded on the class and not taken off the step's funds, so the same cash was paid again
/// further down.
///
/// A class takes what it holds: handed more, it takes its balance, reports that, and the rest
/// stays with the step to go on to the next class.
///
/// The tests are on synthetic deals. They do not rest on the cash ledger alone, which reads the
/// same rows the receipt does: each also adds up what the response pays in a period and holds it
/// against what the pool collected in that period.
/// </summary>
public class PrincipalReceiptTests
{
    private static readonly DateTime FirstPayDate = new(2026, 2, 25);
    private const double PoolBalance = 100_000_000;
    private const double Wac = 9.0;
    private const double NoteCoupon = 5.0;

    // ---- A structure reports the sum of what its classes report --------------------------------

    private sealed class Variables : IDealVariableProvider
    {
        public void SetVariable(string varName, object varValue) { }
        public double GetVariable(string varName, DateTime? asOfDate = null) => 600;
        public object GetVariableObj(string varName, DateTime? asOfDate = null) => 600.0;
    }

    private static readonly Dictionary<string, Func<MockPayable[], IPayable>> Structures = new()
    {
        ["SEQ"] = m => new SequentialStructure(m),
        ["PRORATA"] = m => new ProrataStructure(m),
        ["SEQ of PRORATA and a class"] = m =>
            new SequentialStructure(new IPayable[] { new ProrataStructure(new IPayable[] { m[0], m[1] }), m[2] }),
        ["SHIFTI"] = m => new ShiftingInterestStructure(0.7, m[0], new SequentialStructure(new IPayable[] { m[1], m[2] })),
        ["CSCAP"] = m => new EnhancementCapStructure(0.3, m[0], new SequentialStructure(new IPayable[] { m[1], m[2] })),
        ["FIXED"] = m => new FixedStructure(300.0, m[0], new SequentialStructure(new IPayable[] { m[1], m[2] })),
        ["FORCE_PAYDOWN"] = m => new ForcedPaydownStructure(m[2], new SequentialStructure(new IPayable[] { m[0], m[1] })),
        ["SPLIT"] = m => new SplitStructure(new[] { 50.0, 30.0, 20.0 }, m),
        ["TARGET"] = m => new TargetBalanceStructure(new Variables(), "target", m[0],
            new SequentialStructure(new IPayable[] { m[1], m[2] })),
        ["PAC"] = m => new PlannedAmortizationStructure(new Variables(), "schedule", m[0],
            new SequentialStructure(new IPayable[] { m[1], m[2] })),
        ["SPAC"] = m => new SinglePlannedAmortizationClass(new Variables(), "schedule", m[0])
    };

    public static IEnumerable<object[]> StructuresAndAmounts =>
        from structure in Structures.Keys
        from amount in new[] { 0.0, 10.0, 400.0, 1_500.0, 3_400.0 }
        select new object[] { structure, amount };

    [Theory]
    [MemberData(nameof(StructuresAndAmounts))]
    public void A_structure_reports_what_its_classes_were_paid(string structure, double amount)
    {
        // Three classes holding 1,000, 2,000 and 500. The amounts run from nothing to less than
        // they hold together (3,400 leaves 100).
        foreach (var pay in new Func<IPayable, double, PrincipalReceipt>[]
                 {
                     (p, a) => p.PaySp(null!, FirstPayDate, a, () => { }),
                     (p, a) => p.PayUsp(null!, FirstPayDate, a, () => { }),
                     (p, a) => p.PayRp(null!, FirstPayDate, a, () => { })
                 })
        {
            var classes = new[]
            {
                TestPayableBuilder.Mock(1_000, name: "A"), TestPayableBuilder.Mock(2_000, name: "B"),
                TestPayableBuilder.Mock(500, name: "C")
            };
            var payable = Structures[structure](classes);
            var held = classes.Sum(c => c.Balance);

            PrincipalReceipt receipt;
            try
            {
                receipt = pay(payable, amount);
            }
            catch (Exception e) when (e.Message.Contains("distribut", StringComparison.OrdinalIgnoreCase))
            {
                // Fixed shares refuse an amount they cannot place within their classes. Nothing
                // was reported because nothing was to be relied on.
                structure.Should().Be("SPLIT");
                continue;
            }

            var fell = held - classes.Sum(c => c.Balance);
            receipt.Paid.Should().BeApproximately(fell, 1e-6, $"{structure}, {amount}: what the classes took");
            receipt.Reduced.Should().BeApproximately(fell, 1e-6, $"{structure}, {amount}");
            receipt.PastBalance.Should().BeApproximately(0, 1e-6);
        }
    }

    [Fact]
    public void Receipts_add_up_and_show_what_was_paid_past_a_balance()
    {
        var sum = new PrincipalReceipt(300, 100) + new PrincipalReceipt(50, 50) + PrincipalReceipt.None;
        sum.Should().Be(new PrincipalReceipt(350, 150));
        sum.PastBalance.Should().Be(200);
        new PrincipalReceipt(-40, -40).PastBalance.Should().Be(0, "a negative payment is not an overpayment");
        new PrincipalReceipt(10, 50).PastBalance.Should().Be(0,
            "a balance that fell by more than the rows show is not an overpayment either");
    }

    // ---- A step spends what the payment reports -------------------------------------------------

    [Theory]
    [InlineData("scheduled")]
    [InlineData("unscheduled")]
    [InlineData("recovery")]
    public void A_class_named_alone_takes_its_balance_and_the_rest_goes_on(string source)
    {
        // One source's structure is the senior class alone, which retires in the third period.
        // It is paid its balance and no more. That source's principal then has no class in its
        // structure, and falls to the residual class as leftover.
        var pool = Losses(600_000);
        var request = Deal(pool, order: StripFirstOrder, seniorBalance: 2_500_000, withTurbo: false,
            withResidual: true);
        Step(request, "PRINCIPAL", source).Default = Single("A1");
        var r = Run(request);

        Principal(r, "A1").Should().BeApproximately(2_500_000, 0.01, "the class is paid what it held");
        r.TrancheCashflows["A1"].Should().OnlyContain(c => c.Balance >= 0);
        Principal(r, "R").Should().BeGreaterThan(1_000_000, "the fixture: that source's principal goes on");
        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Fact]
    public void An_OC_turbo_whose_class_has_retired_pays_nothing_and_spends_nothing()
    {
        // The turbo's structure is the senior class alone. Once it retires the turbo has no one
        // to pay, and the interest goes on to the release.
        var pool = Losses(600_000);
        var request = Deal(pool, order: OcOrder, seniorBalance: 4_000_000);
        var turbo = Step(request, "EXCESS_TURBO");
        turbo.Structure = Single("A1");
        turbo.OcTarget = new OcTargetDto { TargetPct = 0.05 };
        var r = Run(request);

        Principal(r, "A1").Should().BeApproximately(4_000_000, 0.01, "the class is paid what it held");
        r.CashLedger!.Steps.Single(s => s.Step == "EXCESS_TURBO").Paid.Should().BeGreaterThan(100_000,
            "the fixture: the turbo pays while the class has a balance");
        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Theory]
    [InlineData("interestFirst")]
    [InlineData("principalFirst")]
    public void An_interleaved_order_pays_the_class_behind_a_retired_one(string order)
    {
        // Interleaved, each level's class is handed the principal directly. The senior class
        // retires in the third period; the principal it cannot take goes on to the junior class.
        var pool = Losses(600_000);
        var request = Deal(pool, order: new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN",
            "EXCESS_RELEASE"
        }, seniorBalance: 2_500_000, withTurbo: false);
        request.Deal.UnifiedWaterfall!.WaterfallOrder = order;
        var r = Run(request);

        Principal(r, "A1").Should().BeApproximately(2_500_000, 0.01, "the class is paid what it held");
        (Principal(r, "A1") + Principal(r, "B1")).Should().BeApproximately(
            pool.Sum(p => p.ScheduledPrincipal + p.UnscheduledPrincipal + p.RecoveryPrincipal), 0.01,
            "every unit of principal collected reaches a note");
        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_forced_paydown_in_a_principal_step_spends_what_it_paid(bool withResidual)
    {
        // A forced paydown pays its class in full whatever it is handed. Measured on balances
        // that was right only by accident; the receipt is what the classes recorded. A residual
        // class takes what is left, never an overdraft.
        var request = Deal(Losses(600_000), order: StripFirstOrder, withResidual: withResidual);
        request.Deal.UnifiedWaterfall!.Steps.RemoveAll(s => s.Type == "EXCESS_TURBO");
        Step(request, "PRINCIPAL", "recovery").Default = new PayableStructureDto
            { Type = "FORCE_PAYDOWN", Forced = Single("B1"), Support = Seq("A1") };
        var r = Run(request);
        var ledger = r.CashLedger!;

        foreach (var step in ledger.Steps.Where(s => s.Step.StartsWith("PRINCIPAL")))
        {
            step.TakenNotPaid.Should().BeApproximately(0, 0.01, step.Step);
            step.PaidNotTaken.Should().BeApproximately(0, 0.01, step.Step);
        }

        // The forced class is paid in full whatever the step held, so the step pays out more
        // than it was given. That is not hidden: the step says how far it overdrew, and the
        // ledger's identity holds with it.
        ledger.Steps.Single(s => s.Step == "PRINCIPAL_RECOVERY").Overdrew.Should().BeGreaterThan(1_000_000,
            "the fixture: the forced paydown pays more than the recoveries");
        ledger.Steps.Where(s => s.Step != "PRINCIPAL_RECOVERY").Should().OnlyContain(s => s.Overdrew == 0,
            "the overdraft is on the step that made it");
        ledger.Overdrawn.Should().BeGreaterThan(1_000_000);
        LedgerAddsUp(ledger);
        ResponsePaysOut(r, request.CollateralCashflows).Should().BeApproximately(
            request.CollateralCashflows.Sum(Collected) + ledger.Overdrawn - ledger.LeftInterest - ledger.LeftPrincipal, 0.05,
            "what the response pays beyond what was collected is what the ledger says was overdrawn");
    }

    [Fact]
    public void An_overdraft_on_one_principal_total_does_not_hide_principal_left_on_another()
    {
        // The scheduled step force-pays A in full in the first period, about 9M beyond the
        // scheduled principal it held. The other principal steps name A alone, so once A is gone
        // their principal reaches no note while B still stands. The period ends with scheduled
        // principal overdrawn and prepaid and recovered principal left: reported apart, not netted.
        var pool = Losses(600_000);
        var request = Deal(pool, order: StripFirstOrder, seniorBalance: 10_000_000, withTurbo: false);
        Step(request, "PRINCIPAL", "scheduled").Default = new PayableStructureDto
            { Type = "FORCE_PAYDOWN", Forced = Single("A1"), Support = Seq("B1") };
        Step(request, "PRINCIPAL", "unscheduled").Default = Seq("A1");
        Step(request, "PRINCIPAL", "recovery").Default = Seq("A1");
        var r = Run(request);
        var ledger = r.CashLedger!;

        var overdrew = 10_000_000 - pool[0].ScheduledPrincipal;
        ledger.Steps.Single(s => s.Step == "PRINCIPAL_SCHEDULED").Overdrew.Should().BeApproximately(overdrew, 0.01);
        ledger.Overdrawn.Should().BeApproximately(overdrew, 0.01, "the run's overdraft is the step's");
        ledger.LeftPrincipal.Should().BeApproximately(
            pool.Sum(p => p.UnscheduledPrincipal + p.RecoveryPrincipal), 0.01,
            "every unit of prepaid and recovered principal is left, the first period's included");
        LedgerAddsUp(ledger);
        ResponsePaysOut(r, pool).Should().BeApproximately(
            pool.Sum(Collected) + ledger.Overdrawn - ledger.LeftInterest - ledger.LeftPrincipal, 0.05);
    }

    [Fact]
    public void A_residual_taking_the_principal_left_settles_an_overdraft_another_total_covered()
    {
        // The scheduled step force-pays A (1.2M) out of about 1M of scheduled principal, overdrawing
        // that total by about 0.2M. Prepaid and recovered principal (0.5M) reach no note and the
        // residual takes the 0.3M the three totals come to. No cash was paid out beyond what was
        // collected: the residual's line shows it settled the overdraft, and the run shows none.
        var pool = Losses(600_000);
        var request = Deal(pool, order: StripFirstOrder, seniorBalance: 1_200_000, withTurbo: false,
            withResidual: true);
        Step(request, "PRINCIPAL", "scheduled").Default = new PayableStructureDto
            { Type = "FORCE_PAYDOWN", Forced = Single("A1"), Support = Seq("B1") };
        Step(request, "PRINCIPAL", "unscheduled").Default = Seq("A1");
        Step(request, "PRINCIPAL", "recovery").Default = Seq("A1");
        var r = Run(request);
        var ledger = r.CashLedger!;

        var overdrew = 1_200_000 - pool[0].ScheduledPrincipal;
        ledger.Steps.Single(s => s.Step == "PRINCIPAL_SCHEDULED").Overdrew.Should().BeApproximately(overdrew, 0.01);
        ledger.Steps.Single(s => s.Step == "RESIDUAL").Overdrew.Should().BeApproximately(-overdrew, 0.01);
        ledger.Overdrawn.Should().BeApproximately(0, 0.01);
        ledger.Steps.Sum(s => s.Overdrew).Should().BeApproximately(ledger.Overdrawn, 0.01);
        ledger.LeftPrincipal.Should().BeApproximately(0, 0.01);
        EveryStepBalances(ledger);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_release_after_an_interest_overdraft_pays_nothing_and_the_overdraft_stands(bool withReserveDeposit)
    {
        // The turbo is a forced paydown of A out of interest: it pays A in full whatever interest is
        // left, and overdraws the interest total. The release that follows has nothing to pay. It
        // used to set the total back to zero, so the overdraft showed as paid on the release and
        // the run reported nothing overdrawn.
        var pool = Losses(600_000);
        var request = Deal(pool, order: OcOrder, seniorBalance: 10_000_000);
        var turbo = Step(request, "EXCESS_TURBO");
        turbo.Structure = new PayableStructureDto { Type = "FORCE_PAYDOWN", Forced = Single("A1"), Support = Seq("B1") };
        turbo.OcTarget = new OcTargetDto { TargetPct = 0.30 };
        if (withReserveDeposit)
        {
            // A reserve below its target between the turbo and the release: nothing is deposited
            // out of an overdraft, and the deposit step does not cancel it either.
            request.Deal.Tranches.Add(new TrancheDto
            {
                TrancheName = "RESERVE", OriginalBalance = 0, TrancheType = "CapFundsReserve",
                CashflowType = "Reserve", CouponType = "None", FirstPayDate = FirstPayDate, PayDay = FirstPayDate.Day,
                ReserveConfig = new ReserveAccountConfigDto
                    { TargetPct = 0.05, TargetBase = "CutoffPoolBalance", CutoffPoolBalance = PoolBalance }
            });
            var order = request.Deal.UnifiedWaterfall!.ExecutionOrder!;
            order.Insert(order.IndexOf("EXCESS_RELEASE"), "RESERVE_DEPOSIT");
            request.Deal.UnifiedWaterfall.Steps.Add(new WaterfallStepDto { Type = "RESERVE_DEPOSIT" });
        }

        var r = Run(request);
        var ledger = r.CashLedger!;

        var turboStep = ledger.Steps.Single(s => s.Step == "EXCESS_TURBO");
        turboStep.Overdrew.Should().BeGreaterThan(1_000_000, "the fixture: the turbo overdraws interest");
        var release = ledger.Steps.Single(s => s.Step == "EXCESS_RELEASE");
        release.Overdrew.Should().BeApproximately(0, 0.01, "the release neither makes nor cancels the overdraft");
        release.TakenNotPaid.Should().BeApproximately(0, 0.01);
        release.PaidNotTaken.Should().BeApproximately(0, 0.01);
        ledger.Steps.Where(s => s.Step != "EXCESS_TURBO").Should().OnlyContain(s => Math.Abs(s.Overdrew) < 0.01,
            "no later step makes or cancels the overdraft");
        ledger.Overdrawn.Should().BeApproximately(turboStep.Overdrew, 0.01, "the run's overdraft is the turbo's");
        LedgerAddsUp(ledger);
        ResponsePaysOut(r, pool).Should().BeApproximately(
            pool.Sum(Collected) + ledger.Overdrawn - ledger.LeftInterest - ledger.LeftPrincipal, 0.05);
    }

    [Fact]
    public void A_certificate_at_the_end_of_the_turbo_structure_is_paid_that_cash_once()
    {
        // The stored-deal case. An OC deal's turbo runs through the notes and then the
        // certificate. The certificate is not a note, so measured on the notes' balance the turbo
        // had paid it nothing, and the release paid the same interest to it again.
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = PoolBalance;
        for (var i = 0; i < 12; i++)
        {
            b.WithPeriod(date: TestConstants.DefaultFirstPayDate.AddMonths(i), beginBalance: bal,
                scheduledPrincipal: 8_000_000, unscheduledPrincipal: 0, interest: bal * Wac / 1200);
            bal -= 8_000_000;
        }

        var cf = new TestDealBuilder()
            .WithTranche("A", 60_000_000, NoteCoupon, subOrder: 0)
            .WithCertificateTranche()
            .WithSequentialWaterfall("A")
            .WithPayRule("TurboStruct", "SET_TURBO_STRUCT(SEQ(SINGLE('A'), SINGLE('Certificates')))")
            .WithPayRule("ReleaseStruct", "SET_RELEASE_STRUCT(SEQ(SINGLE('Certificates')))")
            .WithOcTarget(0.50, 0)
            .WithCashLedger()
            .BuildAndRun(b.Build()).Cashflows;
        var steps = cf.CashLedger!.Steps.ToDictionary(s => s.Key, s => s.Value);

        var note = cf.TrancheCashflows.First(t => t.Key.TrancheName == "A").Value.Cashflows.Values;
        note.Last().Balance.Should().Be(0, "the fixture: the note retires inside the run");
        steps["EXCESS_TURBO"].Paid.Should().BeGreaterThan(1_000_000, "the fixture: the turbo pays");
        steps["EXCESS_TURBO"].PaidNotTaken.Should().BeApproximately(0, 0.01,
            "what the turbo paid the certificate came off the interest");
        steps["EXCESS_RELEASE"].PaidNotTaken.Should().BeApproximately(0, 0.01);
        steps["EXCESS_RELEASE"].TakenNotPaid.Should().BeApproximately(0, 0.01);
        EveryPeriodPaysOutWhatItCollected(cf, b.Build());
    }

    [Fact]
    public void A_certificate_paid_while_its_balance_is_nothing_is_read_where_the_response_reads_it()
    {
        // A certificate opens with no balance, and a payment to it then is recorded on its class
        // row and on no tranche row. The response reports a certificate from its class row, so
        // that is where its receipt reads. Read from the tranche rows the payment would report
        // as nothing and the interest would be paid out again.
        var b = new TestCollateralBuilder().WithGroupNum("1");
        var bal = PoolBalance;
        for (var i = 0; i < 3; i++)
        {
            b.WithPeriod(date: TestConstants.DefaultFirstPayDate.AddMonths(i), beginBalance: bal,
                scheduledPrincipal: 1_000_000, unscheduledPrincipal: 0, interest: bal * Wac / 1200);
            bal -= 1_000_000;
        }

        var cf = new TestDealBuilder()
            .WithTranche("A", PoolBalance, NoteCoupon, subOrder: 0)
            .WithCertificateTranche()
            .WithSequentialWaterfall("A")
            .WithPayRule("TurboStruct", "SET_TURBO_STRUCT(SINGLE('Certificates'))")
            .WithOcTarget(0.10, 0)
            .WithCashLedger()
            .BuildAndRun(b.Build()).Cashflows;
        var turbo = cf.CashLedger!.Steps.ToDictionary(s => s.Key, s => s.Value)["EXCESS_TURBO"];

        turbo.Paid.Should().BeGreaterThan(100_000, "the fixture: the turbo pays the certificate from the first period");
        turbo.PaidPastBalance.Should().Be(0, "a certificate's balance is not what it may be paid");
        turbo.PaidNotTaken.Should().BeApproximately(0, 0.01);
        turbo.TakenNotPaid.Should().BeApproximately(0, 0.01);
        EveryPeriodPaysOutWhatItCollected(cf, b.Build());
    }

    // ---- A certificate is paid what the steps pay it --------------------------------------------

    /// <summary>
    /// A note of 20M and a certificate on a 100M pool repaying 6% a month, with an OC target the
    /// turbo builds toward. The turbo's structure runs through the note and then the
    /// certificate; the release pays the certificate the interest left.
    /// </summary>
    private static WaterfallRequest CertificateDeal(List<PeriodCashflowDto> pool, bool certificateInPrincipal)
    {
        var notes = certificateInPrincipal ? Seq("A1", "CERT") : Seq("A1");
        return new WaterfallRequest
        {
            ProjectionDate = FirstPayDate.AddMonths(-1),
            CollateralCashflows = pool,
            Deal = new DealDto
            {
                DealName = "PRINCIPAL_RECEIPT_TEST",
                WaterfallType = "ComposableStructure",
                ClosingDate = FirstPayDate.AddMonths(-1),
                Tranches = new List<TrancheDto>
                {
                    Note("A1", 20_000_000, 0),
                    new()
                    {
                        TrancheName = "CERT", OriginalBalance = 80_000_000, TrancheType = "Certificate",
                        CashflowType = "PI", CouponType = "None", SubordinationOrder = 9, FirstPayDate = FirstPayDate
                    }
                },
                UnifiedWaterfall = new UnifiedWaterfallDto
                {
                    ExecutionOrder = OcOrder.ToList(),
                    Steps = new List<WaterfallStepDto>
                    {
                        new() { Type = "INTEREST", Structure = Seq("A1") },
                        new() { Type = "PRINCIPAL", Source = "scheduled", Default = notes },
                        new() { Type = "PRINCIPAL", Source = "unscheduled", Default = notes },
                        new() { Type = "PRINCIPAL", Source = "recovery", Default = notes },
                        new() { Type = "WRITEDOWN", Structure = Seq("A1") },
                        new()
                        {
                            Type = "EXCESS_TURBO", Structure = Seq("A1", "CERT"),
                            OcTarget = new OcTargetDto { TargetPct = 0.10, FloorAmt = 90_000_000 }
                        },
                        new() { Type = "EXCESS_RELEASE", Structure = Seq("CERT") }
                    }
                }
            }
        };
    }

    private static List<PeriodCashflowDto> Amortising(double defaulted)
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        for (var i = 0; i < 14; i++)
        {
            var interest = balance * 8.0 / 1200;
            var scheduled = balance * 0.06;
            var prepaid = Math.Min(300_000, balance - scheduled);
            var lost = Math.Min(defaulted, balance - scheduled - prepaid);
            var end = balance - scheduled - lost;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = "1", BeginBalance = balance,
                Balance = end - prepaid, ScheduledPrincipal = scheduled, UnscheduledPrincipal = prepaid,
                Interest = interest, NetInterest = interest, DefaultedPrincipal = lost,
                RecoveryPrincipal = lost * 0.25, CollateralLoss = lost * 0.75, Wac = 8.0
            });
            balance = end - prepaid;
        }

        return periods;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_certificate_the_turbo_pays_is_paid_that_cash_once_whoever_else_pays_it(bool certificateInPrincipal)
    {
        // Both ways round. With the certificate in the turbo's structure only, a build that spent
        // the turbo's payment lost it, because the certificate's balance fall was booked as its
        // principal in place of the payment. With the certificate in the principal structures as
        // well, a build that did not spend it paid the same interest again from the release.
        var pool = Amortising(defaulted: 0);
        var r = Run(CertificateDeal(pool, certificateInPrincipal));

        r.CashLedger!.Steps.Single(s => s.Step == "EXCESS_TURBO").Paid.Should().BeGreaterThan(1_000_000,
            "the fixture: the turbo pays");
        Principal(r, "A1").Should().BeApproximately(20_000_000, 0.01, "the note is repaid");
        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Fact]
    public void A_loss_that_eats_the_overcollateralization_is_not_principal_paid_to_the_certificate()
    {
        // 400,000 of the pool defaults each month and a quarter is recovered. The certificate's
        // balance, the pool less the note, falls by the loss. No one is paid that: the
        // certificate's principal is the cash the steps pay it, and the response adds up to what
        // was collected.
        var pool = Amortising(defaulted: 400_000);
        var r = Run(CertificateDeal(pool, certificateInPrincipal: true));

        pool.Sum(p => p.DefaultedPrincipal).Should().BeGreaterThan(5_000_000, "the fixture: losses");
        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Fact]
    public void A_certificate_sent_with_no_balance_is_still_paid_the_principal_released_to_it()
    {
        // The release structure cannot place principal in a certificate that stands at zero, as
        // one sent with no balance does in its first period. The principal stays held and is paid
        // to the certificate with what the steps leave, in that same period.
        var pool = Amortising(defaulted: 0);
        var request = CertificateDeal(pool, certificateInPrincipal: false);
        request.Deal.Tranches.Single(t => t.TrancheName == "CERT").OriginalBalance = 0;
        // A 10% target with no floor: the deal opens far above it, so principal is released at once.
        Step(request, "EXCESS_TURBO").OcTarget = new OcTargetDto { TargetPct = 0.10 };
        var r = Run(request);

        r.TrancheCashflows["CERT"].First().ScheduledPrincipal.Should().BeGreaterThan(100_000,
            "the fixture: principal is released to the certificate in the first period");
        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Fact]
    public void Excess_the_strip_swept_is_given_up_only_for_principal_a_note_was_paid()
    {
        // The built-in rule: the strip sweeps the excess inside INTEREST and gives back what a
        // loss needs, which pays the notes principal. The junior note is small and retires; from
        // then the loss has no note to pay, and the strip keeps its excess.
        var pool = Losses(600_000);
        var request = Deal(pool, order: new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN"
        }, seniorBalance: 4_000_000, withTurbo: false, withResidual: true);
        request.Deal.UnifiedWaterfall!.Steps.RemoveAll(s => s.Type == "EXCESS_RELEASE");
        Step(request, "INTEREST").Structure = Seq("A1", "B1", "XS");
        // Every principal structure reaches the senior note alone, so once it retires nothing
        // the rule offers can be placed.
        foreach (var step in request.Deal.UnifiedWaterfall.Steps.Where(s => s.Type == "PRINCIPAL"))
            step.Default = Single("A1");
        var r = Run(request);

        Principal(r, "A1").Should().BeApproximately(4_000_000, 0.01, "the fixture: the senior note retires");
        var retired = r.TrancheCashflows["A1"].First(c => c.Balance < 0.01).CashflowDate;
        foreach (var period in pool.Where(p => p.CashflowDate > retired))
        {
            var junior = r.TrancheCashflows["B1"].Single(c => c.CashflowDate == period.CashflowDate).Interest;
            r.TrancheCashflows["XS"].Single(c => c.CashflowDate == period.CashflowDate).Interest.Should()
                .BeApproximately(period.NetInterest - junior, 0.01,
                    $"{period.CashflowDate:yyyy-MM-dd}: the strip keeps the whole excess");
        }

        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Fact]
    public void Excess_a_note_could_take_only_part_of_is_given_up_only_in_that_part()
    {
        // The senior note holds 1,600,000. The first period's principal pays it 1,500,000, so of
        // the 300,000 loss the rule offers, it can take 100,000. The strip gives up 100,000, and
        // the other 200,000 of the loss is written down.
        var pool = Losses(600_000);
        var request = Deal(pool, order: new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN"
        }, seniorBalance: 1_600_000, withTurbo: false, withResidual: true);
        request.Deal.UnifiedWaterfall!.Steps.RemoveAll(s => s.Type == "EXCESS_RELEASE");
        Step(request, "INTEREST").Structure = Seq("A1", "B1", "XS");
        foreach (var step in request.Deal.UnifiedWaterfall.Steps.Where(s => s.Type == "PRINCIPAL"))
            step.Default = Single("A1");
        var r = Run(request);

        var first = pool[0];
        var coupons = r.TrancheCashflows["A1"][0].Interest + r.TrancheCashflows["B1"][0].Interest;
        r.TrancheCashflows["A1"][0].Balance.Should().Be(0, "the fixture: the note retires on the rule's payment");
        r.TrancheCashflows["XS"][0].Interest.Should().BeApproximately(first.NetInterest - coupons - 100_000, 0.01,
            "the strip gives up what the note took");
        r.TrancheCashflows["B1"][0].Writedown.Should().BeApproximately(200_000, 0.01,
            "the rest of the loss is written down");
        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Fact]
    public void A_coverage_cascade_whose_junior_level_names_a_funded_residual_pays_out_what_it_collects()
    {
        // The most junior coverage level names a funded residual class, which the notes' balance
        // leaves out. This holds the cascade to what was collected, period by period. It does not
        // by itself tell the cure's receipt from the notes' balance fall: on this deal the cure
        // pays the notes, which both measures see.
        var pool = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        for (var i = 0; i < 12; i++)
        {
            var interest = balance * Wac / 1200;
            var scheduled = balance * 0.01;
            const double defaulted = 2_500_000;
            const double prepaid = 300_000;
            var end = balance - scheduled - prepaid - defaulted;
            pool.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = "1", BeginBalance = balance,
                Balance = end, ScheduledPrincipal = scheduled, UnscheduledPrincipal = prepaid,
                Interest = interest, NetInterest = interest,
                DefaultedPrincipal = defaulted, RecoveryPrincipal = defaulted * 0.2,
                CollateralLoss = defaulted * 0.8, Wac = Wac
            });
            balance = end;
        }

        var request = Deal(pool, order: StripFirstOrder, withTurbo: false);
        request.Deal.Tranches.Clear();
        request.Deal.Tranches.Add(Note("A1", 4_000_000, 0));
        request.Deal.Tranches.Add(Note("B1", 4_000_000, 1));
        request.Deal.Tranches[0].FixedCoupon = 4.0;
        request.Deal.Tranches.Add(new TrancheDto
        {
            TrancheName = "SUB", OriginalBalance = 92_000_000, TrancheType = "Offered", CashflowType = "PI",
            CouponType = "Residual", FixedCoupon = 0.0, SubordinationOrder = 2, FirstPayDate = FirstPayDate,
            PayFrequency = 12, PayDay = FirstPayDate.Day
        });
        request.Deal.Tranches.Add(new TrancheDto
        {
            TrancheName = "XS", OriginalBalance = PoolBalance, TrancheType = "Offered", CashflowType = "IO",
            CouponType = "ExcessInterest", FixedCoupon = 0.0, ClassReference = "XS", SubordinationOrder = 3,
            FirstPayDate = FirstPayDate, PayFrequency = 12, PayDay = FirstPayDate.Day
        });
        foreach (var step in request.Deal.UnifiedWaterfall!.Steps.Where(s => s.Type == "PRINCIPAL"))
            step.Default = Seq("A1", "B1", "SUB");
        Step(request, "WRITEDOWN").Structure = Seq("SUB", "B1", "A1");
        request.Deal.UnifiedWaterfall.CoverageCascade = new List<CoverageLevelDto>
        {
            new() { Level = "A", Tranches = new List<string> { "A1" }, OcTriggerPct = 120 },
            new() { Level = "B", Tranches = new List<string> { "A1", "B1" }, OcTriggerPct = 110 },
            new() { Level = "SUB", Tranches = new List<string> { "A1", "B1", "SUB" }, OcTriggerPct = 100 }
        };
        var r = Run(request);

        var cured = pool.Sum(p => p.NetInterest - r.TrancheCashflows.Values.SelectMany(rows => rows)
            .Where(c => c.CashflowDate == p.CashflowDate).Sum(c => c.Interest));
        cured.Should().BeGreaterThan(500_000, "the fixture: the cure pays principal out of interest");
        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Fact]
    public void A_tranche_filed_under_another_class_shows_as_paid_past_the_balance()
    {
        // A mis-stated deal: a 40M tranche filed under an 80M class, so the class's tranche rows
        // record half as much again as the class is paid. The cash is spent as recorded; the
        // ledger says how much of it had no balance behind it.
        var request = Deal(Losses(0), order: StripFirstOrder, seniorBalance: 80_000_000, withTurbo: false);
        var junior = request.Deal.Tranches.Single(t => t.TrancheName == "B1");
        junior.OriginalBalance = 40_000_000;
        junior.ClassReference = "A1";
        var ledger = Run(request).CashLedger!;

        ledger.Steps.Sum(s => s.PaidPastBalance).Should().BeGreaterThan(1_000_000);
        ledger.Steps.Where(s => !s.Step.StartsWith("PRINCIPAL")).Should().OnlyContain(s => s.PaidPastBalance == 0,
            "it is reported on the steps that paid principal and not carried to the next line");
    }

    // ---- Where the rest goes ----------------------------------------------------------------------

    private static TrancheDto Certificate(string name, double balance, int subOrder = 9) => new()
    {
        TrancheName = name, OriginalBalance = balance, TrancheType = "Certificate", CashflowType = "PI",
        CouponType = "None", SubordinationOrder = subOrder, FirstPayDate = FirstPayDate
    };

    /// <summary>A pool of 100M at 8% repaying the stated share of its balance a month, with no losses.</summary>
    private static List<PeriodCashflowDto> Repaying(int periods, double share)
    {
        var pool = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        for (var i = 0; i < periods; i++)
        {
            var interest = balance * 8.0 / 1200;
            var scheduled = balance * share;
            pool.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = "1", BeginBalance = balance,
                Balance = balance - scheduled, ScheduledPrincipal = scheduled, Interest = interest,
                NetInterest = interest, Wac = 8.0
            });
            balance -= scheduled;
        }

        return pool;
    }

    private static WaterfallRequest Plain(List<PeriodCashflowDto> pool, List<TrancheDto> tranches,
        PayableStructureDto interest, PayableStructureDto principal, PayableStructureDto writedown,
        PayableStructureDto release, string waterfallOrder = "standard") => new()
    {
        ProjectionDate = FirstPayDate.AddMonths(-1),
        CollateralCashflows = pool,
        Deal = new DealDto
        {
            DealName = "PRINCIPAL_RECEIPT_TEST", WaterfallType = "ComposableStructure",
            ClosingDate = FirstPayDate.AddMonths(-1), Tranches = tranches,
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = StripFirstOrder.ToList(), WaterfallOrder = waterfallOrder,
                Steps = new List<WaterfallStepDto>
                {
                    new() { Type = "INTEREST", Structure = interest },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = principal },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = principal },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = principal },
                    new() { Type = "WRITEDOWN", Structure = writedown },
                    new() { Type = "EXCESS_RELEASE", Structure = release }
                }
            }
        }
    };

    [Fact]
    public void Principal_no_structure_places_is_left_while_a_note_has_a_balance_and_not_paid_to_the_certificate()
    {
        // The principal structures name A alone and leave B out: a mis-stated deal. Once A
        // retires, the principal reaches no note. It is not the certificate's while B still
        // stands, so it is left, and the ledger says so: that is the sign the deal is wrong.
        var pool = Repaying(10, 0.10);
        var r = Run(Plain(pool,
            new List<TrancheDto>
            {
                Note("A", 30_000_000, 0), Note("B", 60_000_000, 1), Certificate("CERT", 10_000_000),
                new()
                {
                    TrancheName = "R", OriginalBalance = 0, TrancheType = "Offered", CashflowType = "PI",
                    CouponType = "Residual", FixedCoupon = 0.0, SubordinationOrder = 10, FirstPayDate = FirstPayDate,
                    PayFrequency = 12, PayDay = FirstPayDate.Day
                }
            },
            Seq("A", "B"), Seq("A"), Seq("B", "A"), Seq("CERT")));

        Principal(r, "A").Should().BeApproximately(30_000_000, 0.01);
        Principal(r, "R").Should().BeApproximately(0, 0.01, "nor is it the residual's beside a certificate");
        r.TrancheCashflows["B"].Last().Balance.Should().BeApproximately(60_000_000, 0.01, "the fixture: B is never paid");
        Principal(r, "CERT").Should().BeApproximately(0, 0.01, "a note still has a balance");
        r.CashLedger!.LeftPrincipal.Should().BeApproximately(
            pool.Sum(p => p.ScheduledPrincipal) - 30_000_000, 0.01, "the principal no structure placed");
        EveryStepBalances(r.CashLedger!);
        (ResponsePaysOut(r, pool) + r.CashLedger.LeftPrincipal + r.CashLedger.LeftInterest).Should()
            .BeApproximately(pool.Sum(Collected), 0.05);
    }

    [Fact]
    public void Principal_left_once_the_notes_are_gone_is_paid_to_the_certificate()
    {
        // The same pool and a well-stated deal: once both notes retire, the principal is the
        // certificate's and it is paid to it.
        var pool = Repaying(10, 0.10);
        var r = Run(Plain(pool,
            new List<TrancheDto> { Note("A", 30_000_000, 0), Note("B", 30_000_000, 1), Certificate("CERT", 40_000_000) },
            Seq("A", "B"), Seq("A", "B"), Seq("B", "A"), Seq("CERT")));

        (Principal(r, "A") + Principal(r, "B")).Should().BeApproximately(60_000_000, 0.01);
        Principal(r, "CERT").Should().BeApproximately(pool.Sum(p => p.ScheduledPrincipal) - 60_000_000, 0.01);
        r.CashLedger!.LeftPrincipal.Should().BeApproximately(0, 0.01);
        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Theory]
    [InlineData("CERT1")]
    [InlineData("CERT2")]
    public void With_two_certificates_the_one_the_release_names_is_paid_the_principal_left(string named)
    {
        // Each certificate carries the whole overcollateralization as its balance, so the balance
        // cannot say which one is owed. The release structure can.
        var pool = Repaying(10, 0.10);
        var r = Run(Plain(pool,
            new List<TrancheDto>
            {
                Note("A", 60_000_000, 0), Certificate("CERT1", 0, subOrder: 8), Certificate("CERT2", 40_000_000)
            },
            Seq("A"), Seq("A"), Seq("A"), Seq(named)));

        var other = named == "CERT1" ? "CERT2" : "CERT1";
        Principal(r, named).Should().BeApproximately(pool.Sum(p => p.ScheduledPrincipal) - 60_000_000, 0.01);
        Principal(r, other).Should().BeApproximately(0, 0.01);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Theory]
    [InlineData("CERT1", "CERT2")]
    [InlineData("CERT2", "CERT1")]
    public void A_release_naming_both_certificates_pays_the_first_it_names_the_interest_and_the_principal(
        string first, string second)
    {
        // The release names both certificates. Its interest goes to the first it names, and so does
        // the principal left once the notes are gone: the release's order, not the deal's class
        // order. (It used to send the principal to whichever came first among the deal's classes.)
        var pool = Repaying(10, 0.10);
        var r = Run(Plain(pool,
            new List<TrancheDto>
            {
                Note("A", 60_000_000, 0), Certificate("CERT1", 0, subOrder: 8), Certificate("CERT2", 40_000_000)
            },
            Seq("A"), Seq("A"), Seq("A"), Seq(first, second)));

        r.TrancheCashflows[first].Sum(c => c.Interest).Should().BeGreaterThan(1_000_000, "the release pays it");
        r.TrancheCashflows[second].Sum(c => c.Interest).Should().BeApproximately(0, 0.01);
        Principal(r, first).Should().BeApproximately(pool.Sum(p => p.ScheduledPrincipal) - 60_000_000, 0.01);
        Principal(r, second).Should().BeApproximately(0, 0.01);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Fact]
    public void A_reserve_draw_the_notes_cannot_take_goes_back_to_the_reserve()
    {
        // The pool loses 1.5M a month with nothing recovered, so the notes stand above it and
        // the reserve is drawn to pay them down. The scheduled structure is a single class that
        // retires; what it cannot take of the draw goes back to the reserve. It used to leave the
        // reserve and reach no one.
        var pool = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        for (var i = 0; i < 8; i++)
        {
            var interest = balance * 8.0 / 1200;
            var scheduled = balance * 0.02;
            const double defaulted = 1_500_000;
            pool.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = "1", BeginBalance = balance,
                Balance = balance - scheduled - defaulted, ScheduledPrincipal = scheduled, Interest = interest,
                NetInterest = interest, DefaultedPrincipal = defaulted, CollateralLoss = defaulted, Wac = 8.0
            });
            balance -= scheduled + defaulted;
        }

        var request = Plain(pool,
            new List<TrancheDto>
            {
                Note("A", 3_000_000, 0), Note("B", 97_000_000, 1),
                new()
                {
                    TrancheName = "RESERVE", OriginalBalance = 5_000_000, TrancheType = "CapFundsReserve",
                    CashflowType = "Reserve", CouponType = "None", FirstPayDate = FirstPayDate,
                    PayDay = FirstPayDate.Day,
                    ReserveConfig = new ReserveAccountConfigDto
                    {
                        TargetPct = 0.05, TargetBase = "CutoffPoolBalance", CapAtNoteBalance = true,
                        CutoffPoolBalance = PoolBalance
                    }
                },
                Certificate("CERT", 0)
            },
            Seq("A", "B"), Seq("A", "B"), Seq("B", "A"), Single("CERT"));
        Step(request, "PRINCIPAL", "scheduled").Default = Single("A");
        request.Deal.UnifiedWaterfall!.ExecutionOrder!.Insert(5, "RESERVE_DEPOSIT");
        request.Deal.UnifiedWaterfall.Steps.Add(new WaterfallStepDto { Type = "RESERVE_DEPOSIT" });
        var r = Run(request);

        Principal(r, "A").Should().BeApproximately(3_000_000, 0.01, "the fixture: A retires");
        EveryStepBalances(r.CashLedger!);

        // The reserve's own row shows the draw that was spent, not a larger draw and a deposit:
        // its deposits are what the deposit step put in (this fixture releases no excess), and
        // the first period's draw is the 1,000,000 that A could take, not the 1,500,000 asked.
        var reserve = r.TrancheCashflows["RESERVE"];
        reserve.Sum(row => row.ScheduledPrincipal).Should().BeApproximately(
            r.CashLedger!.Steps.Single(s => s.Step == "RESERVE_DEPOSIT").Paid, 0.01, "deposits are the deposit step's");
        reserve[0].UnscheduledPrincipal.Should().BeApproximately(-1_000_000, 0.01, "the draw A took");
        foreach (var row in reserve)
            (row.ScheduledPrincipal + row.UnscheduledPrincipal).Should().BeApproximately(row.Balance - row.BeginBalance,
                0.01, "deposits less draws are what the balance moved by");
    }

    [Fact]
    public void A_coverage_cure_that_pays_a_funded_residual_spends_what_it_paid()
    {
        // One period, one coverage level naming a funded residual alone. The level fails and the
        // cure pays the residual principal out of interest. Measured on the notes' balance, which
        // leaves the residual out, the cure paid nothing and the strip was paid the same interest
        // again: 1,500,000 out on 750,000 collected.
        var pool = new List<PeriodCashflowDto>
        {
            new()
            {
                Period = 1, CashflowDate = FirstPayDate, GroupNum = "1", BeginBalance = PoolBalance,
                Balance = PoolBalance - 2_000_000, Interest = 750_000, NetInterest = 750_000,
                DefaultedPrincipal = 2_000_000, CollateralLoss = 2_000_000, Wac = 9.0
            }
        };
        var request = Plain(pool,
            new List<TrancheDto>
            {
                Note("A", 1_000_000, 0),
                new()
                {
                    TrancheName = "SUB", OriginalBalance = 99_000_000, TrancheType = "Offered", CashflowType = "PI",
                    CouponType = "Residual", FixedCoupon = 0.0, SubordinationOrder = 1, FirstPayDate = FirstPayDate,
                    PayFrequency = 12, PayDay = FirstPayDate.Day
                },
                new()
                {
                    TrancheName = "XS", OriginalBalance = PoolBalance, TrancheType = "Offered", CashflowType = "IO",
                    CouponType = "ExcessInterest", FixedCoupon = 0.0, ClassReference = "XS", SubordinationOrder = 2,
                    FirstPayDate = FirstPayDate, PayFrequency = 12, PayDay = FirstPayDate.Day
                }
            },
            Seq("A"), Seq("A", "SUB"), Seq("A"), Seq("XS"));
        request.Deal.Tranches[0].FixedCoupon = 4.0;
        request.Deal.UnifiedWaterfall!.CoverageCascade = new List<CoverageLevelDto>
        {
            new() { Level = "SUB", Tranches = new List<string> { "SUB" }, OcTriggerPct = 100 }
        };
        var r = Run(request);

        (750_000 - r.TrancheCashflows.Values.Sum(rows => rows.Sum(c => c.Interest))).Should()
            .BeGreaterThan(100_000, "the fixture: the cure pays principal out of interest");
        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Theory]
    [InlineData("interestFirst")]
    [InlineData("principalFirst")]
    public void An_interleaved_order_with_a_certificate_ahead_of_a_note_pays_out_what_it_collects(string order)
    {
        // Interleaved, each child of the principal structures is handed the principal directly. A
        // certificate is not held to its balance, so placed ahead of a note it takes what the
        // note behind it would have (a known limit; the standard order pays the note). What
        // this holds is that the cash adds up: measured on balances, the step paid the
        // certificate and the note behind it as well.
        var pool = Losses(600_000);
        var r = Run(Plain(pool,
            new List<TrancheDto> { Note("A", 5_000_000, 0), Note("B", 95_000_000, 1), Certificate("CERT", 0) },
            Seq("A", "B"), Seq("A", "CERT", "B"), Seq("B", "A"), Seq("CERT"), order));

        Principal(r, "A").Should().BeApproximately(5_000_000, 0.01, "the fixture: A retires");
        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Theory]
    [InlineData("interestFirst")]
    [InlineData("principalFirst")]
    public void An_interleaved_order_with_an_interest_only_class_among_the_notes_pays_out_what_it_collects(string order)
    {
        // An interest-only class handed principal records none and its notional falls. Measured on
        // its balance, the step spent principal that reached no one. Scheduled, prepaid and
        // recovered principal all pass it.
        var pool = Losses(600_000);
        var r = Run(Plain(pool,
            new List<TrancheDto>
            {
                Note("A", 5_000_000, 0), Note("B", 95_000_000, 2),
                new()
                {
                    TrancheName = "IO", OriginalBalance = 50_000_000, TrancheType = "Offered", CashflowType = "IO",
                    CouponType = "Fixed", FixedCoupon = 0.0, SubordinationOrder = 1, FirstPayDate = FirstPayDate,
                    PayFrequency = 12, PayDay = FirstPayDate.Day
                }
            },
            Seq("A", "B"), Seq("A", "IO", "B"), Seq("B", "A"), Seq("B"), order));

        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Fact]
    public void Principal_paid_to_a_funded_residual_is_spent_without_a_wider_balance_measure()
    {
        // The case the principal steps' own balance measure was widened for (a funded residual
        // paid exactly twice). The receipt covers it with no measure at all.
        var request = Deal(Losses(0), order: StripFirstOrder);
        request.Deal.UnifiedWaterfall!.Steps.RemoveAll(s => s.Type == "EXCESS_TURBO");
        request.Deal.Tranches.Add(new TrancheDto
        {
            TrancheName = "R", OriginalBalance = 5_000_000, TrancheType = "Offered", CashflowType = "PI",
            CouponType = "Residual", FixedCoupon = 0.0, SubordinationOrder = 9, FirstPayDate = FirstPayDate,
            PayFrequency = 12, PayDay = FirstPayDate.Day
        });
        foreach (var step in request.Deal.UnifiedWaterfall.Steps.Where(s => s.Type == "PRINCIPAL"))
            step.Default = Seq("R", "A1", "B1");
        var r = Run(request);

        Principal(r, "R").Should().BeApproximately(5_000_000, 0.01, "the residual is paid its balance once");
        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, request.CollateralCashflows);
    }

    [Fact]
    public void A_deal_whose_classes_are_paid_within_their_balances_reports_nothing_past_balance()
    {
        var pool = Losses(600_000);
        var r = Run(Deal(pool, order: StripFirstOrder, withTurbo: false));
        r.CashLedger!.Steps.Should().OnlyContain(s => s.PaidPastBalance == 0);
        EveryStepBalances(r.CashLedger!);
        EveryPeriodPaysOutWhatItCollected(r, pool);
    }

    [Fact]
    public void The_ledger_field_binds_under_its_stated_name()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new CashLedgerStepDto { PaidPastBalance = 12.5 },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        json.Should().Contain("\"paidPastBalance\":12.5");
    }

    // ------------------------------------------------------------------------------------------

    private static void EveryStepBalances(CashLedgerDto ledger)
    {
        foreach (var step in ledger.Steps)
        {
            step.TakenNotPaid.Should().BeApproximately(0, 0.01, step.Step);
            step.PaidNotTaken.Should().BeApproximately(0, 0.01, step.Step);
            step.PeriodsOff.Should().Be(0, step.Step);
        }

        (ledger.Steps.Sum(s => s.Paid) + ledger.LeftInterest + ledger.LeftPrincipal).Should()
            .BeApproximately(ledger.Collected, 0.01,
            "what was collected was paid or is left");
    }

    private static double Collected(PeriodCashflowDto p) =>
        p.NetInterest + p.ScheduledPrincipal + p.UnscheduledPrincipal + p.RecoveryPrincipal;

    /// <summary>Every class's interest, principal and expense in the response, over the run.</summary>
    private static double ResponsePaysOut(WaterfallResponse r, List<PeriodCashflowDto> pool) =>
        r.TrancheCashflows.Values.SelectMany(rows => rows)
            .Sum(c => c.Interest + c.ScheduledPrincipal + c.UnscheduledPrincipal + c.Expense);

    /// <summary>Collected is what the steps paid, plus their gaps, plus what was left, less what was overdrawn.</summary>
    private static void LedgerAddsUp(CashLedgerDto ledger) =>
        (ledger.Steps.Sum(s => s.Paid) + ledger.Steps.Sum(s => s.TakenNotPaid - s.PaidNotTaken) +
         ledger.LeftInterest + ledger.LeftPrincipal - ledger.Overdrawn)
        .Should().BeApproximately(ledger.Collected, 0.05, "the ledger's identity");

    /// <summary>
    /// The same check as the response one, on a deal built straight into the engine: certificate
    /// classes read from their class rows and every other class from its tranche rows, which is
    /// where the response reads them.
    /// </summary>
    private static void EveryPeriodPaysOutWhatItCollected(DealCashflows cf, CollateralCashflows pool)
    {
        static double Cash(TrancheCashflow c) =>
            c.Interest + c.ScheduledPrincipal + c.UnscheduledPrincipal + c.Expense;

        var rows = cf.TrancheCashflows
            .Where(t => t.Key.TrancheTypeEnum != TrancheTypeEnum.Certificate)
            .SelectMany(t => t.Value.Cashflows.Values)
            .Concat(cf.ClassCashflows.Where(c => c.Key.TrancheTypeEnum == TrancheTypeEnum.Certificate)
                .SelectMany(c => c.Value.Cashflows.Values));
        var paidOn = rows.GroupBy(c => c.CashflowDate.Date).ToDictionary(g => g.Key, g => g.Sum(Cash));
        foreach (var period in pool.PeriodCashflows)
        {
            var collected = period.NetInterest + period.ScheduledPrincipal + period.UnscheduledPrincipal +
                            period.RecoveryPrincipal;
            paidOn.GetValueOrDefault(period.CashflowDate.Date).Should().BeApproximately(collected, 0.01,
                $"{period.CashflowDate:yyyy-MM-dd}");
        }
    }

    private static double Principal(WaterfallResponse r, string name) =>
        r.TrancheCashflows[name].Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal);

    /// <summary>
    /// Every class's interest and principal in a period, added up from the response, is what the
    /// pool collected in that period. It reads nothing the receipt or the ledger reads.
    /// </summary>
    private static void EveryPeriodPaysOutWhatItCollected(WaterfallResponse r, List<PeriodCashflowDto> pool)
    {
        // By date, not by position: a class has a row only for the periods it was paid in.
        var paidOn = r.TrancheCashflows.Values.SelectMany(rows => rows)
            .GroupBy(row => row.CashflowDate.Date)
            .ToDictionary(g => g.Key,
                g => g.Sum(row => row.Interest + row.ScheduledPrincipal + row.UnscheduledPrincipal + row.Expense));
        paidOn.Keys.Should().BeSubsetOf(pool.Select(p => p.CashflowDate.Date), "no class is paid on another date");
        foreach (var period in pool)
        {
            var collected = period.NetInterest + period.ScheduledPrincipal + period.UnscheduledPrincipal +
                            period.RecoveryPrincipal;
            paidOn.GetValueOrDefault(period.CashflowDate.Date).Should().BeApproximately(collected, 0.01,
                $"{period.CashflowDate:yyyy-MM-dd}");
        }
    }

    private static WaterfallResponse Run(WaterfallRequest request)
    {
        request.ReportCashLedger = true;
        var result = new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(request).Result;
        (result as BadRequestObjectResult)?.Value.Should().BeNull("the waterfall request should succeed");
        return (WaterfallResponse)((OkObjectResult)result!).Value!;
    }

    private static readonly List<string> OcOrder = new()
    {
        "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN",
        "EXCESS_TURBO", "EXCESS_RELEASE"
    };

    private static readonly List<string> StripFirstOrder = new()
    {
        "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN",
        "EXCESS_RELEASE"
    };

    private static WaterfallStepDto Step(WaterfallRequest request, string type, string? source = null) =>
        request.Deal.UnifiedWaterfall!.Steps.Single(s => s.Type == type && (source == null || s.Source == source));

    /// <summary>Two notes and an excess-spread strip paid from the release step.</summary>
    private static WaterfallRequest Deal(List<PeriodCashflowDto> pool, List<string> order,
        double seniorBalance = 70_000_000, bool withTurbo = true, bool withResidual = false)
    {
        var steps = new List<WaterfallStepDto>
        {
            new() { Type = "INTEREST", Structure = Seq("A1", "B1") },
            new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A1", "B1") },
            new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A1", "B1") },
            new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A1", "B1") },
            new() { Type = "WRITEDOWN", Structure = Seq("B1", "A1") },
            new() { Type = "EXCESS_RELEASE", Structure = Seq("XS") }
        };
        if (withTurbo)
            steps.Insert(4, new WaterfallStepDto { Type = "EXCESS_TURBO", Structure = Seq("A1", "B1") });

        var request = new WaterfallRequest
        {
            ProjectionDate = FirstPayDate.AddMonths(-1),
            CollateralCashflows = pool,
            Deal = new DealDto
            {
                DealName = "PRINCIPAL_RECEIPT_TEST",
                WaterfallType = "ComposableStructure",
                ClosingDate = FirstPayDate.AddMonths(-1),
                Tranches = new List<TrancheDto>
                {
                    Note("A1", seniorBalance, 0),
                    Note("B1", PoolBalance - seniorBalance, 1),
                    new()
                    {
                        TrancheName = "XS", OriginalBalance = PoolBalance, TrancheType = "Offered",
                        CashflowType = "IO", CouponType = "ExcessInterest", FixedCoupon = 0.0,
                        ClassReference = "XS", SubordinationOrder = 2, FirstPayDate = FirstPayDate,
                        PayFrequency = 12, PayDay = FirstPayDate.Day
                    }
                },
                UnifiedWaterfall = new UnifiedWaterfallDto { ExecutionOrder = order.ToList(), Steps = steps }
            }
        };
        if (withResidual)
            request.Deal.Tranches.Add(new TrancheDto
            {
                TrancheName = "R", OriginalBalance = 0, TrancheType = "Offered", CashflowType = "PI",
                CouponType = "Residual", FixedCoupon = 0.0, SubordinationOrder = 9, FirstPayDate = FirstPayDate,
                PayFrequency = 12, PayDay = FirstPayDate.Day
            });
        return request;
    }

    private static TrancheDto Note(string name, double balance, int subOrder) => new()
    {
        TrancheName = name, OriginalBalance = balance, TrancheType = "Offered", CashflowType = "PI",
        CouponType = "Fixed", FixedCoupon = NoteCoupon, SubordinationOrder = subOrder,
        FirstPayDate = FirstPayDate, PayFrequency = 12, PayDay = FirstPayDate.Day
    };

    private static PayableStructureDto Seq(params string[] tranches) =>
        new() { Type = "SEQ", Tranches = tranches.ToList() };

    private static PayableStructureDto Single(string tranche) => new() { Type = "SINGLE", Tranche = tranche };

    /// <summary>Twelve months: 1% scheduled, 200,000 prepaid, and the stated defaults at 50% recovery.</summary>
    private static List<PeriodCashflowDto> Losses(double defaulted)
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        for (var i = 0; i < 12; i++)
        {
            var interest = balance * Wac / 1200;
            var scheduled = balance * 0.01;
            const double prepaid = 200_000;
            var end = balance - scheduled - prepaid - defaulted;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = "1", BeginBalance = balance,
                Balance = end, ScheduledPrincipal = scheduled, UnscheduledPrincipal = prepaid, Interest = interest,
                NetInterest = interest, DefaultedPrincipal = defaulted, RecoveryPrincipal = defaulted * 0.5,
                CollateralLoss = defaulted * 0.5, Wac = Wac
            });
            balance = end;
        }

        return periods;
    }
}
