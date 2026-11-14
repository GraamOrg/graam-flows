using System.Reflection;
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
/// How excess spread meets losses is stated by the deal's steps, not decided by the engine.
///
/// A deal whose documents pay principal out of excess up to the period's losses states
/// EXCESS_TURBO with <c>target: 0</c> ahead of a WRITEDOWN sized <c>overPool</c>, and pays the
/// excess-spread strip from EXCESS_RELEASE. The turbo pays the notes down to the pool out of the
/// interest left; the write-down takes only what the notes still stand above the pool by.
///
/// The engine's built-in rule does the same thing for a deal that states none of this: the strip
/// sweeps the excess inside INTEREST and gives back what the period's loss needs. Stated in steps,
/// the same deal has to return the same numbers, which is what lets stored deals be moved from the
/// rule to the steps without their results moving.
/// </summary>
public class ExcessCoversLossesStepsTests
{
    private static readonly DateTime FirstPayDate = new(2026, 2, 25);
    private const double A1Balance = 70_000_000;
    private const double B1Balance = 30_000_000;
    private const double PoolBalance = A1Balance + B1Balance;
    private const double Wac = 9.0;
    private const double NoteCoupon = 5.0;

    // ---- Same numbers as the built-in rule ---------------------------------------------------

    [Fact]
    public void Stated_in_steps_the_deal_returns_the_built_in_rules_numbers()
    {
        var pool = MixedLosses();
        var rule = Run(BuiltInRule(pool));
        var steps = Run(Steps(pool));

        // Anti-vacuity: the pool has to exercise both sides of the rule, a loss the excess covers
        // and a loss it does not.
        rule.TrancheCashflows["B1"].Sum(c => c.Writedown).Should().BeGreaterThan(1_000_000);
        rule.TrancheCashflows["A1"].Sum(c => c.UnscheduledPrincipal).Should().BeGreaterThan(1_000_000);

        foreach (var name in new[] { "A1", "B1" })
        {
            var a = rule.TrancheCashflows[name];
            var b = steps.TrancheCashflows[name];
            b.Should().HaveSameCount(a);
            for (var i = 0; i < a.Count; i++)
            {
                var at = $"{name} period {i + 1}";
                b[i].Interest.Should().BeApproximately(a[i].Interest, 0.01, at);
                b[i].ScheduledPrincipal.Should().BeApproximately(a[i].ScheduledPrincipal, 0.01, at);
                b[i].UnscheduledPrincipal.Should().BeApproximately(a[i].UnscheduledPrincipal, 0.01, at);
                b[i].Writedown.Should().BeApproximately(a[i].Writedown, 0.01, at);
                b[i].Balance.Should().BeApproximately(a[i].Balance, 0.01, at);
            }
        }

        var xsRule = rule.TrancheCashflows["XS"];
        var xsSteps = steps.TrancheCashflows["XS"];
        for (var i = 0; i < xsRule.Count; i++)
            xsSteps[i].Interest.Should().BeApproximately(xsRule[i].Interest, 0.01,
                $"XS period {i + 1}: the strip is paid the excess less what covered the loss");

        // The one column that differs by design: the rule books the loss the strip gave up as a
        // write-down on the strip. Paid from a release step, the strip is simply paid less.
        xsSteps.Should().OnlyContain(c => Math.Abs(c.Writedown) < 0.01);
    }

    // ---- What the steps do --------------------------------------------------------------------

    [Fact]
    public void A_loss_smaller_than_the_excess_writes_nothing_down_and_pays_the_strip_the_rest()
    {
        var pool = Collateral(defaulted: _ => 200_000, recoveryRate: 0.5);
        var r = Run(Steps(pool));

        for (var i = 0; i < pool.Count - 1; i++)
        {
            var loss = pool[i].DefaultedPrincipal - pool[i].RecoveryPrincipal;
            var excess = Excess(pool[i], r, i);
            excess.Should().BeGreaterThan(loss, "the fixture: the excess covers this period's loss");

            r.TrancheCashflows["B1"][i].Writedown.Should().BeApproximately(0, 0.01, $"period {i + 1}");
            r.TrancheCashflows["A1"][i].Writedown.Should().BeApproximately(0, 0.01, $"period {i + 1}");
            r.TrancheCashflows["XS"][i].Interest.Should().BeApproximately(excess - loss, 0.01,
                $"period {i + 1}: the strip is paid the excess less the loss");
            NotesAt(r, i).Should().BeApproximately(pool[i].Balance, 0.01,
                $"period {i + 1}: the excess paid the notes down to the pool");
        }
    }

