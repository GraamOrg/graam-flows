using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Revolving;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>
///     What "retired" promises a caller: leave the series out of every later run and nothing changes.
///     These runs are built to reach a close with something still owed or held: interest, fees, a
///     write-down that can be reinstated, a reserve, a funding account, finance waiting.
/// </summary>
public class RetiredSeriesTests
{
    private const int Runs = 400;
    private const int Months = 10;

    private static double Between(Random random, double low, double high) => low + random.NextDouble() * (high - low);

    private static RevolvingClass Stressed(Random random, RevolvingClass c)
    {
        var kind = random.Next(5);
        return kind switch
        {
            0 => c with { WrittenDown = c.Balance },
            1 => c with { WrittenDown = c.Balance * Between(random, 0.0, 1.0) },
            2 => c with { Balance = 0.0, InterestShortfall = random.Next(2) * 5.0 },
            _ => c
        };
    }

    private static RevolvingSeries StackSeries(Random random, int i)
    {
        var size = Between(random, 500.0, 5_000.0);
        var series = CardProgram.Series($"S{i}", size * 0.8, size * 0.1, size * 0.1, Between(random, 0.0, 9.0),
            Between(random, 0.0, 9.0), Between(random, 0.0, 9.0), $"{random.Next(2)}") with
        {
            PrincipalSharing = random.Next(2) == 0,
            ExcessSharing = random.Next(2) == 0,
            Reserve = random.Next(3) == 0 ? size * 0.01 : 0.0,
            FeesDue = new Dictionary<string, double> { ["servicing"] = random.Next(3) == 0 ? size * 0.01 : 0.0 }
        };
        series = series with { Classes = series.Classes.Select(c => Stressed(random, c)).ToList() };
        return random.Next(4) switch
        {
            0 => series with
            {
                Phase = SeriesPhase.EarlyAmortization,
                FrozenPrincipalPct = Between(random, 0.05, 0.3),
                FrozenNumerator = size
            },
            1 => series with { FirstPayDate = new DateTime(2026, 8, 15).AddMonths(random.Next(1, 4)) },
            2 => series with
            {
                AccumulationStart = new DateTime(2026, 8, 1).AddMonths(random.Next(0, 4)),
                ExpectedDate = new DateTime(2027, 3, 15).AddMonths(random.Next(0, 6)),
                ControlledAmounts = new[] { size / 6.0 }
            },
            _ => series
        };
    }

    private static PoolDriverPath Drivers(Random random)
    {
        double[] Values(double low, double high) =>
            Enumerable.Range(0, 5).Select(_ => random.Next(3) == 0 ? 0.0 : Between(random, low, high)).ToArray();

        return new PoolDriverPath
        {
            PaymentRate = new PoolDriver(Values(0.1, 0.6)),
            NewReceivablesRate = new PoolDriver(Values(0.1, 0.5)),
            Yield = new PoolDriver(Values(0.001, 0.04)),
            ChargeOffRate = new PoolDriver(Values(0.0, 0.03)),
            RecoveryRate = new PoolDriver(new[] { 0.0 }),
            DilutionRate = new PoolDriver(new[] { 0.0 })
        };
    }

    private static RevolvingRun StackRun(int seed)
    {
        var random = new Random(seed);
        var series = Enumerable.Range(0, random.Next(2, 6)).Select(i => StackSeries(random, i)).ToList();
        var pool = series.Sum(s => s.Classes.Sum(c => c.Balance)) * Between(random, 1.1, 2.0) + 100.0;
        var program = CardProgram.Program(actionLag: random.Next(3), triggers: new[] { CardProgram.YieldBelowBaseRate },
            freeze: random.Next(2) == 0 ? PrincipalFreeze.FrozenPercent : PrincipalFreeze.FrozenNumerator);
        if (random.Next(3) == 0)
            program = program with { GroupSteps = new[] { new WaterfallStep(WaterfallVerb.ReallocateGroupFinance) } };
        return CardProgram.Run(program, pool, series, Drivers(random), Months);
    }

