using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     The caller's settle date reaches the response, proved by RUNNING the endpoint.
///
///     `TrancheWalTests` asserts the arithmetic by calling `TrancheWal.Compute` directly, and
///     the wiring by reading source text. Neither runs the controller, so with the anchor at
///     `WaterfallController` deleted, 372 of 373 tests still passed — the single failure was a
///     source assertion, and a source assertion pins whatever text is there. Its first version
///     passed with the anchor deleted because the same expression survived in a doc comment;
///     stripping `//` lines fixed that and left `/* */` blocks, which pass just as well.
///
///     A behavioural test has no such hole. `settleDate` changes which cashflows are in scope
///     (`WeightedAverageLife` drops everything before it), so a settle date that arrives MUST
///     move the number, and one that is dropped cannot.
/// </summary>
public class SettleDateReachesTheResponseTests
{
    private const int Periods = 60;

    /// <summary>A funded class of the sample deal; any real one proves the anchor.</summary>
    private const string Klass = "A1";

    /// <summary>A plain monthly amortiser: one group, level principal, no triggers.</summary>
    private static List<PeriodCashflowDto> Tape()
    {
        var rows = new List<PeriodCashflowDto>();
        double bal = 120_000_000;
        var d = new DateTime(2025, 1, 25);
        for (var p = 0; p < Periods; p++)
        {
            var sched = 2_000_000d;
            rows.Add(new PeriodCashflowDto
            {
                Period = p,
                CashflowDate = d.AddMonths(p),
                GroupNum = "1",
                BeginBalance = bal,
                Balance = bal - sched,
                ScheduledPrincipal = sched,
                Interest = bal * 0.05 / 12,
                NetInterest = bal * 0.05 / 12,
                Wac = 5.0,
                Wam = 360 - p,
            });
            bal -= sched;
        }
        return rows;
    }

    /// <summary>
    ///     The repo's own STACR 2025-DNA1 sample. A hand-built deal is not worth the fight
    ///     here — ComposableStructure requires a valid payable set, and this test is about the
    ///     settle date, not about authoring a waterfall.
    /// </summary>
    private static DealDto SampleDeal()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GraamFlows.sln")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must be able to find the repo root");
        var json = File.ReadAllText(Path.Combine(
            dir!.FullName, "src/GraamFlows.Api/Samples/stacr25dna1_unified.json"));
        var deal = System.Text.Json.JsonSerializer.Deserialize<DealDto>(json,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });
        deal.Should().NotBeNull();
        return deal!;
    }

    private static WaterfallRequest Request(DateTime? settle) => new()
    {
        ProjectionDate = new DateTime(2025, 1, 25),
        SettleDate = settle,
        CollateralCashflows = Tape(),
        Deal = SampleDeal(),
    };

    private static Dictionary<string, TrancheSummaryDto> Run(DateTime? settle)
    {
        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);
        var result = controller.Execute(Request(settle));
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull("the waterfall must run: {0}",
            (result.Result as ObjectResult)?.Value);
        return ((WaterfallResponse)ok!.Value!).Summary.TranchesSummary;
    }

    [Fact]
    public void ASettleDateTheCallerStatesChangesTheReportedWal()
    {
        // Two years in drops the first ~24 payments from the WAL entirely. If the field is
        // dropped on the wire — no property bound, or the controller ignoring it — both runs
        // anchor on ProjectionDate and produce the SAME number, and this fails.
        var atProjection = Run(null)[Klass].Wal;
        var twoYearsLater = Run(new DateTime(2027, 1, 25))[Klass].Wal;

        atProjection.Should().NotBeNull().And.BeGreaterThan(0);
        twoYearsLater.Should().NotBeNull().And.BeGreaterThan(0);
        twoYearsLater.Should().NotBe(atProjection,
            "a settle date the caller states must reach WeightedAverageLife, which drops "
            + "cashflows before it — identical numbers mean the field never arrived");
        twoYearsLater!.Value.Should().BeLessThan(atProjection!.Value,
            "settling later leaves only the remaining flows, which are nearer to it");
    }

    [Fact]
    public void OmittingItAnchorsOnTheProjectionDate()
    {
        // The backward-compatibility half: a request that says nothing must behave exactly as
        // it did before this field existed, which is anchored at ProjectionDate.
        Run(null)[Klass].Wal.Should()
            .Be(Run(new DateTime(2025, 1, 25))[Klass].Wal,
                "an omitted settle date falls back to ProjectionDate");
    }
}
