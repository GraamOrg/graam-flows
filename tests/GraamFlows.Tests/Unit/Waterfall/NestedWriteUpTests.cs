using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// WRITEUP over a nested write-down (graam-harmony#6333): a deal whose senior classes are written down
/// pro rata between groups, and inside each group in its own order.
///
/// The write-down here is <c>SEQ[B1, M1, PRORATA[PRORATA[A1F, A1L], SEQ[A1B, A1A]]]</c>: B1, then M1,
/// then the senior classes pro rata between two groups, the first group's two classes pro rata and the
/// second group's sequentially (A1B first). Documents of this shape reimburse the written-down amounts
/// out of later excess in the reverse: the senior groups first, concurrently and pro rata by each
/// group's unreimbursed written-down amount (not its balance), the first group's classes pro rata by the
/// same measure, the second group's A1A before A1B; then M1, then B1.
/// </summary>
public class NestedWriteUpTests
{
    private static readonly DateTime FirstPayDate = new(2026, 2, 25);
    private const double Wac = 9.0;
    private const double NoteCoupon = 5.0;

    private static readonly (string Name, double Balance)[] Notes =
    {
        ("A1F", 20_000_000), ("A1L", 20_000_000), ("A1A", 25_000_000), ("A1B", 5_000_000),
        ("M1", 15_000_000), ("B1", 15_000_000)
    };

    private static readonly double PoolBalance = Notes.Sum(n => n.Balance);
    private static readonly string[] Seniors = { "A1F", "A1L", "A1A", "A1B" };

    /// <summary>A 50,000,000 loss in period 1, then recoveries in periods 3, 5 and 8.</summary>
    private static List<PeriodCashflowDto> LossThenRecoveries() => Collateral(
        defaulted: i => i == 0 ? 50_000_000 : 0,
        recovery: i => i switch { 2 => 6_000_000, 4 => 15_000_000, 7 => 25_000_000, _ => 0 });

    [Fact]
    public void An_over_pool_loss_writes_the_nested_structure_down_in_its_order()
    {
        var r = Run(Request(LossThenRecoveries()));

        Down(r, "B1", 0).Should().BeApproximately(15_000_000, 0.01, "B1 is written off first");
        Down(r, "M1", 0).Should().BeApproximately(15_000_000, 0.01, "then M1");
        Down(r, "A1B", 0).Should().BeApproximately(5_000_000, 0.01, "the second group writes A1B off first");
        Down(r, "A1A", 0).Should().BeGreaterThan(1_000_000, "the fixture: A1A is written down too");
        Down(r, "A1F", 0).Should().BeGreaterThan(1_000_000, "the fixture: the first group is written down");
        Down(r, "A1L", 0).Should().BeGreaterThan(1_000_000, "the fixture: the first group is written down");
    }

