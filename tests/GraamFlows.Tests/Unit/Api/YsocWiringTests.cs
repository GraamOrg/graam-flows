using System.Reflection;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Objects.DataObjects;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     The DTO seam for <c>ocTarget.ysoc</c>: the API controller and the CLI runner both map it
///     through <see cref="YsocMapper" />, and a schedule that cannot be read is refused rather than
///     run as a deal with no YSOC.
/// </summary>
public class YsocWiringTests
{
    private static readonly DateTime FactorDate = new(2024, 1, 1);

    private static IDeal Build(Type owner, DealDto dto)
    {
        var m = owner.GetMethod("BuildDeal", BindingFlags.NonPublic | BindingFlags.Static);
        m.Should().NotBeNull($"{owner.Name}.BuildDeal must exist for this seam to be testable");
        return (IDeal)m!.Invoke(null, new object?[] { dto, FactorDate, null })!;
    }

    private static TrancheDto Note(string name, int order) => new()
    {
        TrancheName = name, OriginalBalance = 1_000_000, TrancheType = "Offered",
        CashflowType = "PI", CouponType = "Fixed", FixedCoupon = 4.0,
        SubordinationOrder = order, FirstPayDate = FactorDate.AddMonths(1)
    };

    private static DealDto Dto(YsocDto ysoc) => new()
    {
        DealName = "wiring",
        WaterfallType = "ComposableStructure",
        Tranches = new List<TrancheDto> { Note("A1", 1), Note("A2a", 2), Note("A2b", 3) },
        UnifiedWaterfall = new UnifiedWaterfallDto
        {
            Steps = new List<WaterfallStepDto>
            {
                new() { Type = "INTEREST", Structure = Seq("A1", "A2a", "A2b") },
                new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A1", "A2a", "A2b") },
                new() { Type = "WRITEDOWN", Structure = Seq("A1", "A2a", "A2b") },
                new()
                {
                    Type = "EXCESS_TURBO", Structure = Seq("A1", "A2a", "A2b"),
                    OcTarget = new OcTargetDto { FloorAmt = 10_000, Ysoc = ysoc }
                }
            }
        },
    };

    private static PayableStructureDto Seq(params string[] t) => new() { Type = "SEQ", Tranches = t.ToList() };

    private static YsocDto Schedule(params (string Month, double Amount)[] rows) => new()
    {
        Schedule = rows.Select(r => new YsocAmountDto { Month = r.Month, Amount = r.Amount }).ToList()
    };

    public static IEnumerable<object[]> Readers => new List<object[]>
    {
        new object[] { typeof(WaterfallController) },
        new object[] { typeof(GraamFlows.Cli.Services.WaterfallRunner) },
    };

    [Theory]
    [MemberData(nameof(Readers))]
    public void EveryReaderCarriesTheScheduleAndItsSwitch(Type reader)
    {
        var dto = Schedule(("2024-02", 900_000), ("2024-03", 800_000));
        dto.AfterPaidInFull = new YsocSwitchDto
        {
            Classes = new List<string> { "A2a", "A2b" },
            Schedule = new List<YsocAmountDto> { new() { Month = "2024-03", Amount = 500_000 } }
        };

        var ysoc = Build(reader, Dto(dto)).OcTargetConfig!.Ysoc!;

        ysoc.Amounts[(2024, 2)].Should().Be(900_000);
        ysoc.Amounts[(2024, 3)].Should().Be(800_000);
        ysoc.After!.Classes.Should().BeEquivalentTo(new[] { "A2a", "A2b" });
        ysoc.After.Amounts[(2024, 3)].Should().Be(500_000);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void WithoutAScheduleNoReaderSetsOne(Type reader)
    {
        Build(reader, Dto(null!)).OcTargetConfig!.Ysoc.Should().BeNull();
    }

    [Theory]
    [InlineData("2024-2", 1.0, "is written YYYY-MM")]
    [InlineData("February 2024", 1.0, "is written YYYY-MM")]
    [InlineData("2024-02", -1.0, "zero or more")]
    public void AScheduleThatCannotBeReadIsRefused(string month, double amount, string says)
    {
        var act = () => YsocMapper.Map(Schedule((month, amount)), new[] { "A1" }, "d");
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{says}*");
    }

    [Fact]
    public void AMonthStatedTwiceIsRefused()
    {
        var act = () => YsocMapper.Map(Schedule(("2024-02", 2), ("2024-02", 1)), new[] { "A1" }, "d");
        act.Should().Throw<InvalidOperationException>().WithMessage("*twice*");
    }

    [Fact]
    public void AnEmptyScheduleIsRefused()
    {
        var act = () => YsocMapper.Map(new YsocDto(), new[] { "A1" }, "d");
        act.Should().Throw<InvalidOperationException>().WithMessage("*empty schedule*");
    }

    [Fact]
    public void ASwitchOnAClassTheDealDoesNotHaveIsRefused()
    {
        var dto = Schedule(("2024-02", 2));
        dto.AfterPaidInFull = new YsocSwitchDto
        {
            Classes = new List<string> { "A2" },
            Schedule = new List<YsocAmountDto> { new() { Month = "2024-02", Amount = 1 } }
        };
        var act = () => YsocMapper.Map(dto, new[] { "A1", "A2a", "A2b" }, "d");
        act.Should().Throw<InvalidOperationException>().WithMessage("*A2, which the deal does not have*");
    }
}
