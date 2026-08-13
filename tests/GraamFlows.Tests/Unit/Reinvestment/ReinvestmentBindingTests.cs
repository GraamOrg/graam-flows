using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using GraamFlows.Objects.TypeEnum;
using Xunit;

namespace GraamFlows.Tests.Unit.Reinvestment;

/// <summary>
/// JSON binding + DTO→domain mapping for the reinvestment config
/// (graam-flows#48). Mirrors the API's System.Text.Json options (camelCase,
/// case-insensitive, string enums) and exercises ReinvestmentConfigMapper —
/// inputs only, no engine wiring.
/// </summary>
public class ReinvestmentBindingTests
{
    // Must match the options configured in GraamFlows.Api Program.cs.
    private static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private const string DealJson = """
    {
      "dealName": "REINVEST_TEST",
      "reinvestment": {
        "reinvestEndDate": "2029-06-01",
        "reinvestStartDate": "2026-06-01",
        "target": 1000000,
        "holdback": 0.05,
        "reinvestRecoveries": true,
        "templates": [
          {
            "allocationPct": 70,
            "price": 99.5,
            "interestRateType": "FRM",
            "amortizationType": "Bullet",
            "couponRate": 6.25,
            "termMonths": 60,
            "serviceFee": 0.25
          },
          {
            "allocationPct": 30,
            "isSynthetic": true,
            "price": 95,
            "amortizationType": "Bullet",
            "indexName": "Sofr30Avg",
            "indexMargin": 2.5,
            "termMonths": 72
          }
        ]
      }
    }
    """;

    [Fact]
    public void DealDto_BindsReinvestmentBlockFromJson()
    {
        var dto = JsonSerializer.Deserialize<DealDto>(DealJson, ApiJson)!;

        dto.Reinvestment.Should().NotBeNull();
        var r = dto.Reinvestment!;
        r.ReinvestEndDate.Should().Be(new DateTime(2029, 6, 1));
        r.ReinvestStartDate.Should().Be(new DateTime(2026, 6, 1));
        r.Target.Should().Be(1_000_000);
        r.Holdback.Should().Be(0.05);
        r.ReinvestRecoveries.Should().BeTrue();
        r.Templates.Should().HaveCount(2);
        r.Templates[1].IsSynthetic.Should().BeTrue();
        r.Templates[1].IndexName.Should().Be(MarketDataInstEnum.Sofr30Avg); // string enum bound
        r.Templates[1].AmortizationType.Should().Be(AmortizationType.Bullet);
    }

    [Fact]
    public void Mapper_ProducesValidatedDomainConfig()
    {
        var dto = JsonSerializer.Deserialize<DealDto>(DealJson, ApiJson)!;

        var cfg = ReinvestmentConfigMapper.Map(dto.Reinvestment, dto.DealName)!;
        cfg.Should().NotBeNull();

        cfg.Target.Should().Be(1_000_000);
        cfg.Holdback.Should().Be(0.05);
        // Explicit recoveries=true, defaults keep scheduled+prepay on.
        cfg.EligibleProceeds.Should().Be(
            EligibleProceeds.ScheduledPrincipal | EligibleProceeds.Prepayments | EligibleProceeds.Recoveries);

        cfg.Templates.Should().HaveCount(2);
        cfg.Templates[0].CouponRate.Should().Be(6.25);
        cfg.Templates[0].EffectivePrice.Should().Be(99.5);      // cash price preserved
        cfg.Templates[1].IsSynthetic.Should().BeTrue();
        cfg.Templates[1].EffectivePrice.Should().Be(100.0);     // synthetic forced to par despite price 95
        cfg.Templates[1].IndexMargin.Should().Be(2.5);
    }

    [Fact]
    public void Mapper_DefaultsEligibleProceeds_WhenFlagsOmitted()
    {
        const string json = """
        { "dealName": "D",
          "reinvestment": {
            "reinvestEndDate": "2029-06-01", "target": 500000,
            "templates": [ { "allocationPct": 100, "termMonths": 48, "couponRate": 5 } ] } }
        """;
        var dto = JsonSerializer.Deserialize<DealDto>(json, ApiJson)!;

        var cfg = ReinvestmentConfigMapper.Map(dto.Reinvestment, dto.DealName)!;
        cfg.EligibleProceeds.Should().Be(EligibleProceeds.ScheduledPrincipal | EligibleProceeds.Prepayments);
    }

    [Fact]
    public void Mapper_NullReinvestment_ReturnsNull()
    {
        var dto = JsonSerializer.Deserialize<DealDto>("""{ "dealName": "D" }""", ApiJson)!;
        ReinvestmentConfigMapper.Map(dto.Reinvestment, dto.DealName).Should().BeNull();
    }

    [Fact]
    public void Mapper_InvalidAllocations_Throws()
    {
        const string json = """
        { "dealName": "BADALLOC",
          "reinvestment": {
            "reinvestEndDate": "2029-06-01", "target": 500000,
            "templates": [ { "allocationPct": 70, "termMonths": 48 } ] } }
        """;
        var dto = JsonSerializer.Deserialize<DealDto>(json, ApiJson)!;

        var act = () => ReinvestmentConfigMapper.Map(dto.Reinvestment, dto.DealName);
        act.Should().Throw<InvalidOperationException>().WithMessage("*sum to 100*");
    }
}