    [Fact]
    public void A_larger_loss_writes_down_only_what_the_excess_did_not_cover_junior_class_first()
    {
        var pool = Collateral(defaulted: _ => 2_000_000, recoveryRate: 0.5);
        var r = Run(Steps(pool));

        for (var i = 0; i < 6; i++)
        {
            var loss = pool[i].DefaultedPrincipal - pool[i].RecoveryPrincipal;
            var excess = Excess(pool[i], r, i);
            excess.Should().BeLessThan(loss, "the fixture: the loss is more than the excess");

            r.TrancheCashflows["B1"][i].Writedown.Should().BeApproximately(loss - excess, 0.01,
                $"period {i + 1}: the junior class takes the loss less the excess");
            r.TrancheCashflows["A1"][i].Writedown.Should().BeApproximately(0, 0.01, $"period {i + 1}");
            r.TrancheCashflows["XS"][i].Interest.Should().BeApproximately(0, 0.01,
                $"period {i + 1}: all the excess went to the loss");
            NotesAt(r, i).Should().BeApproximately(pool[i].Balance, 0.01, $"period {i + 1}");
        }
    }

    [Fact]
    public void With_the_notes_under_the_pool_the_gap_takes_a_later_loss()
    {
        // Period 1 recovers 600k on no defaults: the notes are paid it and the pool does not
        // fall, so the notes stand 600k under the pool. Period 2 then loses 400k.
        var pool = Collateral(defaulted: i => i == 1 ? 400_000 : 0, recoveryRate: 0,
            recovery: i => i == 0 ? 600_000 : 0);
        var r = Run(Steps(pool));

        (pool[0].Balance - NotesAt(r, 0)).Should().BeApproximately(600_000, 0.01);
        r.TrancheCashflows["B1"][1].Writedown.Should().BeApproximately(0, 0.01);
        r.TrancheCashflows["A1"][1].UnscheduledPrincipal.Should().BeApproximately(0, 0.01,
            "the notes are already under the pool, so the turbo pays nothing");
        r.TrancheCashflows["XS"][1].Interest.Should().BeApproximately(Excess(pool[1], r, 1), 0.01,
            "the strip keeps the whole excess");
        (pool[1].Balance - NotesAt(r, 1)).Should().BeApproximately(200_000, 0.01);
    }

    [Fact]
    public void Documents_that_pay_the_strip_first_state_no_turbo_and_the_notes_take_the_whole_loss()
    {
        var pool = Collateral(defaulted: _ => 200_000, recoveryRate: 0.5);
        var r = Run(StripFirst(pool));

        for (var i = 0; i < 6; i++)
        {
            var loss = pool[i].DefaultedPrincipal - pool[i].RecoveryPrincipal;
            r.TrancheCashflows["B1"][i].Writedown.Should().BeApproximately(loss, 0.01, $"period {i + 1}");
            r.TrancheCashflows["XS"][i].Interest.Should().BeApproximately(Excess(pool[i], r, i), 0.01,
                $"period {i + 1}: the strip is paid the whole excess");
        }
    }

    [Fact]
    public void Stating_periodLoss_is_the_same_as_stating_no_size()
    {
        var pool = MixedLosses();
        var unstated = Run(StripFirst(pool));
        var stated = Run(StripFirst(pool, size: "periodLoss"));

        foreach (var name in new[] { "A1", "B1", "XS" })
            for (var i = 0; i < unstated.TrancheCashflows[name].Count; i++)
            {
                var a = unstated.TrancheCashflows[name][i];
                var b = stated.TrancheCashflows[name][i];
                (b.Interest, b.ScheduledPrincipal, b.UnscheduledPrincipal, b.Writedown, b.Balance).Should()
                    .Be((a.Interest, a.ScheduledPrincipal, a.UnscheduledPrincipal, a.Writedown, a.Balance));
            }
    }

