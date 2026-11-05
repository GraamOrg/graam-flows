using System.Runtime.Loader;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     Counts what <see cref="AssemblyLoadContext.Default" /> holds, so no other test may compile
///     rules while one of these is counting.
/// </summary>
[CollectionDefinition(nameof(DefaultLoadContextCounting), DisableParallelization = true)]
public class DefaultLoadContextCounting
{
}

/// <summary>
///     A waterfall request must not leave an assembly behind.
///
///     Every /api/Waterfall request compiles the deal's pay rules, triggers and coupon formulas
///     into a RulesHost assembly with Roslyn. Those assemblies were loaded into
///     <see cref="AssemblyLoadContext.Default" />, which is never collected, so the process grew
///     by one assembly per request until prod had to be restarted. Identical rules now compile
///     once and are reused, so the count is bounded by the number of distinct rule sets, not by
///     the number of requests.
///
///     The test RUNS the endpoint and counts the default context's assemblies; it does not ask
///     the cache what it holds, so it fails on any path that loads a new assembly per request.
/// </summary>
[Collection(nameof(DefaultLoadContextCounting))]
public class RulesAssemblyIsNotLeakedPerRequestTests
{
    private const int Periods = 60;

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

    /// <summary>The repo's own STACR 2025-DNA1 sample: pay rules, triggers and all.</summary>
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

    private static WaterfallResponse Run(DealDto deal)
    {
        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);
        var result = controller.Execute(new WaterfallRequest
        {
            ProjectionDate = new DateTime(2025, 1, 25),
            CollateralCashflows = Tape(),
            Deal = deal,
        });
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull("the waterfall must run: {0}", (result.Result as ObjectResult)?.Value);
        return (WaterfallResponse)ok!.Value!;
    }

    private static int LoadedAssemblies() => AssemblyLoadContext.Default.Assemblies.Count();

    [Fact]
    public void RepeatingTheSameRequestLoadsNoNewAssembly()
    {
        var first = Run(SampleDeal()); // compiles this deal's rules (and warms up the runtime)
        var before = LoadedAssemblies();

        WaterfallResponse last = first;
        for (var i = 0; i < 5; i++)
            last = Run(SampleDeal());

        LoadedAssemblies().Should().Be(before,
            "the default load context is never collected: an assembly loaded per request is a "
            + "leak that grows until the process is restarted");

        // Reusing the compiled rules must not change the answer.
        last.Summary.TranchesSummary.Keys.Should().BeEquivalentTo(first.Summary.TranchesSummary.Keys);
        foreach (var (name, summary) in first.Summary.TranchesSummary)
            last.Summary.TranchesSummary[name].Wal.Should().Be(summary.Wal, "class {0}", name);
    }

    [Fact]
    public void DifferentRulesStillCompileTheirOwnAssembly()
    {
        // The cache is keyed on the generated code, so a deal whose rules differ gets its own
        // RulesHost — sharing one across different rules would run the wrong formulas.
        Run(SampleDeal());
        var before = LoadedAssemblies();

        var renamed = SampleDeal();
        renamed.DealName = renamed.DealName + "_LEAKTEST";
        Run(renamed);
        LoadedAssemblies().Should().Be(before + 1,
            "the deal name is part of every generated rule name, so these rules are new code");

        Run(renamed);
        LoadedAssemblies().Should().Be(before + 1, "and the second run of them reuses it");
    }
}
