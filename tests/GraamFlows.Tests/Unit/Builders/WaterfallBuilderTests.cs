using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using Xunit;

namespace GraamFlows.Tests.Unit.Builders;

/// <summary>
/// Tests for WaterfallBuilder which transforms structured waterfall DTOs into PayRule DSL strings.
/// </summary>
public class WaterfallBuilderTests
{
    [Fact]
    public void BuildStructureDsl_SingleType_ReturnsSingleDsl()
    {
        var structure = new PayableStructureDto { Type = "SINGLE", Tranche = "A-1" };

        var dsl = WaterfallBuilder.BuildStructureDsl(structure);

        dsl.Should().Be("SINGLE('A-1')");
    }

    [Fact]
    public void BuildStructureDsl_SeqWithTranches_WrapsInSingles()
    {
        var structure = new PayableStructureDto
        {
            Type = "SEQ",
            Tranches = new List<string> { "A-1", "A-2", "B" }
        };

        var dsl = WaterfallBuilder.BuildStructureDsl(structure);

        dsl.Should().Be("SEQ(SINGLE('A-1'), SINGLE('A-2'), SINGLE('B'))");
    }

    [Fact]
    public void BuildStructureDsl_ProrataWithTranches()
    {
        var structure = new PayableStructureDto
        {
            Type = "PRORATA",
            Tranches = new List<string> { "A-1", "A-2" }
        };

        var dsl = WaterfallBuilder.BuildStructureDsl(structure);

        dsl.Should().Be("PRORATA('A-1','A-2')");
    }

    [Fact]
    public void BuildStructureDsl_SeqWithChildren_NestsStructures()
    {
        var structure = new PayableStructureDto
        {
            Type = "SEQ",
            Children = new List<PayableStructureDto>
            {
                new() { Type = "PRORATA", Tranches = new List<string> { "A-1", "A-2" } },
                new() { Type = "SINGLE", Tranche = "B" }
            }
        };

        var dsl = WaterfallBuilder.BuildStructureDsl(structure);

        dsl.Should().Be("SEQ(PRORATA('A-1','A-2'), SINGLE('B'))");
    }

    [Fact]
    public void BuildStructureDsl_Shifti_WithConstantPercent()
    {
        var structure = new PayableStructureDto
        {
            Type = "SHIFTI",
            ShiftPercent = 0.7,
            Seniors = new PayableStructureDto { Type = "SINGLE", Tranche = "A" },
            Subordinates = new PayableStructureDto { Type = "SINGLE", Tranche = "B" }
        };

        var dsl = WaterfallBuilder.BuildStructureDsl(structure);

        dsl.Should().Be("SHIFTI(0.7, SINGLE('A'), SINGLE('B'))");
    }

    [Fact]
    public void BuildStructureDsl_Shifti_WithVariable()
    {
        var structure = new PayableStructureDto
        {
            Type = "SHIFTI",
            ShiftVariable = "ShiftPct",
            Seniors = new PayableStructureDto { Type = "SINGLE", Tranche = "A" },
            Subordinates = new PayableStructureDto { Type = "SINGLE", Tranche = "B" }
        };

        var dsl = WaterfallBuilder.BuildStructureDsl(structure);

        dsl.Should().Be("SHIFTI('ShiftPct', SINGLE('A'), SINGLE('B'))");
    }

    [Fact]
    public void BuildStructureDsl_Cscap_WithVariable()
    {
        var structure = new PayableStructureDto
        {
            Type = "CSCAP",
            CapVariable = "SupplSubReduAmt",
            Primary = new PayableStructureDto { Type = "SINGLE", Tranche = "Senior" },
            Cap = new PayableStructureDto { Type = "SINGLE", Tranche = "Sub" }
        };

        var dsl = WaterfallBuilder.BuildStructureDsl(structure);

        dsl.Should().Be("CSCAP('SupplSubReduAmt', SINGLE('Senior'), SINGLE('Sub'))");
    }