    private static RevolvingSeries Tranche(Random random, string name, string key)
    {
        var size = Between(random, 500.0, 5_000.0);
        var tranche = NoteProgram.Tranche(name, key, size, random.Next(3) == 0 ? 0.0 : Between(random, 1.0, 9.0)) with
        {
            Reserve = random.Next(3) == 0 ? size * 0.01 : 0.0,
            RequiredReserve = random.Next(4) == 0 ? size * 0.01 : 0.0,
            PriorNumerator = size,
            PriorFunding = 0.0
        };
        if (random.Next(3) == 0)
            tranche = tranche with { Fees = Array.Empty<RevolvingFee>() };
        tranche = tranche with
        {
            Classes = new[]
            {
                tranche.Classes[0] with
                {
                    InterestShortfall = random.Next(3) == 0 ? size * 0.01 : 0.0,
                    WrittenDown = random.Next(5) == 0 ? size : 0.0
                }
            }
        };
        return random.Next(3) switch
        {
            0 => tranche with
            {
                Phase = SeriesPhase.EarlyAmortization,
                FrozenNumerator = size,
                FrozenPrincipalPct = Between(random, 0.05, 0.3),
                FundingAccount = tranche.Classes[0].WrittenDown > 0.0 ? 0.0 : size
            },
            1 => NoteProgram.Accumulating(tranche, size, size * Between(random, 0.5, 1.0), size, 0.0, new[] { size / 2.0 },
                new DateTime(2026, 8, 1), new DateTime(2026, 9, 15).AddMonths(random.Next(0, 4))) with
            {
                FrozenPrincipalPct = Between(random, 0.05, 0.3),
                Classes = new[] { tranche.Classes[0] with { WrittenDown = 0.0 } }
            },
            _ => tranche
        };
    }

    private static RevolvingRun NoteRun(int seed)
    {
        var random = new Random(seed);
        var tranches = new List<RevolvingSeries>();
        for (var i = 0; i < random.Next(2, 5); i++)
            tranches.Add(Tranche(random, $"A{i}", "A"));
        tranches.Add(Tranche(random, "B0", "B"));
        tranches.Add(Tranche(random, "C0", "C"));
        var pool = tranches.Sum(t => t.Classes[0].Balance) * Between(random, 1.1, 2.0);
        var program = NoteProgram.Program(random.Next(2) == 0 ? PrincipalFreeze.FrozenNumerator : PrincipalFreeze.FrozenPercent,
            new[] { NoteProgram.ExcessSpreadBelowZero }, random.Next(2) == 0, random.Next(3));
        return CardProgram.Run(program, pool, tranches, Drivers(random), Months);
    }

    private static int CheckLeavingOutRetired(RevolvingRun run)
    {
        var whole = CardProgram.Execute(run);

        var months = CardProgram.MonthByMonth(run, whole, true);

        CardProgram.RequireSameMonths(whole, months);
        var lastHeld = months.Count;
        var left = 0;
        foreach (var series in whole.Series)
        {
            var gone = months.TakeWhile(m => m.Series.Any(s => s.Name == series.Name)).Count();
            if (gone >= lastHeld)
                continue;

            left++;
            foreach (var row in series.Periods.Skip(gone))
            {
                row.FeesPaid.Should().BeApproximately(0.0, 1e-6);
                row.Reserve.Should().BeApproximately(0.0, 1e-6);
                row.FundingAccount.Should().BeApproximately(0.0, 1e-6);
                row.FinanceAllocation.Should().Be(0.0);
            }

            series.Classes.Values.SelectMany(rows => rows.Skip(gone)).Should()
                .AllSatisfy(c => (c.InterestPaid + c.PrincipalPaid).Should().BeApproximately(0.0, 1e-6));
        }

        return left;
    }

