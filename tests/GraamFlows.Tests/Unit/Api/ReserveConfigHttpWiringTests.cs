using System.Reflection;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     HTTP wiring for a reserve account's target. `TrancheDto.ReserveConfig` binds on the
///     controller path and only the CLI copied it, so /api/Waterfall ran every reserve on the
///     no-config default: target = the funded balance, always capped at the notes. The request
///     looked obeyed, because a reserve funded at its target and capped at the notes comes out
///     the same either way. These post through `WaterfallController.Execute` and read the
///     reserve's own balance, on the three shapes where the default and the config disagree.
/// </summary>
public class ReserveConfigHttpWiringTests
{
    private static readonly DateTime Proj = new(2026, 6, 1);
    private const double Pool = 1_000_000;

    private static WaterfallRequest Request(double funded, ReserveAccountConfigDto? config,
        double? balanceAtIssuance = Pool)
    {
        var deal = new DealDto
        {
            DealName = "HTTP_RESERVE_TEST",
            WaterfallType = "Sequential",
            BalanceAtIssuance = balanceAtIssuance,
            Tranches = new List<TrancheDto>
            {
                new()
                {
                    TrancheName = "A", OriginalBalance = Pool, TrancheType = "Offered",
                    CashflowType = "PI", CouponType = "Fixed", FixedCoupon = 4.0,
                    SubordinationOrder = 1, FirstPayDate = Proj.AddMonths(1)
                },
                new()
                {
                    TrancheName = "RESERVE", OriginalBalance = funded, TrancheType = "CapFundsReserve",
                    CashflowType = "Reserve", CouponType = "None", SubordinationOrder = 2,
                    FirstPayDate = Proj.AddMonths(1), ReserveConfig = config
                },
                new()
                {
                    TrancheName = "CERT", OriginalBalance = 0, TrancheType = "Certificate",
                    CashflowType = "PI", CouponType = "None", SubordinationOrder = 3,
                    FirstPayDate = Proj.AddMonths(1)
                }
            },
            UnifiedWaterfall = new UnifiedWaterfallDto
            {
                ExecutionOrder = new List<string>
                {
                    "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED",
                    "PRINCIPAL_RECOVERY", "WRITEDOWN", "RESERVE_DEPOSIT", "EXCESS_RELEASE"
                },
                Steps = new List<WaterfallStepDto>
                {
                    new() { Type = "INTEREST", Structure = Seq("A") },
                    new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A") },
                    new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A") },
                    new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A") },
                    new() { Type = "WRITEDOWN", Structure = Seq("A") },
                    new() { Type = "RESERVE_DEPOSIT" },
                    new() { Type = "EXCESS_RELEASE", Structure = Seq("CERT") }
                }
            }
        };

        // Straight-line amortization over 100 months at a 5% pool rate over a 4% note: about
        // 830 of excess in the first month, so a deposit is visible well before the pool is gone.
        var pool = new List<PeriodCashflowDto>();
        for (var p = 0; p < 100; p++)
        {
            var begin = Pool - 10_000.0 * p;
            pool.Add(new PeriodCashflowDto
            {
                CashflowDate = Proj.AddMonths(p),
                GroupNum = "1",
                BeginBalance = begin,
                Balance = begin - 10_000.0,
                ScheduledPrincipal = 10_000.0,
                UnscheduledPrincipal = 0,
                Interest = begin * 0.05 / 12.0,
                NetInterest = begin * 0.05 / 12.0
            });
        }

        return new WaterfallRequest { Deal = deal, CollateralCashflows = pool, ProjectionDate = Proj };
    }

    private static PayableStructureDto Seq(params string[] tranches) => new()
    {
        Type = "SEQ",
        Tranches = tranches.ToList()
    };

    private static List<double> ReserveBalances(WaterfallRequest request)
    {
        var controller = new WaterfallController(NullLogger<WaterfallController>.Instance);
        var action = controller.Execute(request);
        if (action.Result is BadRequestObjectResult bad)
            throw new Xunit.Sdk.XunitException($"Waterfall 400: {bad.Value}");
        var ok = action.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<WaterfallResponse>().Subject;
        return response.TrancheCashflows["RESERVE"].Select(c => c.Balance).ToList();
    }

    [Fact]
    public void A_reserve_funded_below_its_target_builds_to_the_target()
    {
        var balances = ReserveBalances(Request(5_000, new ReserveAccountConfigDto
        {
            TargetPct = 0.01, TargetBase = "CutoffPoolBalance", CutoffPoolBalance = Pool,
            CapAtNoteBalance = true
        }));

        balances.Max().Should().BeApproximately(10_000, 0.01,
            "1% of the cutoff pool is the target, not the 0.5% funded at closing");
    }

