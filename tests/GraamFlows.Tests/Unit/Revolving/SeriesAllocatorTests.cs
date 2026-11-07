using FluentAssertions;
using GraamFlows.Revolving;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

public class SeriesAllocatorTests
{
    private static PoolPeriod Pool(double beginning, double ending)
    {
        return new PoolPeriod { Beginning = beginning, Ending = ending };
    }

    [Fact]
    public void FloatingIsTheNumeratorOverThePool()
    {
        var allocation = SeriesAllocator.Allocate(Pool(1_000.0, 1_200.0), PoolMeasure.PeriodStart,
            new[] { new SeriesClaim("A", 300.0, SeriesPhase.Revolving), new SeriesClaim("B", 200.0, SeriesPhase.Revolving) });

        allocation[0].FloatingPct.Should().Be(0.30);
        allocation[1].FloatingPct.Should().Be(0.20);
    }

    [Fact]
    public void TheSeriesDollarsMatchThePrintedTwoStepAllocation()
    {
        // Amex prints a series allocation % then a floating % of that slice. Their product is the
        // single-step share of the pool: Series 2024-1, September 2026.
        const double pool = 24_975_208_451.43;
        const double finance = 702_676_169.81;
        const double principal = 12_712_741_903.27;

        var share = SeriesAllocator.Allocate(new PoolPeriod { Beginning = pool, Ending = pool },
                PoolMeasure.PeriodStart,
                new[]
                {
                    new SeriesClaim("2024-1", 1_142_859_000.00, SeriesPhase.Revolving),
                    new SeriesClaim("Others", 12_800_020_000.00, SeriesPhase.Revolving)
                })[0];

        (share.FloatingPct * finance).Should().BeApproximately(32_154_277.57, 0.01);
        (share.PrincipalPct * principal).Should().BeApproximately(581_731_741.18, 0.01);
    }

    [Theory]
    [InlineData(PoolMeasure.PeriodStart, 0.25)]
    [InlineData(PoolMeasure.Average, 250.0 / 1500.0)]
    [InlineData(PoolMeasure.PeriodEnd, 0.125)]
    public void TheProgramChoosesWhereThePoolIsMeasured(PoolMeasure measure, double floating)
    {
        var allocation = SeriesAllocator.Allocate(Pool(1_000.0, 2_000.0), measure,
            new[] { new SeriesClaim("A", 250.0, SeriesPhase.Revolving) });

        allocation[0].FloatingPct.Should().BeApproximately(floating, 1e-12);
    }

    [Fact]
    public void ThePoolIsFlooredAtTheSumOfTheNumerators()
    {
        var allocation = SeriesAllocator.Allocate(Pool(100.0, 100.0), PoolMeasure.PeriodStart,
            new[] { new SeriesClaim("A", 90.0, SeriesPhase.Revolving), new SeriesClaim("B", 60.0, SeriesPhase.Revolving) });

        allocation[0].FloatingPct.Should().Be(0.60);
        allocation[1].FloatingPct.Should().Be(0.40);
    }

    [Fact]
    public void PrincipalEqualsFloatingWhileRevolvingThenFreezes()
    {
        double? frozen = null;
        var phase = new SeriesPhaseMachine();
        var numerator = 500.0;
        var pool = 1_000.0;
        double? frozenAt = null;

        for (var month = 0; month < 12; month++)
        {
            if (month == 6)
                phase.MoveTo(SeriesPhase.Accumulating);

            var row = SeriesAllocator.Allocate(Pool(pool, pool + 50.0), PoolMeasure.PeriodStart,
                new[] { new SeriesClaim("A", numerator, phase.Phase, frozen) })[0];
            frozen = row.FrozenPrincipalPct;

            if (month < 6)
            {
                row.PrincipalPct.Should().Be(row.FloatingPct);
                row.FrozenPrincipalPct.Should().BeNull();
            }
            else
            {
                frozenAt ??= row.PrincipalPct;
                row.PrincipalPct.Should().Be(frozenAt);
                if (month > 6)
                    row.FloatingPct.Should().BeLessThan(frozenAt!.Value);
                numerator -= 40.0;
            }

            pool += 50.0;
        }

        // Numerator over the pool on the last revolving day: the close of month 5.
        frozenAt.Should().Be(500.0 / 1_300.0);
    }

    [Fact]
    public void TheLastRevolvingDayIsThePeriodStartWhateverThePoolMeasure()
    {
        var row = SeriesAllocator.Allocate(Pool(1_000.0, 2_000.0), PoolMeasure.PeriodEnd,
            new[] { new SeriesClaim("A", 400.0, SeriesPhase.EarlyAmortization) })[0];

        row.PrincipalPct.Should().Be(0.40);
        row.FloatingPct.Should().Be(0.20);
    }

    [Fact]
    public void AFrozenShareComesInFromTheStatement()
    {
        var row = SeriesAllocator.Allocate(Pool(1_000.0, 1_000.0), PoolMeasure.PeriodStart,
            new[] { new SeriesClaim("A", 100.0, SeriesPhase.Accumulating, 0.37) })[0];

        row.PrincipalPct.Should().Be(0.37);
        row.FloatingPct.Should().Be(0.10);
    }

    [Fact]
    public void FrozenSharesOnAFallingPoolNeverTakeMoreThanTheCollections()
    {
        var rows = SeriesAllocator.Allocate(Pool(80.0, 80.0), PoolMeasure.PeriodStart, new[]
        {
            new SeriesClaim("Frozen", 30.0, SeriesPhase.Accumulating, 0.60),
            new SeriesClaim("Revolving", 40.0, SeriesPhase.Revolving)
        });

        rows.Sum(r => r.PrincipalPct).Should().BeApproximately(1.0, 1e-12);
        (rows[0].PrincipalPct / rows[1].PrincipalPct).Should().BeApproximately(0.60 / 0.50, 1e-12);
        rows[0].FrozenPrincipalPct.Should().Be(0.60);
    }

    [Fact]
    public void WhatTheSeriesDoNotTakeBelongsToTheTransferor()
    {
        var rows = SeriesAllocator.Allocate(Pool(1_000.0, 1_000.0), PoolMeasure.PeriodStart,
            new[] { new SeriesClaim("A", 300.0, SeriesPhase.Revolving), new SeriesClaim("B", 200.0, SeriesPhase.Revolving) });

        (1.0 - rows.Sum(r => r.FloatingPct)).Should().BeApproximately(0.50, 1e-12);
    }

    [Fact]
    public void AnEmptyPoolAllocatesNothing()
    {
        var row = SeriesAllocator.Allocate(Pool(0.0, 0.0), PoolMeasure.PeriodStart,
            new[] { new SeriesClaim("A", 0.0, SeriesPhase.Revolving) })[0];

        row.FloatingPct.Should().Be(0.0);
    }
}