    [Fact]
    public void Recoveries_write_each_class_back_up_in_the_reverse_of_the_nested_write_down()
    {
        var pool = LossThenRecoveries();
        var r = Run(Request(pool));

        var owed = Notes.ToDictionary(n => n.Name, n => Down(r, n.Name, 0));
        var writeUpPeriods = 0;
        var firstGroupSplitAgainstBalance = 0.0;
        for (var i = 1; i < pool.Count; i++)
        {
            var at = $"period {i + 1}";
            var up = Notes.ToDictionary(n => n.Name, n => -Down(r, n.Name, i));
            up.Values.Should().OnlyContain(u => u > -0.01, $"{at}: nothing is written down after period 1");
            NotesAt(r, i).Should().BeApproximately(pool[i].Balance, 0.01, $"{at}: the notes end at the pool");

            var total = up.Values.Sum();
            if (total <= 0.01)
                continue;
            writeUpPeriods++;

            var seniorsOwed = Seniors.Sum(n => owed[n]);
            if (seniorsOwed > 0.01)
            {
                // The senior groups are restored before M1, and M1 before B1.
                if (Seniors.Sum(n => up[n]) < seniorsOwed - 0.01)
                {
                    up["M1"].Should().BeApproximately(0, 0.01, $"{at}: M1 waits for the senior classes");
                    up["B1"].Should().BeApproximately(0, 0.01, $"{at}: B1 waits for the senior classes");
                }

                // Between the groups: pro rata by each group's unreimbursed write-down.
                var group1Owed = owed["A1F"] + owed["A1L"];
                var group2Owed = owed["A1A"] + owed["A1B"];
                var group1Up = up["A1F"] + up["A1L"];
                var group2Up = up["A1A"] + up["A1B"];
                if (group1Owed > 0.01 && group2Owed > 0.01 && group1Up < group1Owed - 0.01)
                    (group1Up / (group1Up + group2Up)).Should().BeApproximately(
                        group1Owed / (group1Owed + group2Owed), 1e-9,
                        $"{at}: the groups share the write-up by what each still has written down");

                // Inside the first group: pro rata by each class's unreimbursed write-down, not its balance.
                if (group1Up > 0.01 && owed["A1F"] > 0.01 && owed["A1L"] > 0.01)
                {
                    var byOwed = owed["A1F"] / group1Owed;
                    (up["A1F"] / group1Up).Should().BeApproximately(byOwed, 1e-9,
                        $"{at}: A1F and A1L share by what each still has written down");
                    var a1fBal = Bal(r, "A1F", i - 1);
                    var byBalance = a1fBal / (a1fBal + Bal(r, "A1L", i - 1));
                    firstGroupSplitAgainstBalance = Math.Max(firstGroupSplitAgainstBalance, Math.Abs(byBalance - byOwed));
                }

                // Inside the second group: the reverse of SEQ[A1B, A1A], so A1A before A1B.
                if (up["A1A"] < owed["A1A"] - 0.01)
                    up["A1B"].Should().BeApproximately(0, 0.01, $"{at}: A1B waits until A1A is whole");
            }
            else if (owed["M1"] > 0.01 && up["M1"] < owed["M1"] - 0.01)
            {
                up["B1"].Should().BeApproximately(0, 0.01, $"{at}: B1 waits until M1 is whole");
            }

            foreach (var n in Notes)
            {
                up[n.Name].Should().BeLessThanOrEqualTo(owed[n.Name] + 0.01, $"{at}: {n.Name} at most what it lost");
                owed[n.Name] -= up[n.Name];
            }
        }

        // Anti-vacuity: the run reaches every branch of the rule.
        writeUpPeriods.Should().BeGreaterThan(3, "the fixture: recoveries and excess write up over several periods");
        firstGroupSplitAgainstBalance.Should().BeGreaterThan(0.05,
            "the fixture: a split by balance would differ from the split by unreimbursed write-down");
        foreach (var name in Seniors)
            owed[name].Should().BeApproximately(0, 0.01, $"the fixture: {name} is written all the way back up");
        owed["M1"].Should().BeLessThan(Down(r, "M1", 0) - 1_000_000, "the fixture: M1 is written up after the seniors");
        owed["B1"].Should().BeGreaterThan(1_000_000, "the fixture: B1, last in line, is still written down");
    }

    [Fact]
    public void The_write_down_is_the_same_with_or_without_WRITEUP()
    {
        var pool = LossThenRecoveries();
        var withUp = Run(Request(pool));
        var without = Run(Request(pool, writesUp: false));
        foreach (var n in Notes)
            Down(withUp, n.Name, 0).Should().BeApproximately(Down(without, n.Name, 0), 0.01, n.Name);

        // Without WRITEUP the recoveries leave the notes under the pool.
        (pool[2].Balance - NotesAt(without, 2)).Should().BeGreaterThan(6_000_000 - 0.01);
    }

    [Fact]
    public void A_structure_the_write_up_has_no_reverse_for_is_refused_inside_a_nested_write_down()
    {
        var request = Request(LossThenRecoveries());
        var writedown = request.Deal.UnifiedWaterfall!.Steps.Single(s => s.Type == "WRITEDOWN");
        writedown.Structure!.Children![2].Children![1] = new PayableStructureDto
        {
            Type = "SHIFTI", ShiftPercent = 0.5, Seniors = Single("A1A"), Subordinates = Single("A1B")
        };
        Refusal(request).Should().Contain("not built of sequences and pro-rata groups of single classes")
            .And.Contain("ShiftingInterestStructure");
    }

