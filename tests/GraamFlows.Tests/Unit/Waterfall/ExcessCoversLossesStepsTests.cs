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
        // The write-down is the only thing ahead of the late step that closes the notes: the
        // order runs no turbo unless the turbo is the late step.
        var order = new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY"
        };
        order.Remove(late);
        order.AddRange(new[] { "WRITEDOWN", late, "EXCESS_RELEASE" });

        Refusal(Steps(MixedLosses(), target: late == "EXCESS_TURBO" ? 0 : null, order: order))
            .Should().Contain("overPool").And.Contain($"{late} runs after WRITEDOWN");
    }

    // ---- The rule, over every order ------------------------------------------------------------
    //
    // An order can be wrong three ways: a step is missing, a step is named twice, or a step is out
    // of place. So the tests run every order of every subset of the steps, and of every subset
    // with one of its steps named twice, not the orderings of one set.

    private static readonly string[] CollectionsSteps =
    {
        "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY"
    };

    private static readonly string[] StepSet =
        CollectionsSteps.Concat(new[] { "EXCESS_TURBO", "WRITEDOWN", "EXCESS_RELEASE" }).ToArray();

    /// <summary>Every distinct order of the steps; a step listed twice is named twice in each.</summary>
    private static IEnumerable<List<string>> EveryOrder(IReadOnlyList<string> steps)
    {
        if (steps.Count == 0)
        {
            yield return new List<string>();
            yield break;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            if (steps.Take(i).Contains(steps[i]))
                continue;
            var rest = steps.Where((_, j) => j != i).ToList();
            foreach (var tail in EveryOrder(rest))
            {
                tail.Insert(0, steps[i]);
                yield return tail;
            }
        }
    }

    private static IEnumerable<List<string>> EverySubset(IReadOnlyList<string> steps)
    {
        for (var mask = 0; mask < 1 << steps.Count; mask++)
            yield return steps.Where((_, j) => (mask & (1 << j)) != 0).ToList();
    }

    /// <summary>
    /// The rule, stated here without the engine's table, for a deal that states both fields. Each
    /// collections step, the turbo and the write-down are there once, and the release at most
    /// once. Collections pay the notes first, then the turbo, and the write-down and the release
    /// both come after the turbo.
    /// </summary>
    private static bool InPlace(List<string> order)
    {
        int Count(string step) => order.Count(s => s == step);
        var turbo = order.IndexOf("EXCESS_TURBO");
        var release = order.IndexOf("EXCESS_RELEASE");
        return CollectionsSteps.All(s => Count(s) == 1) && Count("EXCESS_TURBO") == 1 && Count("WRITEDOWN") == 1 &&
               Count("EXCESS_RELEASE") <= 1 &&
               CollectionsSteps.All(s => order.IndexOf(s) < turbo) && turbo < order.IndexOf("WRITEDOWN") &&
               (release < 0 || turbo < release);
    }

    /// <summary>Every order of every subset of the seven steps: 13,700 of them.</summary>
    private static IEnumerable<List<string>> EveryOrderOfEverySubset() =>
        EverySubset(StepSet).SelectMany(subset => EveryOrder(subset));

    private static List<List<string>> EveryAcceptedOrder() => EveryOrderOfEverySubset().Where(InPlace).ToList();

    /// <summary>Runs the order, or returns the engine's refusal.</summary>
    private static (WaterfallResponse? Ran, string? Refused) Try(WaterfallRequest request)
    {
        var result = Execute(request).Result;
        return result is OkObjectResult ok
            ? ((WaterfallResponse)ok.Value!, null)
            : (null, ((BadRequestObjectResult)result!).Value!.ToString());
    }

    /// <summary>
    /// Whether a request with this order runs, or the engine's refusal of it.
    /// </summary>
    private static (bool Ran, string? Refused) TryRequest(List<string> order)
    {
        var (ran, refused) = Try(Steps(Collateral(defaulted: _ => 0, recoveryRate: 0.5), order: order));
        return (ran != null, refused);
    }

    /// <summary>
    /// The same for the deal built straight into the engine, over one period. The check is the
    /// engine's either way; without the controller writing up each refusal it costs a third as
    /// much, which is what lets a test run a few hundred thousand orders.
    /// </summary>
    private static (bool Ran, string? Refused) TryInEngine(List<string> order)
    {
        try
        {
            RuleDeal().WithExecutionOrder(order.ToArray()).BuildAndRun(OnePeriod());
            return (true, null);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    /// <summary>
    /// Runs every order, several at a time, and returns what is wrong with each: nothing, for an
    /// order the check passes.
    /// </summary>
    private static List<string> Wrong(IEnumerable<List<string>> orders,
        Func<List<string>, (bool Ran, string? Refused)> run, Func<List<string>, bool, string?, string?> check) =>
        orders.AsParallel()
            .Select(order =>
            {
                var (ran, refused) = run(order);
                var wrong = check(order, ran, refused);
                return wrong == null ? null : $"{string.Join(", ", order)}: {wrong}";
            })
            .Where(wrong => wrong != null).Select(wrong => wrong!).ToList();

    /// <summary>What is wrong with a refusal of this order: nothing, when it names a true fault.</summary>
    private static string? RefusalIsWrong(List<string> order, string refused)
    {
        // An empty order is the default order, which runs the turbo after the write-down.
        if (order.Count == 0)
            return refused.Contains("EXCESS_TURBO runs after WRITEDOWN") &&
                   refused.Contains("states no executionOrder")
                ? null
                : refused;

        var m = System.Text.RegularExpressions.Regex.Match(refused,
            @"([A-Z_]+) runs after ([A-Z_]+) in the execution order");
        if (m.Success)
        {
            var (late, ahead) = (m.Groups[1].Value, m.Groups[2].Value);
            return order.LastIndexOf(late) > order.IndexOf(ahead) &&
                   (CollectionsSteps.Contains(late) || late == "EXCESS_TURBO") &&
                   ahead is "EXCESS_TURBO" or "WRITEDOWN" or "EXCESS_RELEASE" && late != ahead
                ? null
                : refused;
        }

        m = System.Text.RegularExpressions.Regex.Match(refused, @"execution order has no ([A-Z_]+) step");
        if (m.Success)
            return order.Contains(m.Groups[1].Value) ? refused : null;

        m = System.Text.RegularExpressions.Regex.Match(refused,
            @"([A-Z_]+) is named (\d+) times in the execution order");
        if (m.Success)
            return order.Count(s => s == m.Groups[1].Value) == int.Parse(m.Groups[2].Value) &&
                   int.Parse(m.Groups[2].Value) > 1
                ? null
                : refused;

        return refused;
    }

    private static void SameNumbers(WaterfallResponse expected, WaterfallResponse actual, string order,
        params string[] names)
    {
        foreach (var name in names.Length > 0 ? names : new[] { "A1", "B1", "XS" })
        {
            var a = expected.TrancheCashflows[name];
            var b = actual.TrancheCashflows[name];
            b.Should().HaveSameCount(a, order);
            for (var i = 0; i < a.Count; i++)
            {
                var at = $"{name} period {i + 1}, order {order}";
                b[i].Interest.Should().BeApproximately(a[i].Interest, 0.01, at);
                b[i].ScheduledPrincipal.Should().BeApproximately(a[i].ScheduledPrincipal, 0.01, at);
                b[i].UnscheduledPrincipal.Should().BeApproximately(a[i].UnscheduledPrincipal, 0.01, at);
                b[i].Writedown.Should().BeApproximately(a[i].Writedown, 0.01, at);
                b[i].Balance.Should().BeApproximately(a[i].Balance, 0.01, at);
            }
        }
    }

    [Fact]
    public void Over_every_order_of_every_subset_the_engine_accepts_exactly_the_orders_that_are_in_place()
    {
        var orders = EveryOrderOfEverySubset().ToList();
        orders.Should().HaveCount(13_700);
        orders.Count(InPlace).Should().Be(72,
            "4! orders of the collections steps, times the write-down alone or with the release either side of it");

        // Through a request, and again built straight into the engine.
        foreach (var run in new Func<List<string>, (bool, string?)>[] { TryRequest, TryInEngine })
            Wrong(orders, run, (order, ran, refused) =>
                    ran != InPlace(order) ? $"ran {ran}, in place {InPlace(order)}: {refused}" :
                    ran ? null : RefusalIsWrong(order, refused!))
                .Should().BeEmpty();
    }

    [Fact]
    public void Every_order_that_names_a_step_twice_is_refused()
    {
        // Every subset of the seven steps with one of its steps named again.
        var orders = EverySubset(StepSet)
            .SelectMany(subset => subset.Select(again => subset.Append(again).ToList()))
            .SelectMany(steps => EveryOrder(steps)).ToList();
        orders.Should().HaveCount(294_553);

        Wrong(orders, TryInEngine, (order, ran, refused) => ran ? "ran" : RefusalIsWrong(order, refused!))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("INTEREST")]
    [InlineData("PRINCIPAL_SCHEDULED")]
    [InlineData("PRINCIPAL_UNSCHEDULED")]
    [InlineData("PRINCIPAL_RECOVERY")]
    [InlineData("EXCESS_TURBO")]
    [InlineData("WRITEDOWN")]
    [InlineData("EXCESS_RELEASE")]
    public void A_step_named_twice_in_an_order_otherwise_in_place_is_refused_by_its_name(string again)
    {
        // Next to itself: no position is at fault, only the count.
        var order = StepSet.ToList();
        order.Insert(order.IndexOf(again), again);
        Refusal(Steps(MixedLosses(), order: order)).Should().Contain($"{again} is named 2 times in the execution order");
    }

    [Theory]
    [InlineData("INTEREST")]
    [InlineData("PRINCIPAL_SCHEDULED")]
    [InlineData("PRINCIPAL_UNSCHEDULED")]
    [InlineData("PRINCIPAL_RECOVERY")]
    [InlineData("WRITEDOWN")]
    public void An_order_that_leaves_out_a_step_the_notes_need_is_refused_by_its_name(string missing)
    {
        // Left to run, the order without PRINCIPAL_UNSCHEDULED paid the prepayments to no class,
        // paid the strip nothing and wrote the junior class down with no loss in the pool.
        var pool = Collateral(defaulted: _ => 0, recoveryRate: 0.5, prepay: 500_000);
        var order = StepSet.Where(s => s != missing).ToList();
        Refusal(Steps(pool, order: order)).Should().Contain($"The execution order has no {missing} step");
    }

    [Fact]
    public void With_no_losses_every_accepted_order_writes_nothing_down_and_pays_the_strip_the_whole_excess()
    {
        foreach (var order in EveryAcceptedOrder())
        {
            var pool = Collateral(defaulted: _ => 0, recoveryRate: 0.5, prepay: 500_000);
            var text = string.Join(", ", order);
            var r = Run(Steps(pool, order: order));
            for (var i = 0; i < pool.Count - 1; i++)
            {
                var at = $"period {i + 1}, order {text}";
                foreach (var name in new[] { "A1", "B1" })
                {
                    var cf = r.TrancheCashflows[name][i];
                    cf.Writedown.Should().BeApproximately(0, 0.01, at);
                    cf.Interest.Should().BeApproximately(cf.BeginBalance * NoteCoupon / 1200, 0.01,
                        $"{name} is paid its coupon, {at}");
                }

                NotesAt(r, i).Should().BeApproximately(pool[i].Balance, 0.01, $"the notes stand at the pool, {at}");
                var excess = Excess(pool[i], r, i);
                excess.Should().BeGreaterThan(250_000, at);
                // An order with no release step pays the strip nothing: that is all leaving it out does.
                r.TrancheCashflows["XS"][i].Interest.Should().BeApproximately(
                    order.Contains("EXCESS_RELEASE") ? excess : 0, 0.01, $"the strip, {at}");
            }
        }
    }

    [Fact]
    public void With_losses_every_accepted_order_returns_the_canonical_orders_numbers()
    {
        var canonical = Run(Steps(MixedLosses()));
        canonical.TrancheCashflows["B1"].Sum(c => c.Writedown).Should().BeGreaterThan(1_000_000);
        canonical.TrancheCashflows["A1"].Sum(c => c.UnscheduledPrincipal).Should().BeGreaterThan(1_000_000);

        foreach (var order in EveryAcceptedOrder())
        {
            var text = string.Join(", ", order);
            var r = Run(Steps(MixedLosses(), order: order));
            if (order.Contains("EXCESS_RELEASE"))
            {
                SameNumbers(canonical, r, text);
                continue;
            }

            // No release step: the notes' numbers do not move and the strip is paid nothing.
            SameNumbers(canonical, r, text, "A1", "B1");
            r.TrancheCashflows["XS"].Should().OnlyContain(c => Math.Abs(c.Interest) < 0.01, text);
        }
    }

    [Fact]
    public void In_every_accepted_order_every_step_pays_out_what_it_takes_while_the_first_class_retires()
    {
        foreach (var order in EveryAcceptedOrder())
        {
            var text = string.Join(", ", order);
            var request = RetiringSenior(order);
            request.ReportCashLedger = true;
            var r = Run(request);

            r.TrancheCashflows["A1"][3].Balance.Should().Be(0, $"the fixture: the senior class retires, {text}");
            Balances(r.CashLedger!, text, leftInterest: !order.Contains("EXCESS_RELEASE"));
        }
    }

    /// <summary>
    /// One more step in the order than the seven: a second collections step, a step that never
    /// pays the notes principal, or the release under its other name. Every order of the eight.
    /// </summary>
    [Theory]
    [InlineData("SUPPLEMENTAL_REDUCTION", 240)]
    [InlineData("EXPENSE", 384)]
    [InlineData("RESERVE_DEPOSIT", 384)]
    [InlineData("CAP_CARRYOVER", 384)]
    [InlineData("EXCESS", 0)]
    public void With_an_eighth_step_the_engine_accepts_the_orders_in_place_and_they_return_the_canonical_numbers(
        string eighth, int inPlace)
    {
        // The seven in place; a collections step ahead of the turbo; the release named once.
        bool Accepted(List<string> order) =>
            InPlace(order.Where(s => s != eighth).ToList()) && eighth switch
            {
                "SUPPLEMENTAL_REDUCTION" => order.IndexOf(eighth) < order.IndexOf("EXCESS_TURBO"),
                "EXCESS" => false,
                _ => true
            };

        var orders = EveryOrder(StepSet.Append(eighth).ToList()).ToList();
        orders.Should().HaveCount(40_320);
        orders.Count(Accepted).Should().Be(inPlace);

        Wrong(orders, TryInEngine, (order, ran, refused) =>
                ran != Accepted(order) ? $"ran {ran}, in place {Accepted(order)}: {refused}" : null)
            .Should().BeEmpty();

        // The fixture has no fee, reserve, carried-over interest or supplemental amount, so the
        // eighth step moves no cash: this shows the order is accepted and the seven still run.
        var canonical = Run(Steps(MixedLosses()));
        foreach (var order in orders.Where(Accepted))
            SameNumbers(canonical, Run(Steps(MixedLosses(), order: order)), string.Join(", ", order));
    }

    [Theory]
    [InlineData("EXPENSE")]
    [InlineData("RESERVE_DEPOSIT")]
    [InlineData("CAP_CARRYOVER")]
    public void A_step_that_never_pays_the_notes_principal_may_be_named_twice(string free)
    {
        // Accepted, on a deal where the step moves no cash. The count is for the steps that pay
        // the notes, write them down or pay out the interest left.
        var order = StepSet.Prepend(free).Append(free).ToList();
        SameNumbers(Run(Steps(MixedLosses())), Run(Steps(MixedLosses(), order: order)), string.Join(", ", order));
    }

    [Fact]
    public void EXCESS_is_the_release_step_under_its_other_name()
    {
        var pool = MixedLosses();
        List<string> Order(params string[] tail) => CollectionsSteps.Concat(tail).ToList();

        SameNumbers(Run(Steps(pool)), Run(Steps(pool, order: Order("EXCESS_TURBO", "WRITEDOWN", "EXCESS"))),
            "EXCESS for EXCESS_RELEASE");
        Refusal(Steps(pool, order: Order("EXCESS", "EXCESS_TURBO", "WRITEDOWN")))
            .Should().Contain("EXCESS_TURBO runs after EXCESS in the execution order");
        Refusal(Steps(pool, order: Order("EXCESS_TURBO", "WRITEDOWN", "EXCESS", "EXCESS_RELEASE")))
            .Should().Contain("EXCESS_RELEASE is named 2 times in the execution order (EXCESS is the same step)");
    }

    [Fact]
    public void A_zero_target_alone_puts_the_collections_steps_ahead_of_the_turbo()
    {
        // No overPool: the write-down takes the period's loss and the turbo runs after it.
        var order = new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "WRITEDOWN", "EXCESS_TURBO",
            "PRINCIPAL_RECOVERY", "EXCESS_RELEASE"
        };
        Refusal(Steps(MixedLosses(), size: null, order: order))
            .Should().Contain("PRINCIPAL_RECOVERY runs after EXCESS_TURBO in the execution order");
    }

    [Fact]
    public void A_zero_target_alone_needs_the_collections_steps_and_no_write_down()
    {
        // No overPool: excess pays the notes down to the pool and nothing is ever written down.
        var order = CollectionsSteps.Concat(new[] { "EXCESS_TURBO", "EXCESS_RELEASE" }).ToList();
        var r = Run(Steps(MixedLosses(), size: null, order: order));
        r.TrancheCashflows["B1"].Should().OnlyContain(c => Math.Abs(c.Writedown) < 0.01);

        order.Remove("PRINCIPAL_SCHEDULED");
        Refusal(Steps(MixedLosses(), size: null, order: order))
            .Should().Contain("The execution order has no PRINCIPAL_SCHEDULED step");
    }

    [Fact]
    public void OverPool_alone_puts_the_collections_steps_ahead_of_any_turbo()
    {
        var order = new List<string>
        {
            "INTEREST", "EXCESS_TURBO", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY",
            "WRITEDOWN", "EXCESS_RELEASE"
        };
        Refusal(Steps(MixedLosses(), target: null, order: order))
            .Should().Contain("PRINCIPAL_SCHEDULED runs after EXCESS_TURBO in the execution order");
    }

    [Fact]
    public void OverPool_alone_puts_the_coupons_ahead_of_the_release()
    {
        // No turbo in the order: the release is the only step that closes the notes to INTEREST.
        var order = new List<string>
        {
            "EXCESS_RELEASE", "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY",
            "WRITEDOWN"
        };
        Refusal(Steps(MixedLosses(), target: null, order: order))
            .Should().Contain("INTEREST runs after EXCESS_RELEASE in the execution order");
    }

    [Fact]
    public void OverPool_alone_needs_the_collections_steps_too()
    {
        var order = new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN", "EXCESS_RELEASE"
        };
        Refusal(Steps(MixedLosses(), target: null, order: order))
            .Should().Contain("The execution order has no PRINCIPAL_UNSCHEDULED step");
    }

    [Fact]
    public void A_deal_that_states_neither_field_is_not_checked()
    {
        // Out of place, a step short and a step named twice, on a deal with no size and no
        // target: it runs, as on main.
        var steps = Steps(MixedLosses(), size: null, target: null,
            order: new List<string>
            {
                "EXCESS_TURBO", "INTEREST", "WRITEDOWN", "PRINCIPAL_SCHEDULED", "PRINCIPAL_SCHEDULED",
                "PRINCIPAL_RECOVERY", "EXCESS_RELEASE"
            });
        Try(steps).Refused.Should().BeNull();
    }

    // ---- The deal shape the fields are supported on --------------------------------------------
    //
    // The order rule lists what is wrong with an order. A deal is more than its order, so the
    // fields are accepted only on the shape they are tested on. Each test below runs the same
    // deal with neither field stated, to show the refusal is the fields' and not the deal's.

    public static IEnumerable<object?[]> FieldsStated => new List<object?[]>
    {
        new object?[] { "overPool", 0.0 }, new object?[] { "overPool", null }, new object?[] { null, 0.0 }
    };

    /// <summary>The order a deal states with these fields: canonical, or without the turbo.</summary>
    private static List<string> OrderFor(string? size, double? target) =>
        size == "overPool" && target == 0 ? StepSet.ToList() :
        size == "overPool" ? StepSet.Where(s => s != "EXCESS_TURBO").ToList() :
        CollectionsSteps.Concat(new[] { "WRITEDOWN", "EXCESS_TURBO", "EXCESS_RELEASE" }).ToList();

    private static void RefusedWithTheFieldsAndRunsWithout(Func<string?, double?, WaterfallRequest> deal,
        string? size, double? target, string refusal)
    {
        Refusal(deal(size, target)).Should().Contain(refusal);
        Try(deal(null, null)).Refused.Should().BeNull("the same deal with neither field stated");
    }

    [Theory]
    [MemberData(nameof(FieldsStated))]
    public void An_interleaved_order_is_not_supported(string? size, double? target)
    {
        foreach (var interleaved in new[] { "interestFirst", "principalFirst" })
            RefusedWithTheFieldsAndRunsWithout((sz, tg) =>
            {
                var request = Steps(MixedLosses(), sz, tg, OrderFor(size, target));
                request.Deal.UnifiedWaterfall!.WaterfallOrder = interleaved;
                return request;
            }, size, target, "not supported on a deal with an interleaved waterfallOrder");
    }

    [Theory]
    [MemberData(nameof(FieldsStated))]
    public void A_deferrable_note_is_not_supported(string? size, double? target)
    {
        // Left to run, the interest the junior class deferred was written down the month it was
        // added to its balance.
        RefusedWithTheFieldsAndRunsWithout((sz, tg) =>
        {
            var request = Steps(MixedLosses(), sz, tg, OrderFor(size, target));
            request.Deal.UnifiedWaterfall!.DeferrableNotes = new List<DeferrableNoteDto>
            {
                new() { Class = "B1", PriorityClasses = new List<string> { "A1" } }
            };
            return request;
        }, size, target, "not supported on a deal with deferrable notes (B1)");
    }

    [Theory]
    [MemberData(nameof(FieldsStated))]
    public void Forbearance_in_the_collateral_is_not_supported(string? size, double? target)
    {
        RefusedWithTheFieldsAndRunsWithout((sz, tg) =>
        {
            var pool = MixedLosses();
            pool[2].ForbearanceLiquidated = 100_000;
            return Steps(pool, sz, tg, OrderFor(size, target));
        }, size, target, "not supported on a deal with a forbearance balance (first on 04/25/2026)");
    }

    [Fact]
    public void In_the_engine_a_standing_forbearance_balance_is_not_supported()
    {
        // A waterfall request carries the period's forbearance flows and not the accumulated
        // balance, so this is the deal built straight into the engine. The pool's balance leaves
        // forbearance out; notes sized to include it would be written down by it.
        TestDealBuilder Deal(bool statesTheFields = true) => RuleDeal(statesTheFields);
        CollateralCashflows WithForbearance()
        {
            var collateral = OnePeriod();
            collateral.PeriodCashflows.Single().AccumForbearance = 4_000_000;
            return collateral;
        }

        var act = () => Deal().BuildAndRun(WithForbearance());
        act.Should().Throw<Exception>().WithMessage(
            "*not supported on a deal with a forbearance balance (first on*");

        var neither = () => Deal(statesTheFields: false).BuildAndRun(WithForbearance());
        neither.Should().NotThrow("the same deal with neither field stated");
    }

    [Theory]
    [MemberData(nameof(FieldsStated))]
    public void More_than_one_collateral_group_is_not_supported(string? size, double? target)
    {
        // Left to run, classes two groups shared were measured against one group's pool and
        // written down by the other's balance in the first period.
        RefusedWithTheFieldsAndRunsWithout((sz, tg) =>
        {
            var pool = MixedLosses();
            var second = MixedLosses();
            foreach (var period in second)
                period.GroupNum = "2";
            var request = Steps(pool.Concat(second).OrderBy(p => p.CashflowDate).ThenBy(p => p.GroupNum).ToList(),
                sz, tg, OrderFor(size, target));
            foreach (var tranche in request.Deal.Tranches)
                tranche.GroupNum = "0";
            request.Deal.DealStructures = new[] { "A1", "B1", "XS" }.Select((name, i) => new DealStructureDto
                { ClassGroupName = name, SubordinationOrder = i, PayFrom = "Sequential", GroupNum = "0" }).ToList();
            const string notes = "SEQ(SINGLE('A1'), SINGLE('B1'))";
            request.Deal.PayRules = new[]
            {
                $"SET_INTEREST_STRUCT({notes})", $"SET_SCHED_STRUCT({notes})", $"SET_PREPAY_STRUCT({notes})",
                $"SET_RECOV_STRUCT({notes})",
                "SET_WRITEDOWN_STRUCT(SEQ(SINGLE('B1'), SINGLE('A1')))", "SET_RELEASE_STRUCT(SEQ(SINGLE('XS')))"
            }.Select((formula, i) => new PayRuleDto
                { RuleName = $"G2_{i}", ClassGroupName = "GROUP_2", Formula = formula, Priority = 100 + i }).ToList();
            return request;
        }, size, target, "not supported on a deal with more than one collateral group (groups 1, 2)");
    }

    [Theory]
    [MemberData(nameof(FieldsStated))]
    public void A_dropped_first_period_is_not_supported(string? size, double? target)
    {
        // The collateral starts two months ahead of the first payment date. Dropped, those
        // months' principal left the pool and never reached the notes, and was written down.
        WaterfallRequest Stub(string? sz, double? tg, string policy)
        {
            var pool = MixedLosses();
            for (var i = 0; i < pool.Count; i++)
                pool[i].CashflowDate = FirstPayDate.AddMonths(i - 2);
            var request = Steps(pool, sz, tg, OrderFor(size, target));
            request.ProjectionDate = FirstPayDate.AddMonths(-3);
            request.Deal.ClosingDate = FirstPayDate.AddMonths(-3);
            request.Deal.FirstPeriodCollateralPolicy = policy;
            return request;
        }

        RefusedWithTheFieldsAndRunsWithout((sz, tg) => Stub(sz, tg, "Drop"), size, target,
            "not supported on a deal with firstPeriodCollateralPolicy Drop, which leaves out the collateral period of 12/25/2025");
        // The other two policies keep the principal, and run.
        foreach (var policy in new[] { "Fold", "Align" })
            Try(Stub(size, target, policy)).Refused.Should().BeNull(policy);
    }

    [Theory]
    [InlineData("unscheduled", "overPool", 0.0)]
    [InlineData("unscheduled", "overPool", null)]
    [InlineData("unscheduled", null, 0.0)]
    [InlineData("recovery", "overPool", 0.0)]
    [InlineData("recovery", "overPool", null)]
    [InlineData("recovery", null, 0.0)]
    public void A_principal_step_named_in_the_order_with_no_structure_behind_it_is_not_supported(string source,
        string? size, double? target)
    {
        // The name is in the order and the step is not in the deal. Left to run with prepayments
        // and no defaults, the prepayments reached no class, the strip was paid nothing and the
        // junior class was written down.
        RefusedWithTheFieldsAndRunsWithout((sz, tg) =>
        {
            var request = Steps(Collateral(defaulted: _ => 0, recoveryRate: 0.5, prepay: 500_000), sz, tg,
                OrderFor(size, target));
            request.Deal.UnifiedWaterfall!.Steps.RemoveAll(s => s.Type == "PRINCIPAL" && s.Source == source);
            return request;
        }, size, target, $"not supported on a deal with no structure for {source} principal");
    }

    [Fact]
    public void A_recovery_structure_switched_by_a_rule_is_followed_by_the_turbo_as_by_the_built_in_rule()
    {
        // The turbo pays through the recovery structure as the period's rules leave it. Once the
        // senior class is below 60,000,000 a rule switches recovery principal to pro rata, and
        // the built-in rule and the steps both follow the switch. A structure fixed on the step
        // could not.
        var pool = MixedLosses();
        var switchToProRata = new List<PayRuleDto>
        {
            new()
            {
                RuleName = "SwitchRecovery", ClassGroupName = "GROUP_1", Priority = 1000,
                Formula = "if (BALANCE('A1') < 60000000) SET_RECOV_STRUCT(PRORATA(SINGLE('A1'), SINGLE('B1')))"
            }
        };
        var ruleRequest = BuiltInRule(pool);
        ruleRequest.Deal.PayRules = switchToProRata;
        var stepsRequest = Steps(pool);
        stepsRequest.Deal.PayRules = switchToProRata;
        var rule = Run(ruleRequest);
        var steps = Run(stepsRequest);

        // Anti-vacuity: the switch happens, and after it the junior class is paid principal
        // that it would not be paid in sequence.
        var plain = Run(Steps(pool));
        steps.TrancheCashflows["B1"].Sum(c => c.UnscheduledPrincipal).Should().BeGreaterThan(
            plain.TrancheCashflows["B1"].Sum(c => c.UnscheduledPrincipal) + 100_000);

        SameNumbers(rule, steps, "a recovery structure switched by a rule", "A1", "B1");
        // The strip is paid the same; only its write-down column differs, by design (above).
        for (var i = 0; i < rule.TrancheCashflows["XS"].Count; i++)
            steps.TrancheCashflows["XS"][i].Interest.Should().BeApproximately(
                rule.TrancheCashflows["XS"][i].Interest, 0.01, $"XS period {i + 1}");
    }

    // ---- What the two measuring steps check as they run ------------------------------------------

    [Theory]
    [InlineData("overPool", 0.0, "EXCESS_TURBO with a target of zero measures the notes against the pool")]
    [InlineData("overPool", null, "WRITEDOWN sized overPool measures the notes against the pool")]
    [InlineData(null, 0.0, "EXCESS_TURBO with a target of zero measures the notes against the pool")]
    public void Principal_left_unpaid_while_the_notes_have_a_balance_stops_the_run(string? size, double? target,
        string refusal)
    {
        // Every step is named and has a structure, but the unscheduled one reaches only the
        // senior class. Once that retires the prepayments reach no class: the pool falls by them
        // and the notes do not. No rule about the order sees it; the step that measures does.
        WaterfallRequest Deal(string? sz, double? tg)
        {
            var request = Steps(Collateral(defaulted: _ => 0, recoveryRate: 0.5, prepay: 500_000), sz, tg,
                OrderFor(size, target));
            request.Deal.Tranches.Single(t => t.TrancheName == "A1").OriginalBalance = 4_000_000;
            request.Deal.Tranches.Single(t => t.TrancheName == "B1").OriginalBalance = PoolBalance - 4_000_000;
            request.Deal.UnifiedWaterfall!.Steps.Single(s => s.Type == "PRINCIPAL" && s.Source == "unscheduled")
                .Default = Single("A1");
            return request;
        }

        Refusal(Deal(size, target)).Should().Contain(refusal)
            .And.Contain("of the period's principal collected and not paid");
        Try(Deal(null, null)).Refused.Should().BeNull("the same deal with neither field stated");
    }

    [Theory]
    [MemberData(nameof(FieldsStated))]
    public void Principal_left_over_once_the_notes_have_retired_does_not_stop_the_run(string? size, double? target)
    {
        // Notes of 5,000,000 on a pool of 100,000,000 retire inside the run. The principal
        // collected after that has no note to pay, and that is not a loss.
        var request = Steps(MixedLosses(), size, target, OrderFor(size, target));
        request.Deal.Tranches.Single(t => t.TrancheName == "A1").OriginalBalance = 3_000_000;
        request.Deal.Tranches.Single(t => t.TrancheName == "B1").OriginalBalance = 2_000_000;
        request.ReportCashLedger = true;
        var r = Run(request);

        NotesAt(r, 5).Should().Be(0, "the fixture: the notes have retired");
        r.CashLedger!.LeftPrincipal.Should().BeGreaterThan(1_000_000, "the fixture: principal is left over");
        // Sized overPool, notes that stand under the pool are never written down. (Sized by the
        // period's loss they take each loss, with or without a target, as on main.)
        if (size == "overPool")
            foreach (var name in new[] { "A1", "B1" })
                r.TrancheCashflows[name].Sum(c => c.Writedown).Should().BeApproximately(0, 0.01, name);
    }

    [Fact]
    public void A_recovery_structure_that_nets_a_payment_between_classes_stops_the_run()
    {
        // A forced paydown with a bare class as its support pays its class in full and "pays"
        // the support the difference, a negative amount. Switched in as the recovery structure,
        // with no recoveries the recovery step pays nothing and the turbo is the first to pay
        // through it: the total is what it was given, so only the class-by-class look sees it.
        var request = Steps(Collateral(defaulted: _ => 600_000, recoveryRate: 0, prepay: 200_000));
        request.Deal.Tranches.Single(t => t.TrancheName == "A1").OriginalBalance = 2_500_000;
        request.Deal.Tranches.Single(t => t.TrancheName == "B1").OriginalBalance = 20_000_000;
        request.Deal.Tranches.Add(Note("C1", PoolBalance - 22_500_000, 2));
        foreach (var step in request.Deal.UnifiedWaterfall!.Steps.Where(s => s.Type != "EXCESS_RELEASE"))
        {
            if (step.Structure != null) step.Structure = step.Type == "WRITEDOWN" ? Seq("C1", "B1", "A1") : Seq("A1", "B1", "C1");
            if (step.Default != null) step.Default = Seq("A1", "B1", "C1");
        }

        request.Deal.PayRules = new List<PayRuleDto>
        {
            new()
            {
                RuleName = "SwitchTurbo", ClassGroupName = "GROUP_1", Priority = 1000,
                Formula = "if (BALANCE('A1') < 1) SET_RECOV_STRUCT(FORCE_PAYDOWN(SINGLE('B1'), SINGLE('C1')))"
            }
        };

        Refusal(request).Should().Contain("EXCESS_TURBO with a target of zero recorded a payment of -")
            .And.Contain("to C1 on");
    }

    [Fact]
    public void A_turbo_payment_to_a_class_that_is_not_a_note_stops_the_run()
    {
        // A pay rule moves recovery principal to a funded residual class once the senior class
        // retires, inside the period, after the period's structures were checked.
        var request = Steps(Collateral(defaulted: _ => 600_000, recoveryRate: 0.5, prepay: 200_000));
        request.Deal.Tranches.Single(t => t.TrancheName == "A1").OriginalBalance = 2_500_000;
        request.Deal.Tranches.Single(t => t.TrancheName == "B1").OriginalBalance = PoolBalance - 2_500_000;
        request.Deal.Tranches.Add(new TrancheDto
        {
            TrancheName = "R", OriginalBalance = 5_000_000, TrancheType = "Offered", CashflowType = "PI",
            CouponType = "Residual", FixedCoupon = 0.0, SubordinationOrder = 9, FirstPayDate = FirstPayDate,
            PayFrequency = 12, PayDay = FirstPayDate.Day
        });
        request.Deal.PayRules = new List<PayRuleDto>
        {
            new()
            {
                RuleName = "SwitchTurbo", ClassGroupName = "GROUP_1", Priority = 1000,
                Formula = "if (BALANCE('A1') < 1) SET_RECOV_STRUCT(SINGLE('R'))"
            }
        };

        Refusal(request).Should().Contain("EXCESS_TURBO with a target of zero paid")
            .And.Contain("to R on").And.Contain("which the engine does not count as a note");
    }

    [Fact]
    public void A_recovery_structure_that_pays_more_than_it_is_given_stops_the_run()
    {
        // A switch to a forced paydown with a sequence as its support: it pays its class in full
        // whatever it is given.
        var request = Steps(Collateral(defaulted: _ => 600_000, recoveryRate: 0.5, prepay: 200_000));
        request.Deal.Tranches.Single(t => t.TrancheName == "A1").OriginalBalance = 2_500_000;
        request.Deal.Tranches.Single(t => t.TrancheName == "B1").OriginalBalance = 20_000_000;
        request.Deal.Tranches.Add(Note("C1", PoolBalance - 22_500_000, 2));
        foreach (var step in request.Deal.UnifiedWaterfall!.Steps.Where(s => s.Type != "EXCESS_RELEASE"))
        {
            if (step.Structure != null) step.Structure = step.Type == "WRITEDOWN" ? Seq("C1", "B1", "A1") : Seq("A1", "B1", "C1");
            if (step.Default != null) step.Default = Seq("A1", "B1", "C1");
        }

        request.Deal.PayRules = new List<PayRuleDto>
        {
            new()
            {
                RuleName = "SwitchTurbo", ClassGroupName = "GROUP_1", Priority = 1000,
                Formula = "if (BALANCE('A1') < 1) SET_RECOV_STRUCT(FORCE_PAYDOWN(SINGLE('C1'), SEQ(SINGLE('B1'))))"
            }
        };

        Refusal(request).Should().Contain("more principal than the period collected");
    }

    [Fact]
    public void A_tranche_filed_under_another_class_stops_the_run_at_the_turbo()
    {
        // B1's tranche names A1 as its class, so the class's balance and its recorded payments
        // no longer move together.
        var request = Steps(Collateral(defaulted: _ => 600_000, recoveryRate: 0.5, prepay: 200_000));
        request.Deal.Tranches.Single(t => t.TrancheName == "B1").ClassReference = "A1";

        // The class's payment is recorded twice from the first principal step on, so the principal
        // steps pay out more than the period collected and the turbo, measuring, stops the run.
        Refusal(request).Should().Contain("EXCESS_TURBO with a target of zero measures the notes against the pool")
            .And.Contain("more principal than the period collected");
    }

    [Fact]
    public void The_size_is_read_whatever_its_case()
    {
        SameNumbers(Run(Steps(MixedLosses())), Run(Steps(MixedLosses(), size: "OVERPOOL")), "OVERPOOL");
    }

    // ---- The table and the switch --------------------------------------------------------------

    [Fact]
    public void The_step_switch_answers_to_exactly_the_names_the_table_lists()
    {
        var type = typeof(GraamFlows.Waterfall.Structures.ComposableStructure);
        var table = (System.Collections.IDictionary)type
            .GetField("StepEffects", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var listed = table.Keys.Cast<string>().OrderBy(n => n, StringComparer.Ordinal).ToList();

        var source = System.IO.File.ReadAllText(EngineSource());
        var from = source.IndexOf("switch (step.ToUpperInvariant())", StringComparison.Ordinal);
        var to = source.IndexOf("has no case for it", from, StringComparison.Ordinal);
        from.Should().BeGreaterThan(0);
        to.Should().BeGreaterThan(from);
        var cases = System.Text.RegularExpressions.Regex
            .Matches(source[from..to], "case \"([A-Z_]+)\":").Select(m => m.Groups[1].Value)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        cases.Should().OnlyHaveUniqueItems();
        cases.Should().Equal(listed, "a step the switch runs states its effect in the table, and the table " +
                                     "lists no step the switch does not run");
        listed.Should().HaveCount(14, "WRITEUP is the fourteenth (graam-harmony#6199 D7)");
    }

    [Fact]
    public void A_name_the_table_does_not_list_is_refused_as_a_name_no_step_answers_to()
    {
        var order = StepSet.ToList();
        order.Insert(2, "PRINCIPAL");
        Refusal(Steps(MixedLosses(), order: order)).Should().Contain("names 'PRINCIPAL', which no step answers to");
    }

    private static string EngineSource([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!,
            "..", "..", "..", "..", "src", "GraamFlows.Core", "Waterfall", "Structures", "ComposableStructure.cs"));

    // ---- The turbo's structure -----------------------------------------------------------------

    private static PayableStructureDto Single(string tranche) => new() { Type = "SINGLE", Tranche = tranche };

    private static PayableStructureDto ProRata(params string[] tranches) =>
        new() { Type = "PRORATA", Tranches = tranches.ToList() };

    private static readonly Dictionary<string, Func<PayableStructureDto>> NoteStructures = new()
    {
        ["SINGLE A1"] = () => Single("A1"),
        ["SINGLE B1"] = () => Single("B1"),
        ["SEQ A1"] = () => Seq("A1"),
        ["SEQ B1"] = () => Seq("B1"),
        ["SEQ A1 B1"] = () => Seq("A1", "B1"),
        ["SEQ B1 A1"] = () => Seq("B1", "A1"),
        ["PRORATA A1"] = () => ProRata("A1"),
        ["PRORATA B1"] = () => ProRata("B1"),
        ["PRORATA A1 B1"] = () => ProRata("A1", "B1"),
        ["SEQ of SINGLE A1 and PRORATA B1"] = () => new PayableStructureDto
            { Type = "SEQ", Children = new List<PayableStructureDto> { Single("A1"), ProRata("B1") } },
        ["PRORATA of SEQ A1 and SEQ B1"] = () => new PayableStructureDto
            { Type = "PRORATA", Children = new List<PayableStructureDto> { Seq("A1"), Seq("B1") } }
    };

    /// <summary>
    /// The structures that reach both notes. As the recovery structure, one that reaches only one
    /// note leaves recovery principal unpaid once that note retires, which stops the run (below).
    /// </summary>
    public static IEnumerable<object[]> NoteStructuresAndShapes =>
        from structure in NoteStructures.Keys
        where structure.Contains("A1") && structure.Contains("B1")
        from seniorBalance in new[] { 4_000_000.0, A1Balance, PoolBalance - 100_000 }
        select new object[] { structure, seniorBalance };

    /// <summary>
    /// The deal with a loss every period the excess covers, the two notes resized. With a senior
    /// class of 4,000,000 it retires in the third period.
    /// </summary>
    private static WaterfallRequest Resized(double seniorBalance, List<string>? order = null)
    {
        var request = Steps(Collateral(defaulted: _ => 600_000, recoveryRate: 0.5), order: order);
        request.Deal.Tranches.Single(t => t.TrancheName == "A1").OriginalBalance = seniorBalance;
        request.Deal.Tranches.Single(t => t.TrancheName == "B1").OriginalBalance = PoolBalance - seniorBalance;
        return request;
    }

    private static WaterfallRequest RetiringSenior(List<string>? order = null) => Resized(4_000_000, order);

    /// <summary>Every step paid out what it took, and what was paid and left is what was collected.</summary>
    private static void Balances(CashLedgerDto ledger, string what, bool leftInterest = false)
    {
        foreach (var step in ledger.Steps)
        {
            step.TakenNotPaid.Should().BeApproximately(0, 0.01, $"{step.Step}, {what}");
            step.PaidNotTaken.Should().BeApproximately(0, 0.01, $"{step.Step}, {what}");
            step.PeriodsOff.Should().Be(0, $"{step.Step}, {what}");
        }

        ledger.LeftPrincipal.Should().BeApproximately(0, 0.01, what);
        if (!leftInterest)
            ledger.LeftInterest.Should().BeApproximately(0, 0.01, what);
        (ledger.Steps.Sum(s => s.Paid) + ledger.LeftInterest + ledger.LeftPrincipal).Should()
            .BeApproximately(ledger.Collected, 0.01, what);
    }

    [Theory]
    [MemberData(nameof(NoteStructuresAndShapes))]
    public void Through_every_single_sequential_and_pro_rata_structure_every_step_pays_out_what_it_takes(
        string structure,
        double seniorBalance)
    {
        // Three shapes: the senior class retires inside the run, the two classes as elsewhere in
        // this file, and a junior class smaller than one period's loss.
        var request = Resized(seniorBalance);
        Recovery(request).Default = NoteStructures[structure]();
        request.ReportCashLedger = true;
        var r = Run(request);

        Balances(r.CashLedger!, $"{structure}, senior {seniorBalance:N0}");
        foreach (var name in new[] { "A1", "B1" })
            r.TrancheCashflows[name].Should().OnlyContain(c => c.Balance >= 0 && c.BeginBalance >= c.Balance - 0.01);
    }

    [Fact]
    public void A_recovery_structure_naming_the_strip_runs_as_if_it_did_not_name_it()
    {
        // The strip holds no principal, so it is never paid any: naming it is not refused.
        var named = Steps(MixedLosses());
        Recovery(named).Default = Seq("A1", "XS", "B1");
        SameNumbers(Run(Steps(MixedLosses())), Run(named), "the strip named in the recovery structure");
    }

    [Fact]
    public void A_turbo_payment_through_a_recovery_structure_reaching_a_funded_residual_class_stops_the_run()
    {
        // The recovery structure pays the residual class, so the notes stand above the pool by
        // the recoveries and the turbo pays the residual class too.
        var request = Steps(Collateral(defaulted: _ => 600_000, recoveryRate: 0.5));
        request.Deal.Tranches.Add(new TrancheDto
        {
            TrancheName = "R", OriginalBalance = 5_000_000, TrancheType = "Offered", CashflowType = "PI",
            CouponType = "Residual", FixedCoupon = 0.0, SubordinationOrder = 9, FirstPayDate = FirstPayDate,
            PayFrequency = 12, PayDay = FirstPayDate.Day
        });
        Recovery(request).Default = Seq("R");
        Refusal(request).Should().Contain("EXCESS_TURBO with a target of zero paid")
            .And.Contain("to R on").And.Contain("which the engine does not count as a note");
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
    public void A_zero_target_with_a_structure_of_its_own_is_refused()
    {
        var request = Steps(MixedLosses());
        Turbo(request).Structure = Seq("A1", "B1");
        Refusal(request).Should().Contain("states target 0 and a structure of its own");
    }

    // The same two refusals inside the engine, for a deal that reaches it without the mapper
    // (pay rules rather than steps).

    private static TestDealBuilder RuleDeal(bool statesTheFields = true)
    {
        var deal = new TestDealBuilder()
            .WithTranche("A", 70_000_000, NoteCoupon, subOrder: 0)
            .WithTranche("B", 30_000_000, NoteCoupon, subOrder: 1)
            .WithSequentialWaterfall("A", "B")
            .WithExecutionOrder("INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY",
                "EXCESS_TURBO", "WRITEDOWN");
        return statesTheFields ? deal.WithWritedownSize(WritedownSize.OverPool).WithExcessTurboToPool() : deal;
    }

    private static CollateralCashflows OnePeriod() => new TestCollateralBuilder().WithGroupNum("1")
        .WithPeriod(date: TestConstants.DefaultFirstPayDate, beginBalance: 100_000_000,
            scheduledPrincipal: 1_000_000, unscheduledPrincipal: 0, interest: 750_000,
            defaultedPrincipal: 200_000, recoveryPrincipal: 100_000)
        .Build();

    [Fact]
    public void In_the_engine_a_zero_target_with_a_turbo_structure_of_its_own_is_refused()
    {
        var act = () => RuleDeal()
            .WithPayRule("TurboStruct", "SET_TURBO_STRUCT(SEQ(SINGLE('A'), SINGLE('B')))")
            .BuildAndRun(OnePeriod());
        act.Should().Throw<Exception>().WithMessage("*target of zero and a structure of its own*");
    }

    [Fact]
    public void In_the_engine_a_zero_target_beside_an_OC_target_is_refused()
    {
        var act = () => RuleDeal()
            .WithOcTarget(0.02, 0)
            .BuildAndRun(OnePeriod());
        act.Should().Throw<Exception>().WithMessage("*both a target of zero and an OC target*");
    }

    [Fact]
    public void In_the_engine_the_rule_built_deal_runs_with_no_turbo_structure()
    {
        // Anti-vacuity for the two refusals above: the same deal runs and the excess covers the
        // 100k loss through the recovery structure.
        var cf = RuleDeal().BuildAndRun(OnePeriod()).Cashflows;
        var b = cf.TrancheCashflows.First(t => t.Key.TrancheName == "B").Value.Cashflows.Values.First();
        b.Writedown.Should().BeApproximately(0, 0.01);
    }

    [Theory]
    [InlineData("PROFORMA(SINGLE('A'), 0.5, SINGLE('B'), 0.5)")]
    [InlineData("SHIFTI(0.5, SINGLE('A'), SINGLE('B'))")]
    [InlineData("CSCAP(0.5, SINGLE('A'), SINGLE('B'))")]
    [InlineData("FIXED(100000.0, SINGLE('A'), SINGLE('B'))")]
    public void In_the_engine_the_turbo_pays_through_a_recovery_structure_of_another_form(string structure)
    {
        // The step pays through whatever the recovery structure is, as the built-in rule does,
        // and spends what it reports it paid: the excess covers the 100k loss.
        var cf = RuleDeal().WithPayRule("RecovStructOverride", $"SET_RECOV_STRUCT({structure})")
            .BuildAndRun(OnePeriod()).Cashflows;
        var notes = cf.TrancheCashflows.Where(t => t.Key.TrancheName is "A" or "B")
            .Sum(t => t.Value.Cashflows.Values.First().Balance);
        notes.Should().BeApproximately(100_000_000 - 1_000_000 - 200_000, 0.01, structure);
        cf.TrancheCashflows.Sum(t => t.Value.Cashflows.Values.First().Writedown).Should()
            .BeApproximately(0, 0.01, structure);
    }

    [Theory]
    [InlineData("FORCE_PAYDOWN(SINGLE('B'), SEQ(SINGLE('A')))")]
    public void In_the_engine_a_recovery_structure_that_pays_more_than_it_is_given_stops_the_run(string structure)
    {
        var act = () => RuleDeal().WithPayRule("RecovStructOverride", $"SET_RECOV_STRUCT({structure})")
            .BuildAndRun(OnePeriod());
        act.Should().Throw<Exception>().WithMessage("*more principal than the period collected*");
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

    private static WaterfallStepDto Recovery(WaterfallRequest request) =>
        request.Deal.UnifiedWaterfall!.Steps.First(s => s.Type == "PRINCIPAL" && s.Source == "recovery");

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
    /// recovery-principal structure (the turbo states none of its own), the write-down takes what
    /// is left over the pool, and the strip is paid from the release step.
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
            new() { Type = "EXCESS_TURBO", Target = target },
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
        Func<int, double>? recovery = null, double prepay = 0)
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        for (var i = 0; i < 12; i++)
        {
            var interest = balance * Wac / 100 / 12;
            var def = defaulted(i);
            var recov = recovery?.Invoke(i) ?? def * recoveryRate;
            var scheduled = balance * 0.01;
            var endBalance = balance - scheduled - def - prepay;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1,
                CashflowDate = FirstPayDate.AddMonths(i),
                GroupNum = "1",
                BeginBalance = balance,
                Balance = endBalance,
                ScheduledPrincipal = scheduled,
                UnscheduledPrincipal = prepay,
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



    // ---- Paying back earlier write-downs (graam-harmony#6199 D7) ---------------------------------

    /// <summary>The steps with WRITEUP after WRITEDOWN: the documents' cumulative item.</summary>
    private static WaterfallRequest WriteUpSteps(List<PeriodCashflowDto> pool, List<string>? order = null)
    {
        var request = Steps(pool, order: order ?? new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "EXCESS_TURBO",
            "WRITEDOWN", "WRITEUP", "EXCESS_RELEASE"
        });
        request.Deal.UnifiedWaterfall!.Steps.Add(new WaterfallStepDto { Type = "WRITEUP" });
        return request;
    }

    private static double CumWrittenDown(WaterfallResponse r, string name, int i) =>
        r.TrancheCashflows[name][i].CumWritedown;

    [Fact]
    public void A_write_down_is_paid_back_out_of_later_excess_before_the_strip_is_paid()
    {
        // One loss in period 1, more than that period's excess; no loss after it.
        var pool = Collateral(defaulted: i => i == 0 ? 2_000_000 : 0, recoveryRate: 0.5);
        var r = Run(WriteUpSteps(pool));

        var loss = pool[0].DefaultedPrincipal - pool[0].RecoveryPrincipal;
        var excess0 = Excess(pool[0], r, 0);
        excess0.Should().BeLessThan(loss, "the fixture: the loss is more than the period's excess");
        CumWrittenDown(r, "B1", 0).Should().BeApproximately(loss - excess0, 0.01, "only the remainder is written down");
        r.TrancheCashflows["XS"][0].Interest.Should().BeApproximately(0, 0.01);

        var restoredAt = -1;
        for (var i = 1; i < pool.Count; i++)
        {
            var owed = CumWrittenDown(r, "B1", i - 1);
            var excess = Excess(pool[i], r, i);
            var paidBack = Math.Min(owed, excess);
            (owed - CumWrittenDown(r, "B1", i)).Should().BeApproximately(paidBack, 0.01,
                $"period {i + 1}: the excess pays back what is still written down");
            r.TrancheCashflows["XS"][i].Interest.Should().BeApproximately(excess - paidBack, 0.01,
                $"period {i + 1}: the strip is paid only what is left once the write-down is paid back");
            NotesAt(r, i).Should().BeApproximately(pool[i].Balance, 0.01, $"period {i + 1}: the notes end at the pool");
            if (restoredAt < 0 && CumWrittenDown(r, "B1", i) < 0.01)
                restoredAt = i;
        }

        restoredAt.Should().BeGreaterThan(1, "the fixture: paying it back takes more than one period");
        r.TrancheCashflows["XS"][restoredAt + 1].Interest.Should().BeApproximately(
            Excess(pool[restoredAt + 1], r, restoredAt + 1), 0.01, "once whole, the strip takes the whole excess");
    }

    [Fact]
    public void Without_WRITEUP_the_strip_takes_the_excess_from_the_period_after_the_write_down()
    {
        // Today's steps (D1 to D3), for the contrast: nothing is paid back.
        var pool = Collateral(defaulted: i => i == 0 ? 2_000_000 : 0, recoveryRate: 0.5);
        var r = Run(Steps(pool));

        var written = CumWrittenDown(r, "B1", 0);
        written.Should().BeGreaterThan(100_000, "the fixture: period 1 writes B1 down");
        for (var i = 1; i < 6; i++)
        {
            CumWrittenDown(r, "B1", i).Should().BeApproximately(written, 0.01, $"period {i + 1}");
            r.TrancheCashflows["XS"][i].Interest.Should().BeApproximately(Excess(pool[i], r, i), 0.01,
                $"period {i + 1}");
        }
    }

    [Fact]
    public void The_write_up_restores_the_most_senior_class_first()
    {
        // A 35,000,000 loss in period 1 writes B1 (30,000,000) off and A1 down by the rest less the
        // excess; the excess after that writes A1 back up before B1.
        var pool = Collateral(defaulted: i => i == 0 ? 70_000_000 : 0, recoveryRate: 0.5);
        var r = Run(WriteUpSteps(pool));

        CumWrittenDown(r, "B1", 0).Should().BeApproximately(B1Balance, 0.01);
        CumWrittenDown(r, "A1", 0).Should().BeGreaterThan(1_000_000, "the fixture: A1 is written down too");
        var a1Owed = CumWrittenDown(r, "A1", 0);
        for (var i = 1; i < pool.Count; i++)
        {
            // A write-up shows as a negative write-down in its period. (A class written off to zero
            // reports a cumulative write-down of 0 on its later rows, so its own column is read.)
            var a1Up = -r.TrancheCashflows["A1"][i].Writedown;
            var b1Up = -r.TrancheCashflows["B1"][i].Writedown;
            if (b1Up > 0.01)
                a1Owed.Should().BeApproximately(0, 0.01, $"period {i + 1}: B1 is written up only once A1 is whole");
            a1Owed -= Math.Max(a1Up, 0);
            NotesAt(r, i).Should().BeApproximately(pool[i].Balance, 0.01, $"period {i + 1}");
        }

        (CumWrittenDown(r, "A1", 0) - CumWrittenDown(r, "A1", pool.Count - 1)).Should().BeGreaterThan(0,
            "the fixture: A1 is written back up inside the run");
    }

    [Fact]
    public void A_recovery_that_comes_after_its_loss_was_written_down_writes_the_class_back_up()
    {
        // Period 1 defaults 2,000,000 with nothing recovered; period 3 recovers 800,000 with no
        // default. Paid as principal, the recovery takes the notes under the pool; the write-up
        // restores the class the loss was written off, so the notes end at the pool.
        var pool = Collateral(defaulted: i => i == 0 ? 2_000_000 : 0, recoveryRate: 0,
            recovery: i => i == 2 ? 800_000 : 0);
        var steps = Run(WriteUpSteps(pool));
        var without = Run(Steps(pool));

        (CumWrittenDown(steps, "B1", 1) - CumWrittenDown(steps, "B1", 2)).Should().BeGreaterThan(800_000 - 0.01,
            "period 3: the recovery and the excess both go to writing B1 back up");
        NotesAt(steps, 2).Should().BeApproximately(pool[2].Balance, 0.01);
        (pool[2].Balance - NotesAt(without, 2)).Should().BeApproximately(800_000, 0.01,
            "without WRITEUP the notes stand under the pool by the recovery");
    }

    [Fact]
    public void WRITEUP_needs_an_overPool_write_down()
    {
        var request = WriteUpSteps(Collateral(defaulted: _ => 0, recoveryRate: 0.5));
        request.Deal.UnifiedWaterfall!.Steps.Single(s => s.Type == "WRITEDOWN").Size = null;
        request.Deal.UnifiedWaterfall.Steps.Single(s => s.Type == "EXCESS_TURBO").Target = null;
        Refusal(request).Should().Contain("which only a WRITEDOWN sized overPool measures");
    }

    [Fact]
    public void WRITEUP_runs_after_WRITEDOWN()
    {
        var request = WriteUpSteps(Collateral(defaulted: _ => 0, recoveryRate: 0.5), new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "EXCESS_TURBO",
            "WRITEUP", "WRITEDOWN", "EXCESS_RELEASE"
        });
        Refusal(request).Should().Contain("WRITEUP runs ahead of WRITEDOWN");
    }

    [Fact]
    public void A_step_that_pays_the_notes_after_WRITEUP_is_refused()
    {
        var request = WriteUpSteps(Collateral(defaulted: _ => 0, recoveryRate: 0.5), new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN",
            "WRITEUP", "EXCESS_TURBO", "EXCESS_RELEASE"
        });
        Refusal(request).Should().Contain("EXCESS_TURBO");
    }

    [Fact]
    public void WRITEUP_named_twice_is_refused()
    {
        var request = WriteUpSteps(Collateral(defaulted: _ => 0, recoveryRate: 0.5), new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "EXCESS_TURBO",
            "WRITEDOWN", "WRITEUP", "WRITEUP", "EXCESS_RELEASE"
        });
        Refusal(request).Should().Contain("WRITEUP is named 2 times");
    }

    [Fact]
    public void A_WRITEUP_step_the_order_leaves_out_is_refused()
    {
        var request = Steps(Collateral(defaulted: _ => 0, recoveryRate: 0.5));
        request.Deal.UnifiedWaterfall!.Steps.Add(new WaterfallStepDto { Type = "WRITEUP" });
        Refusal(request).Should().Contain("the execution order has no WRITEUP");
    }

    [Fact]
    public void A_WRITEUP_step_with_a_structure_is_refused()
    {
        var request = WriteUpSteps(Collateral(defaulted: _ => 0, recoveryRate: 0.5));
        request.Deal.UnifiedWaterfall!.Steps.Single(s => s.Type == "WRITEUP").Structure = Seq("A1", "B1");
        Refusal(request).Should().Contain("WRITEUP states a structure or a field");
    }

    [Fact]
    public void WRITEUP_with_no_WRITEDOWN_in_the_order_is_refused()
    {
        var request = WriteUpSteps(Collateral(defaulted: _ => 0, recoveryRate: 0.5), new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "EXCESS_TURBO",
            "WRITEUP", "EXCESS_RELEASE"
        });
        Refusal(request).Should().Contain("names WRITEUP and no WRITEDOWN");
    }

    [Fact]
    public void WRITEUP_beside_an_OC_target_is_refused()
    {
        // The pool standing above the notes is the overcollateralization the target builds.
        var request = WriteUpSteps(Collateral(defaulted: _ => 0, recoveryRate: 0.5));
        Turbo(request).Target = null;
        Turbo(request).OcTarget = new OcTargetDto { TargetPct = 0.02 };
        Refusal(request).Should().Contain("an OC target together with WRITEUP");
    }

    [Fact]
    public void WRITEUP_on_a_deal_with_a_class_cut_from_a_note_is_refused()
    {
        // A class-cut IO is written down with the note it is cut from; the write-up would restore
        // the note and leave it down.
        var request = WriteUpSteps(Collateral(defaulted: _ => 0, recoveryRate: 0.5));
        request.Deal.Tranches.Add(new TrancheDto
        {
            TrancheName = "A1X", OriginalBalance = A1Balance, TrancheType = "Offered", CashflowType = "IO",
            CouponType = "Fixed", FixedCoupon = 1.0, NotionalReference = "A1", SubordinationOrder = 3,
            FirstPayDate = FirstPayDate, PayFrequency = 12, PayDay = FirstPayDate.Day
        });
        Refusal(request).Should().Contain("WRITEUP and classes written down with the notes they are cut from (A1X)");
    }

    [Fact]
    public void WRITEUP_with_a_pro_rata_write_down_is_refused_and_the_same_deal_runs_without_it()
    {
        // Classes written down together are restored together in the documents; one after another
        // is an order no document states.
        var pool = Collateral(defaulted: _ => 0, recoveryRate: 0.5);
        var proRata = new PayableStructureDto { Type = "PRORATA", Tranches = new List<string> { "B1", "A1" } };
        var request = WriteUpSteps(pool);
        request.Deal.UnifiedWaterfall!.Steps.Single(s => s.Type == "WRITEDOWN").Structure = proRata;
        Refusal(request).Should().Contain("a write-down structure that takes classes pro rata (B1, A1)");

        var without = Steps(pool);
        without.Deal.UnifiedWaterfall!.Steps.Single(s => s.Type == "WRITEDOWN").Structure = proRata;
        Try(without).Refused.Should().BeNull("the same deal without WRITEUP");
    }

    // ---- What the turbo spends, and what stops it ----------------------------------------------

    [Fact]
    public void The_turbo_spends_what_the_recovery_structure_paid_and_the_rest_goes_on_to_the_strip()
    {
        // The recovery structure reaches only a junior class of 100,000; with no recoveries the
        // turbo is its only payment. Asked for the excess, it can pay the class only what the
        // class holds: the turbo spends that, the strip is paid the rest, and the notes still
        // above the pool are written down.
        var request = Steps(Collateral(defaulted: _ => 600_000, recoveryRate: 0));
        request.Deal.Tranches.Single(t => t.TrancheName == "A1").OriginalBalance = PoolBalance - 100_000;
        request.Deal.Tranches.Single(t => t.TrancheName == "B1").OriginalBalance = 100_000;
        Recovery(request).Default = Single("B1");
        request.ReportCashLedger = true;
        var r = Run(request);

        var excess = Excess(request.CollateralCashflows[0], r, 0);
        excess.Should().BeGreaterThan(100_000, "the fixture: the excess is more than the class holds");
        r.TrancheCashflows["B1"][0].UnscheduledPrincipal.Should().BeApproximately(100_000, 0.01);
        r.TrancheCashflows["XS"][0].Interest.Should().BeApproximately(excess - 100_000, 0.01,
            "the strip is paid what the turbo could not place");
        r.TrancheCashflows["A1"][0].Writedown.Should().BeApproximately(600_000 - 100_000, 0.01);
        Balances(r.CashLedger!, "a recovery structure that can take part of the excess");
    }

    [Fact]
    public void A_recovery_structure_that_pays_the_turbo_more_than_it_gave_stops_the_run()
    {
        // With no recoveries the recovery step pays nothing, and the turbo is the first payment
        // through a forced paydown: asked for the excess, it pays its class in full.
        var request = Steps(Collateral(defaulted: _ => 600_000, recoveryRate: 0));
        request.Deal.Tranches.Single(t => t.TrancheName == "A1").OriginalBalance = 2_500_000;
        request.Deal.Tranches.Single(t => t.TrancheName == "B1").OriginalBalance = 20_000_000;
        request.Deal.Tranches.Add(Note("C1", PoolBalance - 22_500_000, 2));
        foreach (var step in request.Deal.UnifiedWaterfall!.Steps.Where(s => s.Type != "EXCESS_RELEASE"))
        {
            if (step.Structure != null) step.Structure = step.Type == "WRITEDOWN" ? Seq("C1", "B1", "A1") : Seq("A1", "B1", "C1");
            if (step.Default != null) step.Default = Seq("A1", "B1", "C1");
        }

        Recovery(request).Default = new PayableStructureDto
            { Type = "FORCE_PAYDOWN", Forced = Single("C1"), Support = Seq("B1") };
        Refusal(request).Should().Contain("EXCESS_TURBO with a target of zero gave the recovery structure")
            .And.Contain("and it paid");
    }

    [Fact]
    public void An_overdraft_on_one_principal_total_is_not_netted_against_principal_left_on_another()
    {
        // The unscheduled structure reaches only a class of 50,000, so most of the period's
        // prepayments are left unpaid. The recovery structure is a forced paydown of a class of
        // 400,000 on 300,000 of recoveries, so recovery principal is overdrawn by 100,000. Netted,
        // the 450,000 left would hide the overdraft; each is its own fault, and the overdraft is
        // the one reported.
        var request = Steps(Collateral(defaulted: _ => 600_000, recoveryRate: 0.5, prepay: 500_000));
        request.Deal.Tranches.Single(t => t.TrancheName == "A1").OriginalBalance = PoolBalance - 450_000;
        request.Deal.Tranches.Single(t => t.TrancheName == "B1").OriginalBalance = 400_000;
        request.Deal.Tranches.Add(Note("C1", 50_000, 2));
        foreach (var step in request.Deal.UnifiedWaterfall!.Steps.Where(s => s.Type != "EXCESS_RELEASE"))
        {
            if (step.Structure != null) step.Structure = step.Type == "WRITEDOWN" ? Seq("C1", "B1", "A1") : Seq("A1", "B1", "C1");
            if (step.Default != null) step.Default = Seq("A1", "B1", "C1");
        }

        request.Deal.UnifiedWaterfall!.Steps.Single(s => s.Type == "PRINCIPAL" && s.Source == "unscheduled")
            .Default = Single("C1");
        Recovery(request).Default = new PayableStructureDto
            { Type = "FORCE_PAYDOWN", Forced = Single("B1"), Support = Seq("A1") };
        Refusal(request).Should().Contain("more principal than the period collected");
    }

    [Fact]
    public void An_overPool_writedown_that_writes_down_a_class_that_is_not_a_note_stops_the_run()
    {
        // An IO class on the senior note heads the write-down structure. Written down, it leaves
        // the notes above the pool, and the same loss would be measured again next period.
        TrancheDto Io() => new()
        {
            TrancheName = "AIO", OriginalBalance = A1Balance, TrancheType = "Offered", CashflowType = "IO",
            CouponType = "Fixed", FixedCoupon = 1.0, NotionalReference = "A1", SubordinationOrder = 3,
            FirstPayDate = FirstPayDate, PayFrequency = 12, PayDay = FirstPayDate.Day
        };
        WaterfallRequest Deal(string? size)
        {
            var request = Steps(MixedLosses(), size: size, target: size == null ? null : 0,
                order: size == null ? new List<string>
                {
                    "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN",
                    "EXCESS_RELEASE"
                } : null);
            request.Deal.Tranches.Add(Io());
            request.Deal.UnifiedWaterfall!.Steps.Single(s => s.Type == "INTEREST").Structure = Seq("A1", "B1", "AIO");
            request.Deal.UnifiedWaterfall!.Steps.Single(s => s.Type == "WRITEDOWN").Structure = Seq("AIO", "B1", "A1");
            return request;
        }

        Refusal(Deal("overPool")).Should().Contain("WRITEDOWN sized overPool wrote")
            .And.Contain("down on AIO").And.Contain("which the engine does not count as a note");
        Try(Deal(null)).Refused.Should().BeNull("the same deal with neither field stated runs, as on main");
    }

    [Fact]
    public void The_strip_named_first_in_the_writedown_structure_takes_nothing_and_the_notes_take_the_loss()
    {
        // The strip has no principal balance to write down, so naming it first is not refused and
        // the notes take the loss.
        var request = Steps(MixedLosses());
        request.Deal.UnifiedWaterfall!.Steps.Single(s => s.Type == "WRITEDOWN").Structure = Seq("XS", "B1", "A1");
        SameNumbers(Run(Steps(MixedLosses())), Run(request), "the strip heading the write-down structure");
    }


    // ---- Round-6 review: the tolerance, the non-note check, the thresholds ---------------------

    /// <summary>
    /// Three periods built by hand: the first collects the principal given, the rest a plain month.
    /// </summary>
    private static List<PeriodCashflowDto> Periods(double scheduled, double prepay, double defaulted, double recovered)
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        for (var i = 0; i < 3; i++)
        {
            var (sp, up, d, r) = i == 0 ? (scheduled, prepay, defaulted, recovered) : (balance * 0.01, 0.0, 0.0, 0.0);
            var interest = balance * Wac / 100 / 12;
            var end = balance - sp - up - d;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = "1", BeginBalance = balance,
                Balance = end, ScheduledPrincipal = sp, UnscheduledPrincipal = up, Interest = interest,
                NetInterest = interest, DefaultedPrincipal = d, RecoveryPrincipal = r, CollateralLoss = d - r, Wac = Wac
            });
            balance = end;
        }

        return periods;
    }

    [Theory]
    [MemberData(nameof(FieldsStated))]
    public void Each_principal_total_is_held_to_a_unit_on_its_own(string? size, double? target)
    {
        // A pro-rata split of prepayments and one of recoveries each drop under a unit (0.70 and
        // 0.80 here): correct, and each within the tolerance. Summed they pass a unit, which
        // stopped this deal. At a cent the run stops on either.
        var request = Steps(Periods(scheduled: 600_000, prepay: 4_000_007, defaulted: 8, recovered: 8), size, target,
            OrderFor(size, target));
        request.Deal.Tranches.Single(t => t.TrancheName == "A1").OriginalBalance = 1_000_000;
        request.Deal.Tranches.Single(t => t.TrancheName == "B1").OriginalBalance = 90_000_000;
        request.Deal.Tranches.Add(Note("A2", 9_000_000, 0));
        var split = new PayableStructureDto
        {
            Type = "SEQ", Children = new List<PayableStructureDto> { ProRata("A2", "A1"), Single("B1") }
        };
        foreach (var step in request.Deal.UnifiedWaterfall!.Steps)
        {
            if (step.Type == "INTEREST") step.Structure = Seq("A1", "A2", "B1");
            if (step.Type == "WRITEDOWN") step.Structure = Seq("B1", "A2", "A1");
            if (step.Type == "PRINCIPAL")
                step.Default = step.Source == "scheduled" ? Seq("A1", "A2", "B1") : split;
        }

        var (ran, refused) = Try(request);
        refused.Should().BeNull();
        var notes = new[] { "A1", "A2", "B1" }.Sum(n => ran!.TrancheCashflows[n][0].Balance);
        notes.Should().BeApproximately(request.CollateralCashflows[0].Balance, 2.0,
            "the notes end the period within the drops of the pool");
    }

    [Fact]
    public void A_negative_turbo_payment_to_a_class_that_is_not_a_note_stops_the_run()
    {
        // A forced paydown with a funded residual class as its support: asked for the excess, it
        // pays the junior note in full and "pays" the residual class the difference, a negative
        // amount. With no recoveries the turbo is the first payment through it.
        var request = Steps(Collateral(defaulted: _ => 600_000, recoveryRate: 0));
        request.Deal.Tranches.Add(new TrancheDto
        {
            TrancheName = "R", OriginalBalance = 5_000_000, TrancheType = "Offered", CashflowType = "PI",
            CouponType = "Residual", FixedCoupon = 0.0, SubordinationOrder = 9, FirstPayDate = FirstPayDate,
            PayFrequency = 12, PayDay = FirstPayDate.Day
        });
        Recovery(request).Default = new PayableStructureDto
            { Type = "FORCE_PAYDOWN", Forced = Single("B1"), Support = Single("R") };
        Refusal(request).Should().Contain("EXCESS_TURBO with a target of zero paid -")
            .And.Contain("to R on").And.Contain("which the engine does not count as a note");
    }

    [Fact]
    public void Notes_less_than_a_unit_above_the_pool_are_paid_down_to_it()
    {
        // The senior note is stored half a dollar over the pool. At no loss the turbo pays that
        // half dollar out of excess in the first period, and nothing after.
        var request = Steps(Collateral(defaulted: _ => 0, recoveryRate: 0));
        request.Deal.Tranches.Single(t => t.TrancheName == "A1").OriginalBalance = A1Balance + 0.5;
        var r = Run(request);
        r.TrancheCashflows["A1"][0].UnscheduledPrincipal.Should().BeApproximately(0.5, 0.001);
        r.TrancheCashflows["A1"][1].UnscheduledPrincipal.Should().BeApproximately(0, 0.001);
        NotesAt(r, 0).Should().BeApproximately(request.CollateralCashflows[0].Balance, 0.001);
    }

    [Fact]
    public void OverPool_on_a_deal_the_built_in_rule_runs_on_returns_the_rules_numbers()
    {
        // The order names no excess step, so the built-in rule still runs; sized overPool, the
        // write-down it hands the rule is the notes over the pool, which on a deal whose notes
        // start at the pool is the period's loss less what the rule paid.
        var pool = MixedLosses();
        var sized = BuiltInRule(pool);
        sized.Deal.UnifiedWaterfall!.Steps.Single(s => s.Type == "WRITEDOWN").Size = "overPool";
        var rule = Run(BuiltInRule(pool));
        rule.TrancheCashflows["B1"].Sum(c => c.Writedown).Should().BeGreaterThan(1_000_000, "the fixture");
        SameNumbers(rule, Run(sized), "overPool beside the built-in rule");
    }

    [Theory]
    [InlineData("recovery")]
    [InlineData("unscheduled")]
    public void Each_forbearance_flow_in_the_collateral_is_not_supported(string flow)
    {
        var pool = MixedLosses();
        if (flow == "recovery") pool[2].ForbearanceRecovery = 100_000;
        else pool[2].ForbearanceUnscheduled = 100_000;
        Refusal(Steps(pool)).Should().Contain("not supported on a deal with a forbearance balance (first on 04/25/2026)");
    }

    [Theory]
    [InlineData("default")]
    [InlineData("rules")]
    [InlineData("useStructure")]
    [InlineData("onTriggerFail")]
    public void A_zero_target_turbo_stating_a_structure_any_other_way_is_refused(string field)
    {
        var request = Steps(MixedLosses());
        var turbo = Turbo(request);
        switch (field)
        {
            case "default": turbo.Default = Seq("A1", "B1"); break;
            case "rules": turbo.Rules = new List<WaterfallRuleDto> { new() { Structure = Seq("A1", "B1") } }; break;
            case "useStructure": turbo.UseStructure = "recovery"; break;
            default: turbo.OnTriggerFail = new TriggerConditionDto { Structure = Seq("A1", "B1") }; break;
        }

        Refusal(request).Should().Contain("states target 0 and a structure of its own");
    }
}