    [Fact]
    public void BuildStructureDsl_Fixed_WithAmount()
    {
        var structure = new PayableStructureDto
        {
            Type = "FIXED",
            FixedAmount = 12345,
            Primary = new PayableStructureDto { Type = "SINGLE", Tranche = "A" },
            Overflow = new PayableStructureDto { Type = "SINGLE", Tranche = "B" }
        };

        var dsl = WaterfallBuilder.BuildStructureDsl(structure);

        dsl.Should().Be("FIXED(12345, SINGLE('A'), SINGLE('B'))");
    }

    [Fact]
    public void BuildStructureDsl_ForcePaydown()
    {
        var structure = new PayableStructureDto
        {
            Type = "FORCE_PAYDOWN",
            Forced = new PayableStructureDto { Type = "SINGLE", Tranche = "OC" },
            Support = new PayableStructureDto { Type = "SINGLE", Tranche = "Notes" }
        };

        var dsl = WaterfallBuilder.BuildStructureDsl(structure);

        dsl.Should().Be("FORCE_PAYDOWN(SINGLE('OC'), SINGLE('Notes'))");
    }

    [Fact]
    public void BuildStructureDsl_Accrete()
    {
        var structure = new PayableStructureDto { Type = "ACCRETE", Tranche = "OC" };

        var dsl = WaterfallBuilder.BuildStructureDsl(structure);

        dsl.Should().Be("ACCRETE('OC')");
    }

    [Fact]
    public void BuildStructureDsl_UnknownType_Throws()
    {
        var structure = new PayableStructureDto { Type = "UNKNOWN" };

        var act = () => WaterfallBuilder.BuildStructureDsl(structure);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildPayRules_SimpleSequential_GeneratesAllPrincipalRules()
    {
        var waterfall = new WaterfallStructureDto
        {
            ScheduledPrincipal = new WaterfallPrincipalDto
            {
                Default = new PayableStructureDto
                {
                    Type = "SEQ",
                    Tranches = new List<string> { "A", "B", "C" }
                }
            }
        };

        var rules = WaterfallBuilder.BuildPayRules(waterfall);

        rules.Should().NotBeEmpty();
        rules.First().Formula.Should().Contain("SET_SCHED_STRUCT");
        rules.First().Formula.Should().Contain("SEQ(SINGLE('A'), SINGLE('B'), SINGLE('C'))");
    }

    [Fact]
    public void BuildPayRules_WithTriggerCondition_GeneratesConditionalRules()
    {
        var waterfall = new WaterfallStructureDto
        {
            ScheduledPrincipal = new WaterfallPrincipalDto
            {
                Default = new PayableStructureDto
                {
                    Type = "SEQ",
                    Tranches = new List<string> { "A", "B" }
                },
                OnTriggerFail = new TriggerConditionDto
                {
                    Triggers = new List<string> { "CE_Trigger" },
                    Structure = new PayableStructureDto
                    {
                        Type = "PRORATA",
                        Tranches = new List<string> { "A", "B" }
                    }
                }
            }
        };

        var rules = WaterfallBuilder.BuildPayRules(waterfall);

        rules.Should().HaveCountGreaterThanOrEqualTo(2);
        rules.Should().Contain(r => r.Formula.Contains("PASSED('CE_Trigger')"));
        rules.Should().Contain(r => r.Formula.Contains("FAILED('CE_Trigger')"));
    }

    [Fact]
    public void BuildPayRules_SameUnscheduled_ReusesScheduled()
    {
        var waterfall = new WaterfallStructureDto
        {
            ScheduledPrincipal = new WaterfallPrincipalDto
            {
                Default = new PayableStructureDto
                {
                    Type = "SEQ",
                    Tranches = new List<string> { "A", "B" }
                }
            },
            UnscheduledPrincipal = "same"
        };

        var rules = WaterfallBuilder.BuildPayRules(waterfall);

        // Should have both SCHED and PREPAY rules
        rules.Should().Contain(r => r.Formula.Contains("SET_SCHED_STRUCT"));
        rules.Should().Contain(r => r.Formula.Contains("SET_PREPAY_STRUCT"));
    }
}
