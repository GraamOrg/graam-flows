using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using Xunit;

namespace GraamFlows.Tests.Unit.Waterfall;

/// <summary>
/// JSON binding + DTO→domain mapping for the CLO coverage cascade
/// (graam-flows#65). The DTO contract is FIXED — harmony is built against these
/// field names (coverageCascade / level / tranches / ocTriggerPct /
/// icTriggerPct) — so this pins the wire shape. Mirrors
/// ReinvestmentBindingTests: inputs only, no engine wiring.
/// </summary>
public class CoverageCascadeBindingTests
{
    // Must match the options configured in GraamFlows.Api Program.cs.
    private static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private const string WaterfallJson = """
    {
      "steps": [],
      "coverageCascade": [
        { "level": "A/B", "tranches": ["A1", "A2", "B"], "ocTriggerPct": 121.58, "icTriggerPct": 120.0 },
        { "level": "C",   "tranches": ["A1", "A2", "B", "C"], "ocTriggerPct": 112.5 },
        { "level": "D",   "tranches": ["A1", "A2", "B", "C", "D"] }
      ]
    }
    """;

    [Fact]
    public void UnifiedWaterfallDto_BindsCoverageCascadeFromJson()
    {
        var dto = JsonSerializer.Deserialize<UnifiedWaterfallDto>(WaterfallJson, ApiJson)!;

        dto.CoverageCascade.Should().NotBeNull().And.HaveCount(3);

        var ab = dto.CoverageCascade![0];
        ab.Level.Should().Be("A/B");
        ab.Tranches.Should().Equal("A1", "A2", "B");
        ab.OcTriggerPct.Should().Be(121.58);
        ab.IcTriggerPct.Should().Be(120.0);

        dto.CoverageCascade[1].IcTriggerPct.Should().BeNull("no IC test at level C");
        dto.CoverageCascade[2].OcTriggerPct.Should().BeNull("a level may carry no tests");
    }

    [Fact]
    public void Mapper_MapsLevelsInOrder()
    {
        var dto = JsonSerializer.Deserialize<UnifiedWaterfallDto>(WaterfallJson, ApiJson)!;

        var configs = CoverageCascadeMapper.Map(dto.CoverageCascade, "CLO_TEST")!;

        configs.Should().HaveCount(3);
        configs.Select(c => c.Level).Should().Equal("A/B", "C", "D");
        configs[0].Tranches.Should().Equal("A1", "A2", "B");
        configs[0].OcTriggerPct.Should().Be(121.58);
        configs[0].IcTriggerPct.Should().Be(120.0);
        configs[2].OcTriggerPct.Should().BeNull();
    }

    [Fact]
    public void Mapper_NullOrEmpty_MapsToNull()
    {
        CoverageCascadeMapper.Map(null, "CLO_TEST").Should().BeNull();
        CoverageCascadeMapper.Map(new List<CoverageLevelDto>(), "CLO_TEST").Should().BeNull();
    }

    [Fact]
    public void Mapper_RejectsMalformedLevels()
    {
        var noTranches = () => CoverageCascadeMapper.Map(new List<CoverageLevelDto>
        {
            new() { Level = "A/B", Tranches = new List<string>(), OcTriggerPct = 120 }
        }, "CLO_TEST");
        noTranches.Should().Throw<InvalidOperationException>().WithMessage("*A/B*tranche*");

        var noName = () => CoverageCascadeMapper.Map(new List<CoverageLevelDto>
        {
            new() { Level = "", Tranches = new List<string> { "A" }, OcTriggerPct = 120 }
        }, "CLO_TEST");
        noName.Should().Throw<InvalidOperationException>().WithMessage("*level*");

        var duplicate = () => CoverageCascadeMapper.Map(new List<CoverageLevelDto>
        {
            new() { Level = "A/B", Tranches = new List<string> { "A" }, OcTriggerPct = 120 },
            new() { Level = "A/B", Tranches = new List<string> { "A", "B" }, OcTriggerPct = 110 }
        }, "CLO_TEST");
        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*more than once*");

        var badTrigger = () => CoverageCascadeMapper.Map(new List<CoverageLevelDto>
        {
            new() { Level = "A/B", Tranches = new List<string> { "A" }, OcTriggerPct = -5 }
        }, "CLO_TEST");
        badTrigger.Should().Throw<InvalidOperationException>().WithMessage("*positive*");
    }
}