    [Fact]
    public void LeavingOutEveryRetiredSeriesChangesNothingOnAStack()
    {
        Enumerable.Range(0, Runs).Sum(seed => CheckLeavingOutRetired(StackRun(seed))).Should().BeGreaterThan(20);
    }

    [Fact]
    public void LeavingOutEveryRetiredTrancheChangesNothing()
    {
        Enumerable.Range(0, Runs).Sum(seed => CheckLeavingOutRetired(NoteRun(seed))).Should().BeGreaterThan(50);
    }

    [Fact]
    public void AStackCutEveryMonthMatchesOneLongRun()
    {
        foreach (var run in Enumerable.Range(0, Runs).Select(StackRun))
        {
            var whole = CardProgram.Execute(run);
            CardProgram.RequireSameMonths(whole, CardProgram.MonthByMonth(run, whole));
        }
    }

    [Fact]
    public void NotesCutEveryMonthMatchOneLongRun()
    {
        foreach (var run in Enumerable.Range(0, Runs).Select(NoteRun))
        {
            var whole = CardProgram.Execute(run);
            CardProgram.RequireSameMonths(whole, CardProgram.MonthByMonth(run, whole));
        }
    }

    /// <summary>The first month every series has closed retired or stranded, and what paid after it.</summary>
    private static int CheckStoppingOnTheFlags(RevolvingRun run)
    {
        var whole = CardProgram.Execute(run);
        for (var month = 1; month < run.Periods; month++)
        {
            var part = CardProgram.Execute(run with { Periods = month });
            if (!part.Series.All(s => s.Retired || s.Stranded))
                continue;

            foreach (var series in whole.Series)
            {
                series.Periods.Skip(month).Should().AllSatisfy(p =>
                {
                    p.FeesPaid.Should().BeApproximately(0.0, 1e-6, series.Name);
                    p.FinanceAllocation.Should().BeApproximately(0.0, 1e-6, series.Name);
                });
                series.Classes.Values.SelectMany(rows => rows.Skip(month)).Should().AllSatisfy(c =>
                    (c.InterestPaid + c.PrincipalPaid).Should().BeApproximately(0.0, 1e-6, series.Name));
            }

            return 1;
        }

        return 0;
    }

    [Fact]
    public void StoppingWhenEverySeriesIsRetiredOrStrandedLosesNothing()
    {
        Enumerable.Range(0, Runs).SelectMany(seed => new[] { StackRun(seed), NoteRun(seed) })
            .Sum(CheckStoppingOnTheFlags).Should().BeGreaterThan(10);
    }

    [Fact]
    public void NoRetiredSeriesOwesOrHoldsAnything()
    {
        foreach (var run in Enumerable.Range(0, Runs).SelectMany(seed => new[] { StackRun(seed), NoteRun(seed) }))
            foreach (var series in CardProgram.Execute(run).Series.Where(s => s.Retired))
            {
                var closing = series.Closing;
                (closing.Reserve + closing.FundingAccount + closing.CarriedFinance + closing.FeesDue.Values.Sum()).Should()
                    .BeLessThan(0.05);
                closing.Classes.Should().AllSatisfy(c =>
                    (c.Balance + c.WrittenDown + c.InterestShortfall + c.DefaultDue).Should().BeLessThan(0.05));
            }
    }

    [Fact]
    public void AStrandedSeriesIsNeverRetired()
    {
        var stranded = 0;
        foreach (var run in Enumerable.Range(0, Runs).SelectMany(seed => new[] { StackRun(seed), NoteRun(seed) }))
        {
            var result = CardProgram.Execute(run with
            {
                Periods = 120,
                Drivers = run.Drivers! with
                {
                    NewReceivablesRate = new PoolDriver(new[] { 0.3 }),
                    PaymentRate = new PoolDriver(new[] { 0.3 })
                }
            });
            foreach (var series in result.Series)
            {
                (series.Retired && series.Stranded).Should().BeFalse();
                stranded += series.Stranded ? 1 : 0;
            }
        }

        stranded.Should().BeGreaterThan(0);
    }
}