    [Fact]
    public void An_uncapped_reserve_holds_its_target_until_the_notes_retire()
    {
        var balances = ReserveBalances(Request(20_000, new ReserveAccountConfigDto
        {
            TargetPct = 0.02, TargetBase = "CutoffPoolBalance", CutoffPoolBalance = Pool,
            CapAtNoteBalance = false
        }));

        // 10,000 of notes are left after the 99th period; a cap would have the reserve there.
        balances[98].Should().BeApproximately(20_000, 0.01,
            "CapAtNoteBalance false is a stated term, not the default");
        balances[^1].Should().BeApproximately(0, 0.01,
            "with no notes left the cash is the residual's, not the account's");
    }

    [Fact]
    public void A_target_on_the_current_pool_steps_down_with_the_pool()
    {
        var balances = ReserveBalances(Request(10_000, new ReserveAccountConfigDto
        {
            TargetPct = 0.01, TargetBase = "CurrentPoolBalance", CapAtNoteBalance = true
        }));

        balances[49].Should().BeApproximately(0.01 * (Pool - 10_000.0 * 50), 0.01,
            "1% of the pool at the end of the 50th period");
    }

    [Fact]
    public void A_cutoff_balance_left_out_falls_back_to_the_balance_at_issuance()
    {
        var balances = ReserveBalances(Request(5_000, new ReserveAccountConfigDto
        {
            TargetPct = 0.01, TargetBase = "CutoffPoolBalance"
        }));

        balances.Max().Should().BeApproximately(10_000, 0.01);
    }

    [Fact]
    public void PoolBalance_is_the_current_pool()
    {
        var balances = ReserveBalances(Request(10_000, new ReserveAccountConfigDto
        {
            TargetPct = 0.01, TargetBase = "PoolBalance", CutoffPoolBalance = Pool
        }));

        balances[49].Should().BeApproximately(0.01 * (Pool - 10_000.0 * 50), 0.01);
    }

    [Fact]
    public void A_reserve_with_no_config_keeps_the_funded_balance_as_its_target()
    {
        var balances = ReserveBalances(Request(5_000, null));

        balances.Max().Should().BeApproximately(5_000, 0.01,
            "no stated target means the closing deposit is the target, as it always was");
    }

    public static IEnumerable<object?[]> TargetsThatStateNothing => new List<object?[]>
    {
        new object?[] { new ReserveAccountConfigDto { TargetBase = "CutoffPoolBalance", CutoffPoolBalance = Pool }, Pool },
        new object?[] { new ReserveAccountConfigDto { TargetPct = 0.01, TargetBase = "CutoffPoolBalance", CutoffPoolBalance = 0 }, null },
        new object?[] { new ReserveAccountConfigDto { TargetPct = 0.01 }, null },
    };

    [Theory]
    [MemberData(nameof(TargetsThatStateNothing))]
    public void A_target_with_no_percentage_or_no_base_does_not_empty_the_account(
        ReserveAccountConfigDto config, double? balanceAtIssuance)
    {
        var balances = ReserveBalances(Request(10_000, config, balanceAtIssuance));

        balances[50].Should().BeApproximately(10_000, 0.01,
            "an unstated target is the funded balance, never zero");
    }

    public static IEnumerable<object[]> Readers => new List<object[]>
    {
        new object[] { typeof(WaterfallController) },
        new object[] { typeof(GraamFlows.Cli.Services.WaterfallRunner) },
    };

    [Theory]
    [MemberData(nameof(Readers))]
    public void EveryReaderCarriesTheStatedConfig(Type reader)
    {
        var m = reader.GetMethod("BuildDeal", BindingFlags.NonPublic | BindingFlags.Static);
        m.Should().NotBeNull($"{reader.Name}.BuildDeal must exist for this seam to be testable");

        var request = Request(5_000, new ReserveAccountConfigDto
        {
            TargetPct = 0.02, TargetBase = "CurrentPoolBalance", CutoffPoolBalance = 123_000,
            CapAtNoteBalance = false
        });
        var deal = (IDeal)m!.Invoke(null, new object?[] { request.Deal, Proj, null })!;

        deal.Tranches.Single(t => t.TrancheName == "RESERVE").ReserveConfig.Should().Be(
            new ReserveAccountConfig
            {
                TargetPct = 0.02, TargetBase = "CurrentPoolBalance", CutoffPoolBalance = 123_000,
                CapAtNoteBalance = false
            });
    }
}
