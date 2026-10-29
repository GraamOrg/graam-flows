using System.Reflection;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     The DTO seam for `ocTarget.fullTurboPoolPct`. The API controller and the CLI runner each
///     build the OC target from the SAME DealDto. The controller copied the field and the CLI did
///     not, so a caller on the CLI path had the full turbo silently dropped. Both readers are
///     pinned here, as in <see cref="FirstPeriodCollateralPolicyWiringTests" />.
/// </summary>
public class FullTurboPoolPctWiringTests
{
    private static readonly DateTime FactorDate = new(2024, 1, 1);

    private static IDeal Build(Type owner, DealDto dto)
    {
        var m = owner.GetMethod("BuildDeal", BindingFlags.NonPublic | BindingFlags.Static);
        m.Should().NotBeNull($"{owner.Name}.BuildDeal must exist for this seam to be testable");
        return (IDeal)m!.Invoke(null, new object?[] { dto, FactorDate, null })!;
    }

    private static DealDto Dto(OcTargetDto ocTarget, double? balanceAtIssuance = null) => new()
    {
        DealName = "wiring",
        WaterfallType = "ComposableStructure",
        BalanceAtIssuance = balanceAtIssuance,
        Tranches = new List<TrancheDto>
        {
            new()
            {
                TrancheName = "A", OriginalBalance = 1_000_000, TrancheType = "Offered",
                CashflowType = "PI", CouponType = "Fixed", FixedCoupon = 4.0,
                SubordinationOrder = 1, FirstPayDate = FactorDate.AddMonths(1)
            }
        },
        UnifiedWaterfall = new UnifiedWaterfallDto
        {
            Steps = new List<WaterfallStepDto>
            {
                new() { Type = "INTEREST", Structure = Seq("A") },
                new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A") },
                new() { Type = "WRITEDOWN", Structure = Seq("A") },
                new() { Type = "EXCESS_TURBO", Structure = Seq("A"), OcTarget = ocTarget }
            }
        },
    };

    private static PayableStructureDto Seq(params string[] t) => new() { Type = "SEQ", Tranches = t.ToList() };

    public static IEnumerable<object[]> Readers => new List<object[]>
    {
        new object[] { typeof(WaterfallController) },
        new object[] { typeof(GraamFlows.Cli.Services.WaterfallRunner) },
    };

    [Theory]
    [MemberData(nameof(Readers))]
    public void EveryReaderCarriesTheFullTurboShareAndItsCutoffPool(Type reader)
    {
        var cfg = Build(reader, Dto(new OcTargetDto
        {
            TargetPct = 0.01, FullTurboPoolPct = 0.10, CutoffBalance = 400_000_000
        })).OcTargetConfig;

        cfg!.FullTurboPoolPct.Should().Be(0.10);
        cfg.CutoffPoolBalance.Should().Be(400_000_000);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void EveryReaderFallsBackToTheBalanceAtIssuance(Type reader)
    {
        var cfg = Build(reader, Dto(new OcTargetDto { TargetPct = 0.01, FullTurboPoolPct = 0.10 },
            balanceAtIssuance: 250_000_000)).OcTargetConfig;

        cfg!.CutoffPoolBalance.Should().Be(250_000_000);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void WithoutTheShare_NoReaderSetsACutoffPool(Type reader)
    {
        var cfg = Build(reader, Dto(new OcTargetDto { TargetPct = 0.01, CutoffBalance = 400_000_000 }))
            .OcTargetConfig;

        cfg!.FullTurboPoolPct.Should().BeNull();
        cfg.CutoffPoolBalance.Should().BeNull();
    }
}