    [Fact]
    public void A_class_named_in_two_groups_is_refused()
    {
        var request = Request(LossThenRecoveries());
        var writedown = request.Deal.UnifiedWaterfall!.Steps.Single(s => s.Type == "WRITEDOWN");
        writedown.Structure!.Children![2].Children![1] = Seq("A1B", "A1F");
        Refusal(request).Should().Contain("The write-down structure names A1F twice");
    }

    // ------------------------------------------------------------------------------------------

    private static double Down(WaterfallResponse r, string name, int i) => r.TrancheCashflows[name][i].Writedown;
    private static double Bal(WaterfallResponse r, string name, int i) => r.TrancheCashflows[name][i].Balance;

    private static double NotesAt(WaterfallResponse r, int i) => Notes.Sum(n => Bal(r, n.Name, i));

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

    private static PayableStructureDto Single(string tranche) => new() { Type = "SINGLE", Tranche = tranche };

    private static PayableStructureDto Seq(params string[] tranches) =>
        new() { Type = "SEQ", Tranches = tranches.ToList() };

    private static PayableStructureDto ProRata(params string[] tranches) =>
        new() { Type = "PRORATA", Tranches = tranches.ToList() };

    /// <summary>SEQ[B1, M1, PRORATA[PRORATA[A1F, A1L], SEQ[A1B, A1A]]].</summary>
    private static PayableStructureDto NestedWritedown() => new()
    {
        Type = "SEQ",
        Children = new List<PayableStructureDto>
        {
            Single("B1"), Single("M1"),
            new() { Type = "PRORATA", Children = new List<PayableStructureDto> { ProRata("A1F", "A1L"), Seq("A1B", "A1A") } }
        }
    };

    private static WaterfallRequest Request(List<PeriodCashflowDto> pool, bool writesUp = true)
    {
        var order = new List<string>
        {
            "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "EXCESS_TURBO",
            "WRITEDOWN"
        };
        if (writesUp)
            order.Add("WRITEUP");
        order.Add("EXCESS_RELEASE");

        var byPriority = Notes.Select(n => n.Name).ToArray();
        var steps = new List<WaterfallStepDto>
        {
            new() { Type = "INTEREST", Structure = Seq(byPriority) },
            new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq(byPriority) },
            new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq(byPriority) },
            new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq(byPriority) },
            new() { Type = "EXCESS_TURBO", Target = 0 },
            new() { Type = "WRITEDOWN", Structure = NestedWritedown(), Size = "overPool" },
            new() { Type = "EXCESS_RELEASE", Structure = Seq("XS") }
        };
        if (writesUp)
            steps.Add(new WaterfallStepDto { Type = "WRITEUP" });

        var tranches = Notes.Select((n, i) => new TrancheDto
        {
            TrancheName = n.Name, OriginalBalance = n.Balance, TrancheType = "Offered", CashflowType = "PI",
            CouponType = "Fixed", FixedCoupon = NoteCoupon, SubordinationOrder = i, FirstPayDate = FirstPayDate,
            PayFrequency = 12, PayDay = FirstPayDate.Day
        }).ToList();
        tranches.Add(new TrancheDto
        {
            TrancheName = "XS", OriginalBalance = PoolBalance, TrancheType = "Offered", CashflowType = "IO",
            CouponType = "ExcessInterest", FixedCoupon = 0.0, ClassReference = "XS", SubordinationOrder = Notes.Length,
            FirstPayDate = FirstPayDate, PayFrequency = 12, PayDay = FirstPayDate.Day
        });

        return new WaterfallRequest
        {
            ProjectionDate = FirstPayDate.AddMonths(-1),
            CollateralCashflows = pool,
            Deal = new DealDto
            {
                DealName = "NESTED_WRITE_UP_TEST",
                WaterfallType = "ComposableStructure",
                ClosingDate = FirstPayDate.AddMonths(-1),
                Tranches = tranches,
                UnifiedWaterfall = new UnifiedWaterfallDto { ExecutionOrder = order, Steps = steps }
            }
        };
    }

    private static List<PeriodCashflowDto> Collateral(Func<int, double> defaulted, Func<int, double> recovery)
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = PoolBalance;
        for (var i = 0; i < 12; i++)
        {
            var interest = balance * Wac / 100 / 12;
            var def = defaulted(i);
            var recov = recovery(i);
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
                UnscheduledPrincipal = 0,
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
