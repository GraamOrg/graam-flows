using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;
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
/// The tests are on synthetic deals. Each reads the cash ledger: with receipts every step that
/// pays principal takes off its funds exactly what the classes recorded.
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
        ["PROFORMA"] = m => new ProformaStructure(m[0], 0.5, m[1], 0.3, m[2], 0.2),
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
                structure.Should().Be("PROFORMA");
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
    }

    // ---- A step spends what the payment reports -------------------------------------------------

    [Theory]
    [InlineData("scheduled", "PRINCIPAL_SCHEDULED")]
    [InlineData("unscheduled", "PRINCIPAL_UNSCHEDULED")]
    [InlineData("recovery", "PRINCIPAL_RECOVERY")]
    public void A_principal_step_paying_a_class_more_than_it_holds_spends_the_whole_payment(string source,
        string stepName)
    {
        // One source's structure is the senior class alone, which retires in the third period.
        // It goes on being paid that source's principal. Its rows record the cash, so the step
        // has spent it: it is not left over to be paid to anyone else.
        var request = Deal(Losses(600_000), order: StripFirstOrder, seniorBalance: 2_500_000, withTurbo: false);
        Step(request, "PRINCIPAL", source).Default = Single("A1");
        var ledger = Run(request).CashLedger!;

        ledger.Steps.Single(s => s.Step == stepName).PaidPastBalance.Should().BeGreaterThan(1_000_000,
            "the fixture: the retired class is paid past its balance by this step");
        ledger.Steps.Where(s => s.Step != stepName).Should().OnlyContain(s => s.PaidPastBalance == 0,
            "the overpayment is reported on the step that made it");
        EveryStepBalances(ledger);
        ledger.LeftPrincipal.Should().BeApproximately(0, 0.01);
    }

    [Fact]
    public void An_OC_turbo_paying_a_class_more_than_it_holds_spends_the_whole_payment()
    {
        // The turbo's structure is the senior class alone. Once it retires it is still paid; its
        // balance stays at zero, so measured on balances the turbo paid nothing and the interest
        // was paid out a second time by the release.
        var request = Deal(Losses(600_000), order: OcOrder, seniorBalance: 4_000_000);
        var turbo = Step(request, "EXCESS_TURBO");
        turbo.Structure = Single("A1");
        turbo.OcTarget = new OcTargetDto { TargetPct = 0.05 };
        var ledger = Run(request).CashLedger!;

        var line = ledger.Steps.Single(s => s.Step == "EXCESS_TURBO");
        line.Paid.Should().BeGreaterThan(1_000_000, "the fixture: the turbo pays");
        line.PaidPastBalance.Should().BeGreaterThan(500_000, "the fixture: it goes on paying the retired class");
        ledger.Steps.Where(s => s.Step != "EXCESS_TURBO").Should().OnlyContain(s => s.PaidPastBalance == 0);
        EveryStepBalances(ledger);
    }

    [Theory]
    [InlineData("interestFirst")]
    [InlineData("principalFirst")]
    public void An_interleaved_order_paying_a_class_more_than_it_holds_spends_the_whole_payment(string order)
    {
        // Interleaved, each level's class is paid directly, and a class is not a structure: it
        // records all it is handed. The senior class retires in the third period.
        var request = Deal(Losses(0), order: new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN",
            "EXCESS_RELEASE"
        }, seniorBalance: 2_500_000);
        request.Deal.UnifiedWaterfall!.WaterfallOrder = order;
        request.Deal.UnifiedWaterfall.Steps.RemoveAll(s => s.Type == "EXCESS_TURBO");
        var ledger = Run(request).CashLedger!;

        ledger.Steps.Single(s => s.Step == "INTEREST").PaidPastBalance.Should().BeGreaterThan(100_000,
            "the fixture: the retired class is paid past its balance");
        EveryStepBalances(ledger);
    }

    [Fact]
    public void A_forced_paydown_in_a_principal_step_spends_what_it_paid()
    {
        // A forced paydown pays its class in full whatever it is handed. Measured on balances
        // that was right only by accident; the receipt is what the classes recorded.
        var request = Deal(Losses(600_000), order: StripFirstOrder);
        request.Deal.UnifiedWaterfall!.Steps.RemoveAll(s => s.Type == "EXCESS_TURBO");
        Step(request, "PRINCIPAL", "recovery").Default = new PayableStructureDto
            { Type = "FORCE_PAYDOWN", Forced = Single("B1"), Support = Seq("A1") };
        var ledger = Run(request).CashLedger!;

        foreach (var step in ledger.Steps.Where(s => s.Step.StartsWith("PRINCIPAL")))
        {
            step.TakenNotPaid.Should().BeApproximately(0, 0.01, step.Step);
            step.PaidNotTaken.Should().BeApproximately(0, 0.01, step.Step);
        }
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

        r.TrancheCashflows["R"].Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal).Should()
            .BeApproximately(5_000_000, 0.01, "the residual is paid its balance once");
        EveryStepBalances(r.CashLedger!);
    }

    [Fact]
    public void A_deal_whose_classes_are_paid_within_their_balances_reports_nothing_past_balance()
    {
        var ledger = Run(Deal(Losses(600_000), order: StripFirstOrder, withTurbo: false)).CashLedger!;
        ledger.Steps.Should().OnlyContain(s => s.PaidPastBalance == 0);
        EveryStepBalances(ledger);
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

        (ledger.Steps.Sum(s => s.Paid) + ledger.LeftInterest + ledger.LeftPrincipal +
         ledger.LeftPrincipalBesideCertificate).Should().BeApproximately(ledger.Collected, 0.01,
            "what was collected was paid or is left");
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
        double seniorBalance = 70_000_000, bool withTurbo = true)
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
