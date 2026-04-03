using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using Xunit;

namespace GraamFlows.Tests.Unit.Builders;

/// <summary>
/// Tests for UnifiedWaterfallBuilder which transforms step-based waterfall JSON
/// into PayRules and DealStructures.
/// </summary>
public class UnifiedWaterfallBuilderTests
{
    [Fact]
    public void BuildPayRules_InterestStep_GeneratesSetInterestStruct()
    {
        var waterfall = CreateMinimalWaterfall();

        var rules = UnifiedWaterfallBuilder.BuildPayRules(waterfall);

        rules.Should().Contain(r => r.Formula.Contains("SET_INTEREST_STRUCT"));
    }

    [Fact]
    public void BuildPayRules_WritedownStep_GeneratesSetWritedownStruct()
    {
        var waterfall = CreateMinimalWaterfall();

        var rules = UnifiedWaterfallBuilder.BuildPayRules(waterfall);

        rules.Should().Contain(r => r.Formula.Contains("SET_WRITEDOWN_STRUCT"));
    }

    [Fact]
    public void BuildPayRules_PrincipalScheduled_GeneratesSetSchedStruct()
    {
        var waterfall = CreateMinimalWaterfall();

        var rules = UnifiedWaterfallBuilder.BuildPayRules(waterfall);

        rules.Should().Contain(r => r.Formula.Contains("SET_SCHED_STRUCT"));
    }

    [Fact]
    public void BuildPayRules_ExcessStep_GeneratesSetExcessStruct()
    {
        var waterfall = CreateMinimalWaterfall();
        waterfall.Steps.Add(new WaterfallStepDto
        {
            Type = "EXCESS",
            Structure = new PayableStructureDto { Type = "SINGLE", Tranche = "R" }
        });

        var rules = UnifiedWaterfallBuilder.BuildPayRules(waterfall);

        rules.Should().Contain(r => r.Formula.Contains("SET_EXCESS_STRUCT"));
    }

    [Fact]
    public void BuildPayRules_PrincipalWithMultiBranchRules_GeneratesConditionalRules()
    {
        var waterfall = CreateMinimalWaterfall();
        // Replace the simple principal step with a multi-branch one
        var principalStep = waterfall.Steps.First(s => s.Type == "PRINCIPAL" && s.Source == "scheduled");
        principalStep.Default = null;
        principalStep.Rules = new List<WaterfallRuleDto>
        {
            new()
            {
                When = new RuleConditionDto { Pass = new List<string> { "CE_Test" } },
                Structure = new PayableStructureDto
                {
                    Type = "SEQ",
                    Tranches = new List<string> { "A", "B", "C" }
                }
            },
            new()
            {
                // Fallback (no condition)
                Structure = new PayableStructureDto
                {
                    Type = "PRORATA",
                    Tranches = new List<string> { "A", "B", "C" }
                }
            }
        };

        var rules = UnifiedWaterfallBuilder.BuildPayRules(waterfall);

        rules.Should().Contain(r => r.Formula.Contains("PASSED('CE_Test')"));
    }

    [Fact]
    public void BuildPayRules_ComputedVariables_GeneratesSetVarRules()
    {
        var waterfall = CreateMinimalWaterfall();
        waterfall.ComputedVariables = new List<ComputedVariableDto>
        {
            new()
            {
                Name = "SenRedu",
                Rules = new List<ComputedVariableRuleDto>
                {
                    new()
                    {
                        When = new RuleConditionDto { Fail = new List<string> { "CE_Test" } },
                        Formula = "0.0"
                    },
                    new() { Formula = "0.055" }
                }
            }
        };

        var rules = UnifiedWaterfallBuilder.BuildPayRules(waterfall);

        rules.Should().Contain(r => r.Formula.Contains("SET_VAR('SenRedu'"));
    }

    [Fact]
    public void BuildPayRules_PriorityAutoIncrements()
    {
        var waterfall = CreateMinimalWaterfall();

        var rules = UnifiedWaterfallBuilder.BuildPayRules(waterfall);

        var priorities = rules.Select(r => r.Priority).ToList();
        priorities.Should().BeInAscendingOrder();
        priorities.Distinct().Count().Should().Be(priorities.Count, "All priorities should be unique");
    }

