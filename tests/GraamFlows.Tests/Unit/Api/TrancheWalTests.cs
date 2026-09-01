using FluentAssertions;
using GraamFlows.Api.Models;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     The WAL the waterfall response reports, and the settle date it is measured from.
///
///     This endpoint returned no WAL, so every caller computed its own. harmony grew two that
///     disagreed — one anchored at settle, one at `period / 12` from the projection start,
///     which overstated every WAL by the cutoff-to-closing gap — then consolidated them into a
///     third that approximates ActualActual-ISDA as actual/365.25. Returning the engine's own
///     number is what stops the fourth, and it only means anything if the caller's SETTLE date
///     actually arrives: `settleDate` had been on the wire for some time with no property
///     bound to it, silently dropped by System.Text.Json.
/// </summary>
public class TrancheWalTests
{
    private static readonly DateTime Settle = new(2025, 4, 14);

    /// <summary>A two-year amortiser: half the principal at 1yr, half at 2yr.</summary>
    private static List<TrancheCashflowDto> Level() => new()
    {
        new TrancheCashflowDto
        {
            CashflowDate = new DateTime(2026, 4, 14), BeginBalance = 100, Balance = 50,
            ScheduledPrincipal = 50, Interest = 5,
        },
        new TrancheCashflowDto
        {
            CashflowDate = new DateTime(2027, 4, 14), BeginBalance = 50, Balance = 0,
            ScheduledPrincipal = 50, Interest = 2.5,
        },
    };

    [Fact]
    public void WalIsPrincipalWeightedFromSettle()
    {
        var (wal, _) = TrancheWal.Compute(Level(), Settle, isIo: false);
        // (50*1 + 50*2) / 100 = 1.5
        wal.Should().BeApproximately(1.5, 0.01);
    }

    [Fact]
    public void CashflowsBeforeSettleAreDropped()
    {
        // The engine filters `CashflowDate >= SettleDate`. Settling a year later must drop the
        // first payment entirely — not merely re-weight it — so the answer becomes 1.0, the
        // remaining flow's own distance. This is the assertion that would have caught the
        // dropped `settleDate` field: with it ignored, both settle dates give the same number.
        var (early, _) = TrancheWal.Compute(Level(), Settle, isIo: false);
        var (late, _) = TrancheWal.Compute(Level(), new DateTime(2026, 4, 15), isIo: false);
        early.Should().BeApproximately(1.5, 0.01);
        late.Should().BeApproximately(1.0, 0.01);
        late.Should().NotBe(early);
    }

    [Fact]
    public void AnIoStreamIsWeightedByItsNotionalNotItsPrincipal()
    {
        // THE case the harmony implementations cannot express. A notional strip receives no
        // principal, so a principal-weighted WAL has nothing to weight and returns nothing at
        // all — which is why STACR 2025-DNA1's M-2I / M-2AI / M-2BI could not be scored
        // against their published WALs. Weighting the balance change gives the notional's life.
        var io = new List<TrancheCashflowDto>
        {
            new()
            {
                CashflowDate = new DateTime(2026, 4, 14), BeginBalance = 100, Balance = 50,
                ScheduledPrincipal = 0, UnscheduledPrincipal = 0, Interest = 5,
            },
            new()
            {
                CashflowDate = new DateTime(2027, 4, 14), BeginBalance = 50, Balance = 0,
                ScheduledPrincipal = 0, UnscheduledPrincipal = 0, Interest = 2.5,
            },
        };

        var (principalWeighted, _) = TrancheWal.Compute(io, Settle, isIo: false);
        var (notionalWeighted, _) = TrancheWal.Compute(io, Settle, isIo: true);

        principalWeighted.Should().Be(0, "there is no principal to weight");
        notionalWeighted.Should().BeApproximately(1.5, 0.01);
    }

    [Fact]
    public void AnEmptyStreamIsZeroNotAThrow()
    {
        // A response is built for expense and certificate rows that may carry no cashflows at
        // all; the summary must still render.
        TrancheWal.Compute(new List<TrancheCashflowDto>(), Settle, isIo: false)
            .Should().Be((0d, 0d));
    }

    [Fact]
    public void BalanceWalUsesADifferentDayCountSoItIsNotTheSameNumber()
    {
        // `BalanceWeightedAverageLife` is Thirty360Us where `WeightedAverageLife` is
        // ActualActual-ISDA. They are two conventions, not one value under two names, and a
        // caller picking between them should see they differ.
        var oddDates = new List<TrancheCashflowDto>
        {
            new()
            {
                CashflowDate = new DateTime(2026, 2, 28), BeginBalance = 100, Balance = 0,
                ScheduledPrincipal = 100, Interest = 5,
            },
        };
        var (wal, balanceWal) = TrancheWal.Compute(oddDates, Settle, isIo: false);
        wal.Should().BePositive();
        balanceWal.Should().BePositive();
        wal.Should().NotBe(balanceWal);
    }
}

/// <summary>
///     The WIRING, not the arithmetic. `TrancheSummaryDto` is built at FOUR sites — two in
///     `WaterfallController` (dynamic tranches, then class-only tranches) and two in the CLI's
///     `WaterfallRunner` — and a field only some of them populate is worse than one none of
///     them do, because the response looks complete. That is precisely how `reserveConfig`
///     became CLI-only and `FirstPeriodCollateralPolicy` became API-only.
///
///     Source-level rather than behavioural because reaching the class-only branch needs a
///     deal with certificate or expense rows and a full waterfall run; this catches the
///     omission that actually happens, which is a site added or edited without the field.
/// </summary>
public class TrancheWalWiringTests
{
    /// <summary>
    ///     The file's CODE lines, comments stripped.
    ///
    ///     Not cosmetic. The first version of this read the whole file, and the anchor
    ///     assertion below passed with the anchor DELETED — because the same expression also
    ///     appears in a doc comment two methods away. A source-text assertion pins whatever
    ///     text is there, including prose describing code that no longer exists.
    /// </summary>
    private static string ReadCode(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GraamFlows.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must be able to find the repo root");
        var lines = File.ReadAllLines(Path.Combine(dir!.FullName, relative))
            .Where(l =>
            {
                var t = l.TrimStart();
                return !t.StartsWith("//") && !t.StartsWith("///") && !t.StartsWith("*");
            });
        return string.Join("\n", lines);
    }

    [Theory]
    [InlineData("src/GraamFlows.Api/Controllers/WaterfallController.cs", 2)]
    [InlineData("src/GraamFlows.Cli/Services/WaterfallRunner.cs", 2)]
    public void EveryTrancheSummarySiteComputesTheWal(string path, int sites)
    {
        var src = ReadCode(path);
        var built = src.Split("new TrancheSummaryDto").Length - 1;
        var computed = src.Split("TrancheWal.Compute").Length - 1;

        built.Should().Be(sites, $"{path} is expected to build {sites} tranche summaries");
        computed.Should().Be(built,
            $"{path} builds {built} TrancheSummaryDto but calls TrancheWal.Compute {computed} " +
            "times — a summary without a WAL reports 0.0, which reads as a real answer");
    }

    [Fact]
    public void TheApiAnchorsOnTheRequestSettleDateAndFallsBackToProjection()
    {
        // `settleDate` was on the wire with no property bound to it, so it was silently
        // dropped. The fallback keeps a request that omits it byte-for-byte unchanged.
        var src = ReadCode("src/GraamFlows.Api/Controllers/WaterfallController.cs");
        src.Should().Contain("request.SettleDate ?? request.ProjectionDate");
    }
}