    // ---- Refusals -------------------------------------------------------------------------------

    [Theory]
    [InlineData("PRINCIPAL_RECOVERY")]
    [InlineData("PRINCIPAL_SCHEDULED")]
    [InlineData("EXCESS_TURBO")]
    [InlineData("SUPPLEMENTAL_REDUCTION")]
    [InlineData("INTEREST")]
    public void OverPool_is_refused_when_a_step_that_can_pay_the_notes_runs_after_the_writedown(string late)
    {
        var order = new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "EXCESS_TURBO"
        };
        order.Remove(late);
        order.AddRange(new[] { "WRITEDOWN", late, "EXCESS_RELEASE" });

        Refusal(Steps(MixedLosses(), order: order)).Should().Contain("overPool").And.Contain($"{late} runs after");
    }

    [Fact]
    public void Interest_that_pays_the_principal_itself_is_refused_after_an_overPool_writedown()
    {
        // Interleaved, INTEREST is the step that pays all the principal. Left to run after the
        // write-down, the junior class was written down by the principal about to be paid.
        var request = Steps(MixedLosses(),
            order: new List<string> { "EXCESS_TURBO", "WRITEDOWN", "INTEREST", "EXCESS_RELEASE" });
        request.Deal.UnifiedWaterfall!.WaterfallOrder = "interestFirst";

        Refusal(request).Should().Contain("overPool").And.Contain("INTEREST runs after");
    }

    [Theory]
    [InlineData("EXPENSE")]
    [InlineData("RESERVE_DEPOSIT")]
    [InlineData("CAP_CARRYOVER")]
    public void A_step_that_never_pays_the_notes_may_follow_an_overPool_writedown(string after)
    {
        var order = new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "EXCESS_TURBO",
            "WRITEDOWN", after, "EXCESS_RELEASE"
        };
        var plain = Run(Steps(MixedLosses()));
        var with = Run(Steps(MixedLosses(), order: order));

        for (var i = 0; i < plain.TrancheCashflows["B1"].Count; i++)
            with.TrancheCashflows["B1"][i].Writedown.Should()
                .BeApproximately(plain.TrancheCashflows["B1"][i].Writedown, 0.01);
    }

    [Fact]
    public void Two_writedown_steps_sized_differently_are_refused()
    {
        var request = Steps(MixedLosses());
        request.Deal.UnifiedWaterfall!.Steps.Add(
            new WaterfallStepDto { Type = "WRITEDOWN", Structure = Seq("B1", "A1"), Size = "periodLoss" });
        Refusal(request).Should().Contain("state different sizes");
    }

    [Fact]
    public void OverPool_with_no_stated_order_is_refused_and_says_the_order_is_the_default()
    {
        // The default order runs EXCESS_TURBO after WRITEDOWN.
        Refusal(Steps(MixedLosses(), order: new List<string>()))
            .Should().Contain("overPool").And.Contain("states no executionOrder");
    }

    [Fact]
    public void A_zero_target_turbo_ahead_of_a_periodLoss_writedown_is_refused()
    {
        // The turbo would pay the loss as principal and the write-down would take it again.
        Refusal(Steps(MixedLosses(), size: null)).Should().Contain("written down as well");
    }

    [Fact]
    public void A_zero_target_the_order_never_runs_is_refused()
    {
        var order = new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN",
            "EXCESS_RELEASE"
        };
        Refusal(Steps(MixedLosses(), order: order)).Should().Contain("no EXCESS_TURBO step");
    }

    [Fact]
    public void A_target_other_than_zero_is_refused_and_pointed_at_ocTarget()
    {
        Refusal(Steps(MixedLosses(), target: 0.05)).Should().Contain("target 0.05").And.Contain("ocTarget");
    }

    [Fact]
    public void A_zero_target_beside_an_OC_target_is_refused()
    {
        var request = Steps(MixedLosses());
        Turbo(request).OcTarget = new OcTargetDto { TargetPct = 0.02 };
        Refusal(request).Should().Contain("both target 0 and an ocTarget");
    }

    [Fact]
    public void A_zero_target_with_no_structure_is_refused()
    {
        var request = Steps(MixedLosses());
        Turbo(request).Structure = null;
        Refusal(request).Should().Contain("states target 0 and no structure");
    }

    // The same two refusals inside the engine, for a deal that reaches it without the mapper
    // (pay rules rather than steps).

    private static TestDealBuilder RuleDeal() => new TestDealBuilder()
        .WithTranche("A", 70_000_000, NoteCoupon, subOrder: 0)
        .WithTranche("B", 30_000_000, NoteCoupon, subOrder: 1)
        .WithSequentialWaterfall("A", "B")
        .WithExecutionOrder("INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY",
            "EXCESS_TURBO", "WRITEDOWN")
        .WithWritedownSize(WritedownSize.OverPool)
        .WithExcessTurboToPool();

    private static CollateralCashflows OnePeriod() => new TestCollateralBuilder().WithGroupNum("1")
        .WithPeriod(date: TestConstants.DefaultFirstPayDate, beginBalance: 100_000_000,
            scheduledPrincipal: 1_000_000, unscheduledPrincipal: 0, interest: 750_000,
            defaultedPrincipal: 200_000, recoveryPrincipal: 100_000)
        .Build();

    [Fact]
    public void In_the_engine_a_zero_target_with_no_turbo_structure_is_refused()
    {
        var act = () => RuleDeal().BuildAndRun(OnePeriod());
        act.Should().Throw<Exception>().WithMessage("*target of zero and no structure to pay through*");
    }

    [Fact]
    public void In_the_engine_a_zero_target_beside_an_OC_target_is_refused()
    {
        var act = () => RuleDeal()
            .WithPayRule("TurboStruct", "SET_TURBO_STRUCT(SEQ(SINGLE('A'), SINGLE('B')))")
            .WithOcTarget(0.02, 0)
            .BuildAndRun(OnePeriod());
        act.Should().Throw<Exception>().WithMessage("*both a target of zero and an OC target*");
    }

    [Fact]
    public void In_the_engine_the_rule_built_deal_runs_once_it_states_its_turbo_structure()
    {
        // Anti-vacuity for the two refusals above: the same deal, completed, runs and the excess
        // covers the 100k loss.
        var cf = RuleDeal()
            .WithPayRule("TurboStruct", "SET_TURBO_STRUCT(SEQ(SINGLE('A'), SINGLE('B')))")
            .BuildAndRun(OnePeriod()).Cashflows;
        var b = cf.TrancheCashflows.First(t => t.Key.TrancheName == "B").Value.Cashflows.Values.First();
        b.Writedown.Should().BeApproximately(0, 0.01);
    }

    [Fact]
    public void A_size_the_engine_does_not_know_is_refused_not_read_as_the_default()
    {
        Refusal(Steps(MixedLosses(), size: "overpool ")).Should().Contain("size 'overpool '")
            .And.Contain("periodLoss").And.Contain("overPool");
    }

    [Fact]
    public void A_size_or_a_target_on_another_step_is_refused()
    {
        var sized = Steps(MixedLosses());
        sized.Deal.UnifiedWaterfall!.Steps.First(s => s.Type == "INTEREST").Size = "overPool";
        Refusal(sized).Should().Contain("Only the WRITEDOWN step is sized");

        var targeted = Steps(MixedLosses());
        targeted.Deal.UnifiedWaterfall!.Steps.First(s => s.Type == "EXCESS_RELEASE").Target = 0;
        Refusal(targeted).Should().Contain("Only the EXCESS_TURBO step has one");
    }

    // ---- The request reaches the engine through both readers ---------------------------------

    public static IEnumerable<object[]> Readers => new List<object[]>
    {
        new object[] { typeof(WaterfallController) },
        new object[] { typeof(GraamFlows.Cli.Services.WaterfallRunner) },
    };

    private static IDeal Build(Type owner, DealDto dto)
    {
        var m = owner.GetMethod("BuildDeal", BindingFlags.NonPublic | BindingFlags.Static);
        m.Should().NotBeNull($"{owner.Name}.BuildDeal must exist for this seam to be testable");
        return (IDeal)m!.Invoke(null, new object?[] { dto, FirstPayDate.AddMonths(-1), null })!;
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void Every_reader_carries_the_size_and_the_zero_target(Type reader)
    {
        var deal = Build(reader, Steps(MixedLosses()).Deal);
        deal.WritedownSize.Should().Be(WritedownSize.OverPool);
        deal.ExcessTurboToPool.Should().BeTrue();
        deal.OcTargetConfig.Should().BeNull("a zero target is not an OC target: nothing is released");
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void Unstated_every_reader_leaves_the_deal_as_it_was(Type reader)
    {
        var deal = Build(reader, BuiltInRule(MixedLosses()).Deal);
        deal.WritedownSize.Should().Be(WritedownSize.PeriodLoss);
        deal.ExcessTurboToPool.Should().BeFalse();
    }

    [Fact]
    public void The_fields_bind_from_the_request_json_under_their_stated_names()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true
        };
        var steps = JsonSerializer.Deserialize<List<WaterfallStepDto>>(
            """[{"type":"EXCESS_TURBO","target":0},{"type":"WRITEDOWN","size":"overPool"},{"type":"WRITEDOWN"}]""",
            options)!;

        steps[0].Target.Should().Be(0);
        steps[1].Size.Should().Be("overPool");
        steps[2].Size.Should().BeNull();
        steps[2].Target.Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------

    private static ActionResult<WaterfallResponse> Execute(WaterfallRequest request) =>
        new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(request);

    private static WaterfallResponse Run(WaterfallRequest request)
    {
        var result = Execute(request).Result;
        (result as BadRequestObjectResult)?.Value.Should().BeNull("the waterfall request should succeed");
        return (WaterfallResponse)((OkObjectResult)result!).Value!;
    }

    private static string Refusal(WaterfallRequest request) =>
        Execute(request).Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()!;

    private static WaterfallStepDto Turbo(WaterfallRequest request) =>
        request.Deal.UnifiedWaterfall!.Steps.First(s => s.Type == "EXCESS_TURBO");

    private static double NotesAt(WaterfallResponse r, int i) =>
        r.TrancheCashflows["A1"][i].Balance + r.TrancheCashflows["B1"][i].Balance;

    /// <summary>The period's interest left after the notes' coupons.</summary>
    private static double Excess(PeriodCashflowDto pool, WaterfallResponse r, int i) =>
        pool.NetInterest - r.TrancheCashflows["A1"][i].Interest - r.TrancheCashflows["B1"][i].Interest;

    /// <summary>
    /// The deal as the built-in rule runs it: the strip sweeps inside INTEREST and the order
    /// names no excess step.
    /// </summary>
    private static WaterfallRequest BuiltInRule(List<PeriodCashflowDto> pool) => Request(pool,
        new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN"
        },
        new List<WaterfallStepDto>
        {
            new() { Type = "INTEREST", Structure = Seq("A1", "B1", "XS") },
            new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A1", "B1") },
            new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A1", "B1") },
            new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A1", "B1") },
            new() { Type = "WRITEDOWN", Structure = Seq("B1", "A1") }
        });

    /// <summary>
    /// The same deal stated in steps: excess pays the notes down to the pool through the
    /// recovery-principal structure, the write-down takes what is left over the pool, and the
    /// strip is paid from the release step.
    /// </summary>
    private static WaterfallRequest Steps(List<PeriodCashflowDto> pool, string? size = "overPool",
        double? target = 0, List<string>? order = null) => Request(pool,
        order ?? new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "EXCESS_TURBO",
            "WRITEDOWN", "EXCESS_RELEASE"
        },
        new List<WaterfallStepDto>
        {
            new() { Type = "INTEREST", Structure = Seq("A1", "B1") },
            new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A1", "B1") },
            new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A1", "B1") },
            new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A1", "B1") },
            new() { Type = "EXCESS_TURBO", Structure = Seq("A1", "B1"), Target = target },
            new() { Type = "WRITEDOWN", Structure = Seq("B1", "A1"), Size = size },
            new() { Type = "EXCESS_RELEASE", Structure = Seq("XS") }
        });

    /// <summary>Documents that pay the strip ahead of any loss cover: no turbo, period-loss size.</summary>
    private static WaterfallRequest StripFirst(List<PeriodCashflowDto> pool, string? size = null) => Request(pool,
        new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN",
            "EXCESS_RELEASE"
        },
        new List<WaterfallStepDto>
        {
            new() { Type = "INTEREST", Structure = Seq("A1", "B1") },
            new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A1", "B1") },
            new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A1", "B1") },
            new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A1", "B1") },
            new() { Type = "WRITEDOWN", Structure = Seq("B1", "A1"), Size = size },
            new() { Type = "EXCESS_RELEASE", Structure = Seq("XS") }
        });

    private static WaterfallRequest Request(List<PeriodCashflowDto> pool, List<string> order,
        List<WaterfallStepDto> steps) => new()
    {
        ProjectionDate = FirstPayDate.AddMonths(-1),
        CollateralCashflows = pool,
        Deal = new DealDto
        {
            DealName = "EXCESS_COVERS_LOSSES_TEST",
            WaterfallType = "ComposableStructure",
            ClosingDate = FirstPayDate.AddMonths(-1),
            Tranches = new List<TrancheDto>
            {
                Note("A1", A1Balance, 0),
                Note("B1", B1Balance, 1),
                new()
                {
                    TrancheName = "XS",
                    OriginalBalance = PoolBalance,
                    TrancheType = "Offered",
                    CashflowType = "IO",
                    CouponType = "ExcessInterest",
                    FixedCoupon = 0.0,
                    ClassReference = "XS",
                    SubordinationOrder = 2,
                    FirstPayDate = FirstPayDate,
                    PayFrequency = 12,
                    PayDay = FirstPayDate.Day
                }
            },
            UnifiedWaterfall = new UnifiedWaterfallDto { ExecutionOrder = order, Steps = steps }
        }
    };

    private static TrancheDto Note(string name, double balance, int subOrder) => new()
    {
        TrancheName = name,
        OriginalBalance = balance,
        TrancheType = "Offered",
        CashflowType = "PI",
        CouponType = "Fixed",
        FixedCoupon = NoteCoupon,
        SubordinationOrder = subOrder,
        FirstPayDate = FirstPayDate,
        PayFrequency = 12,
        PayDay = FirstPayDate.Day
    };

    private static PayableStructureDto Seq(params string[] tranches) => new()
    {
        Type = "SEQ",
        Tranches = tranches.ToList()
    };

    /// <summary>
    /// No loss, then a loss the excess covers, then one it does not, then a covered one again:
    /// both sides of the rule, and the turn from one to the other in each direction.
    /// </summary>
    private static List<PeriodCashflowDto> MixedLosses() => Collateral(
        defaulted: i => i < 3 ? 0 : i < 6 ? 300_000 : i < 9 ? 2_400_000 : 250_000, recoveryRate: 0.5);

    private static List<PeriodCashflowDto> Collateral(Func<int, double> defaulted, double recoveryRate,
        Func<int, double>? recovery = null)
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        for (var i = 0; i < 12; i++)
        {
            var interest = balance * Wac / 100 / 12;
            var def = defaulted(i);
            var recov = recovery?.Invoke(i) ?? def * recoveryRate;
            var scheduled = balance * 0.01;
            var endBalance = balance - scheduled - def;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1,
                CashflowDate = FirstPayDate.AddMonths(i),
                GroupNum = "1",
                BeginBalance = balance,
                Balance = endBalance,
                ScheduledPrincipal = scheduled,
                Interest = interest,
                NetInterest = interest,
                DefaultedPrincipal = def,
                RecoveryPrincipal = recov,
                CollateralLoss = def - recov,
                Wac = Wac
            });
            balance = endBalance;
        }

        return periods;
    }
}