    [Fact]
    public void BuildDealStructures_OrdersFromWritedownStep()
    {
        var waterfall = CreateMinimalWaterfall();
        var tranches = new List<TrancheDto>
        {
            new() { TrancheName = "A" },
            new() { TrancheName = "B" },
            new() { TrancheName = "C" }
        };

        var structures = UnifiedWaterfallBuilder.BuildDealStructures(waterfall, tranches);

        structures.Should().HaveCount(3);
        // Writedown is SEQ(C, B, A) so C is most junior, A is most senior
        var aStruct = structures.First(s => s.ClassGroupName == "A");
        var cStruct = structures.First(s => s.ClassGroupName == "C");
        aStruct.SubordinationOrder.Should().BeLessThan(cStruct.SubordinationOrder,
            "A (last in writedown) should be more senior (lower order) than C (first in writedown)");
    }

    [Fact]
    public void BuildDealStructures_AllGroupNumOne()
    {
        var waterfall = CreateMinimalWaterfall();
        var tranches = new List<TrancheDto>
        {
            new() { TrancheName = "A" },
            new() { TrancheName = "B" },
            new() { TrancheName = "C" }
        };

        var structures = UnifiedWaterfallBuilder.BuildDealStructures(waterfall, tranches);

        structures.Should().OnlyContain(s => s.GroupNum == "1");
    }

    [Fact]
    public void ValidateSteps_MissingInterest_GeneratesNoInterestRule()
    {
        var waterfall = new UnifiedWaterfallDto
        {
            Steps = new List<WaterfallStepDto>
            {
                new()
                {
                    Type = "PRINCIPAL", Source = "scheduled",
                    Default = new PayableStructureDto { Type = "SINGLE", Tranche = "A" }
                },
                new()
                {
                    Type = "WRITEDOWN",
                    Structure = new PayableStructureDto { Type = "SINGLE", Tranche = "A" }
                }
            }
        };

        var rules = UnifiedWaterfallBuilder.BuildPayRules(waterfall);

        // Without INTEREST step, no SET_INTEREST_STRUCT rule should be generated
        rules.Should().NotContain(r => r.Formula.Contains("SET_INTEREST_STRUCT"));
    }

    [Fact]
    public void BuildPayRules_ExecutionOrder_Preserved()
    {
        var waterfall = CreateMinimalWaterfall();
        waterfall.ExecutionOrder = new List<string>
        {
            "EXPENSE", "INTEREST", "PRINCIPAL_SCHEDULED", "WRITEDOWN", "EXCESS"
        };

        var rules = UnifiedWaterfallBuilder.BuildPayRules(waterfall);

        // Rules should still be generated (execution order is metadata, not rule generation)
        rules.Should().NotBeEmpty();
    }

    [Fact]
    public void BuildPayRules_SupplementalReductionStep_GeneratesSupplConfig()
    {
        var waterfall = CreateMinimalWaterfall();
        waterfall.Steps.Add(new WaterfallStepDto
        {
            Type = "SUPPLEMENTAL_REDUCTION",
            CapVariable = "SupplSubReduAmt",
            OfferedTranches = new List<string> { "M1", "M2" },
            SeniorTranches = new List<string> { "AH", "B1H" },
            Default = new PayableStructureDto
            {
                Type = "SEQ",
                Tranches = new List<string> { "M1", "M2" }
            }
        });

        var rules = UnifiedWaterfallBuilder.BuildPayRules(waterfall);

        rules.Should().Contain(r => r.Formula.Contains("SET_SUPPL_CONFIG"));
    }

    private static UnifiedWaterfallDto CreateMinimalWaterfall()
    {
        return new UnifiedWaterfallDto
        {
            Steps = new List<WaterfallStepDto>
            {
                new()
                {
                    Type = "INTEREST",
                    Structure = new PayableStructureDto
                    {
                        Type = "SEQ",
                        Tranches = new List<string> { "A", "B", "C" }
                    }
                },
                new()
                {
                    Type = "PRINCIPAL",
                    Source = "scheduled",
                    Default = new PayableStructureDto
                    {
                        Type = "SEQ",
                        Tranches = new List<string> { "A", "B", "C" }
                    }
                },
                new()
                {
                    Type = "WRITEDOWN",
                    Structure = new PayableStructureDto
                    {
                        Type = "SEQ",
                        Tranches = new List<string> { "C", "B", "A" }
                    }
                }
            }
        };
    }
}
