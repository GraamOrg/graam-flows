using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Json;
using GraamFlows.Api.Models;
using GraamFlows.Cli.Commands;
using GraamFlows.Cli.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Cli;

/// <summary>
///     A run file is the requests a caller posted to the API, replayed by the CLI. These pin that
///     the replay IS the API: the same body through the CLI and through the two controllers gives
///     the same WALs, keys are normalized as the API's middleware normalizes them, and what a file
///     says about the collateral rows between the two calls is applied, not ignored.
/// </summary>
public class RunFileTests
{
    private const string Projection = "2026-06-01";

    private static JsonObject Asset(string id, double balance, double rate, int term, bool snakeCase = false)
    {
        var o = new JsonObject
        {
            ["assetId"] = id, ["assetName"] = id, ["interestRateType"] = "FRM", ["originalDate"] = Projection,
            ["originalBalance"] = balance, ["originalInterestRate"] = rate, ["currentInterestRate"] = rate,
            ["originalAmortizationTerm"] = term, ["groupNum"] = "1"
        };
        o[snakeCase ? "current_balance" : "currentBalance"] = balance;
        return o;
    }

    private static JsonObject Deal()
    {
        var deal = new DealDto
        {
            DealName = "RUN_FILE_TEST",
            WaterfallType = "Sequential",
            Tranches = new List<TrancheDto>
            {
                new()
                {
                    TrancheName = "A", OriginalBalance = 700_000, TrancheType = "Offered", CashflowType = "PI",
                    CouponType = "Fixed", FixedCoupon = 4.0, SubordinationOrder = 1,
                    FirstPayDate = DateTime.Parse(Projection).AddMonths(1)
                },
                new()
                {
                    TrancheName = "B", OriginalBalance = 300_000, TrancheType = "Offered", CashflowType = "PI",
                    CouponType = "Fixed", FixedCoupon = 5.0, SubordinationOrder = 2,
                    FirstPayDate = DateTime.Parse(Projection).AddMonths(1)
                }
            },
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = ["INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN"],
                Steps =
                [
                    new() { Type = "INTEREST", Structure = Seq("A", "B") },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A", "B") },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A", "B") },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A", "B") },
                    new() { Type = "WRITEDOWN", Structure = Seq("B", "A") }
                ]
            }
        };
        return JsonSerializer.SerializeToNode(deal, ApiJson.Options)!.AsObject();
    }

    private static PayableStructureDto Seq(params string[] tranches) => new() { Type = "SEQ", Tranches = tranches.ToList() };

    private static JsonObject Assumptions(double cpr) => new()
    {
        ["cpr"] = cpr, ["cdr"] = 0.0, ["severity"] = 0.0, ["delinquency"] = 0.0, ["advancing"] = 0.0
    };

    private static RunFile File(bool snakeCaseAsset = false) => RunFile.Parse(new JsonObject
    {
        ["graamflowsRun"] = 1,
        ["dealName"] = "RUN_FILE_TEST",
        ["collateralDate"] = Projection,
        ["calcCollateral"] = new JsonObject
        {
            ["assets"] = new JsonArray(Asset("1", 600_000, 6.0, 120), Asset("2", 400_000, 5.5, 60, snakeCaseAsset)),
            ["projectionDate"] = Projection,
            ["settleDate"] = Projection
        },
        ["waterfall"] = new JsonObject
        {
            ["deal"] = Deal(), ["projectionDate"] = Projection, ["settleDate"] = Projection
        },
        ["walTolerance"] = 0.15,
        ["scenarios"] = new JsonArray(
            new JsonObject { ["name"] = "0 CPR", ["speed"] = 0.0, ["calcCollateral"] = new JsonObject { ["assumptions"] = Assumptions(0) } },
            new JsonObject { ["name"] = "20 CPR", ["speed"] = 20.0, ["calcCollateral"] = new JsonObject { ["assumptions"] = Assumptions(20) } })
    }.ToJsonString());

    /// <summary>The scenario run straight through the two controllers, as an HTTP caller would.</summary>
    private static WaterfallResponse ThroughControllers(RunFile file, RunFileScenario scenario)
    {
        var calc = JsonSerializer.Deserialize<CalcCollateralRequest>(
            ApiJson.NormalizeKeys(RunFile.Merge(file.CalcCollateral, scenario.CalcCollateral).ToJsonString()),
            ApiJson.Options)!;
        var collateral = (CalcCollateralResponse)((OkObjectResult)new CalcCollateralController(
            NullLogger<CalcCollateralController>.Instance).Calculate(calc).Result!).Value!;
        var body = RunFile.Merge(file.Waterfall, scenario.Waterfall);
        body["collateralCashflows"] = JsonSerializer.SerializeToNode(collateral.Cashflows, ApiJson.Options);
        var wf = JsonSerializer.Deserialize<WaterfallRequest>(body.ToJsonString(), ApiJson.Options)!;
        return (WaterfallResponse)((OkObjectResult)new WaterfallController(
            NullLogger<WaterfallController>.Instance).Execute(wf).Result!).Value!;
    }

    [Fact]
    public void A_run_file_is_told_from_a_deal_model()
    {
        RunFile.IsRunFile("{\"graamflowsRun\": 1}").Should().BeTrue();
        RunFile.IsRunFile("{\"deal\": {\"dealName\": \"X\"}}").Should().BeFalse();
        RunFile.IsRunFile("not json").Should().BeFalse();
    }

    [Fact]
    public void An_unknown_version_is_refused_not_guessed()
    {
        var act = () => RunFile.Parse("{\"graamflowsRun\": 2, \"scenarios\": [{\"name\": \"x\"}]}");
        act.Should().Throw<InvalidOperationException>().WithMessage("*version 2*");
    }

    [Fact]
    public void A_scenario_replaces_shared_top_level_keys_and_leaves_the_shared_body_alone()
    {
        var file = File();
        var merged = RunFile.Merge(file.CalcCollateral, file.Scenarios[1].CalcCollateral);
        merged["assumptions"]!["cpr"]!.GetValue<double>().Should().Be(20);
        merged["assets"]!.AsArray().Count.Should().Be(2);
        file.CalcCollateral.ContainsKey("assumptions").Should().BeFalse();
    }

    [Fact]
    public void The_cli_replay_gives_the_wals_the_controllers_give()
    {
        var file = File();
        foreach (var scenario in file.Scenarios)
        {
            var cli = new RunFileRunner().Run(file, scenario);
            var direct = ThroughControllers(file, scenario);
            foreach (var tranche in new[] { "A", "B" })
            {
                cli.Wal(tranche, file.WalField).Should().NotBeNull();
                cli.Wal(tranche, file.WalField).Should().Be(direct.Summary.TranchesSummary[tranche].BalanceWal);
            }
        }

        // And the speed is applied: 20 CPR shortens the senior class.
        var slow = new RunFileRunner().Run(file, file.Scenarios[0]).Wal("A", null);
        var fast = new RunFileRunner().Run(file, file.Scenarios[1]).Wal("A", null);
        fast.Should().BeLessThan(slow!.Value);
    }

    [Fact]
    public void Snake_case_keys_bind_as_the_api_binds_them()
    {
        // The API rewrites snake_case keys before binding; a CLI that did not would read this asset's
        // balance as 0 and answer differently from the API on the same body.
        var camel = new RunFileRunner().Run(File(), File().Scenarios[0]);
        var snake = new RunFileRunner().Run(File(snakeCaseAsset: true), File(snakeCaseAsset: true).Scenarios[0]);
        snake.Wal("A", null).Should().Be(camel.Wal("A", null));
        snake.Collateral!.Cashflows[0].BeginBalance.Should().BeApproximately(1_000_000, 0.01);
    }

    [Fact]
    public void A_collateral_adjustment_reaches_the_waterfall()
    {
        var file = File();
        var plain = new RunFileRunner().Run(file, file.Scenarios[0]);
        file.Scenarios[0].CollateralAdjustments =
        [
            new CollateralAdjustment { Row = 0, Add = new Dictionary<string, double> { ["scheduledPrincipal"] = 50_000 } }
        ];
        var adjusted = new RunFileRunner().Run(file, file.Scenarios[0]);

        // 50,000 more principal in the first period pays the senior class down sooner.
        adjusted.Wal("A", null).Should().BeLessThan(plain.Wal("A", null)!.Value);
    }

    [Fact]
    public void An_adjustment_to_a_row_the_engine_did_not_return_is_refused()
    {
        var file = File();
        file.Scenarios[0].CollateralAdjustments =
            [new CollateralAdjustment { Row = 9_999, Add = new Dictionary<string, double> { ["interest"] = 1 } }];
        var act = () => new RunFileRunner().Run(file, file.Scenarios[0]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*row 9999*");
    }

    [Fact]
    public void Collateral_rows_carried_in_the_file_are_distributed_as_given()
    {
        var file = File();
        var first = new RunFileRunner().Run(file, file.Scenarios[1]);
        file.Scenarios[0].CollateralCashflows =
            JsonSerializer.SerializeToNode(first.Collateral!.Cashflows, ApiJson.Options)!.AsArray();
        var replay = new RunFileRunner().Run(file, file.Scenarios[0]);

        replay.Collateral.Should().BeNull("no collateral request is run when the rows are given");
        replay.Wal("A", null).Should().Be(first.Wal("A", null));
    }

    [Fact]
    public void Wal_tests_scores_the_published_cells_and_the_reference_run()
    {
        var file = File();
        var a0 = new RunFileRunner().Run(file, file.Scenarios[0]).Wal("A", null)!.Value;
        file.Scenarios[0].ExpectedWal = new Dictionary<string, double> { ["A"] = a0 + 0.1, ["B"] = 99 };
        file.Scenarios[0].ReferenceWal = new Dictionary<string, double> { ["A"] = a0 };
        file.Scenarios[1].ReferenceWal = new Dictionary<string, double> { ["A"] = 0.5 };

        var (published, reference) = RunFileCommands.Score(file, file.WalTolerance!.Value);

        published.Single(c => c.Tranche == "A").Passed.Should().BeTrue("0.1 is inside the file's 0.15 tolerance");
        published.Single(c => c.Tranche == "B").Passed.Should().BeFalse();
        reference.Single(c => c.Scenario == "0 CPR").Passed.Should().BeTrue();
        reference.Single(c => c.Scenario == "20 CPR").Passed.Should().BeFalse();
    }

    [Fact]
    public void A_command_line_speed_replaces_the_scenarios_and_its_vector()
    {
        var body = new JsonObject { ["assumptions"] = new JsonObject { ["cpr"] = 10.0, ["cprVector"] = new JsonArray(1.0, 2.0) } };
        RunFileCommands.Override(body, 30, null, null, null, "s");
        body["assumptions"]!["cpr"]!.GetValue<double>().Should().Be(30);
        body["assumptions"]!.AsObject().ContainsKey("cprVector").Should().BeFalse();
    }

    [Fact]
    public void A_command_line_speed_is_refused_over_per_asset_speeds()
    {
        var body = new JsonObject
        {
            ["assumptions"] = new JsonObject { ["cpr"] = 10.0 },
            ["assetAssumptions"] = new JsonObject { ["7"] = new JsonObject { ["cprVector"] = new JsonArray(0.0, 10.0) } }
        };
        var act = () => RunFileCommands.Override(body, 30, null, null, null, "s");
        act.Should().Throw<InvalidOperationException>().WithMessage("*per asset*");
    }

    [Fact]
    public void A_stated_rounding_of_the_difference_is_how_a_boundary_cell_is_scored()
    {
        // Scored as round(model - published, 3) <= 0.15, a model 0.1505 years off ties; compared
        // unrounded it misses. The same model numbers, two answers: the file says which.
        var file = File();
        var a0 = new RunFileRunner().Run(file, file.Scenarios[0]).Wal("A", null)!.Value;
        file.Scenarios[0].ExpectedWal = new Dictionary<string, double> { ["A"] = a0 - 0.1504 };
        file.Scenarios = [file.Scenarios[0]];

        RunFileCommands.Score(file, 0.15).Published.Single().Passed.Should().BeFalse();
        file.WalDiffDecimals = 3;
        RunFileCommands.Score(file, 0.15).Published.Single().Passed.Should().BeTrue();
    }
}
