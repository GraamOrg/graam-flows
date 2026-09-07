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

    /// <summary>
    /// Issue #9 (exposed by graam-harmony#1054): the fallback rule (no <c>When</c>)
    /// must be guarded with the negation of all prior conditions. Without this guard
    /// the fallback's unconditional formula runs every period under "last matching
    /// wins" semantics and overwrites whatever the prior conditional rules just set
    /// — so PRORATA seniors gated on credit triggers would always be reverted to the
    /// sequential fallback. Mirrors the same negation pattern in
    /// <see cref="UnifiedWaterfallBuilder.BuildComputedVariableRules"/>.
    /// </summary>
    [Fact]
    public void BuildPayRules_MultiBranchFallback_IsGuardedByNegatedPriorConditions()
    {
        var waterfall = CreateMinimalWaterfall();
        var principalStep = waterfall.Steps.First(s => s.Type == "PRINCIPAL" && s.Source == "scheduled");
        principalStep.Default = null;
        principalStep.Rules = new List<WaterfallRuleDto>
        {
            new()
            {
                When = new RuleConditionDto
                {
                    Pass = new List<string> { "DelinquencyTest", "CumNetLossTest" }
                },
                Structure = new PayableStructureDto
                {
                    Type = "SEQ",
                    Children = new List<PayableStructureDto>
                    {
                        new()
                        {
                            Type = "PRORATA",
                            Tranches = new List<string> { "A1", "A2", "A3" }
                        }
                    }
                }
            },
            new()
            {
                // Fallback — no When clause. Must be emitted as
                // `if (!(prior)) SET_SCHED_STRUCT(...)` so it only fires when the
                // prior conditional rule did not match.
                Structure = new PayableStructureDto
                {
                    Type = "SEQ",
                    Tranches = new List<string> { "A1", "A2", "A3" }
                }
            }
        };

        var rules = UnifiedWaterfallBuilder.BuildPayRules(waterfall);

        // The conditional rule is unchanged — gated on PASSED(...).
        rules.Should().Contain(r =>
            r.Formula.Contains("if (PASSED('DelinquencyTest,CumNetLossTest'))") &&
            r.Formula.Contains("PRORATA"));

        // The fallback's formula must be guarded by the negation of the prior
        // condition. Without this guard the unconditional SET_SCHED_STRUCT(SEQ(...))
        // would run every period and overwrite the conditional PRORATA above.
        var fallbackRule = rules.Single(r =>
            r.Formula.Contains("SET_SCHED_STRUCT") &&
            !r.Formula.Contains("PRORATA"));
        fallbackRule.Formula.Should().StartWith("if (!(PASSED('DelinquencyTest,CumNetLossTest')))");
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
    public void BuildDealStructures_ServicingIoStrip_PaysFromExcessServicing()
    {
        // A Class A-IO-S servicing strip (IO + Reference) must draw its strip
        // from the servicing fee, so it routes PayFrom = ExcessServicing — not
        // the default Sequential — while the offered classes stay Sequential.
        var waterfall = CreateMinimalWaterfall();
        var tranches = new List<TrancheDto>
        {
            new() { TrancheName = "A" },
            new() { TrancheName = "AIOS", CashflowType = "IO", TrancheType = "Reference" }
        };

        var structures = UnifiedWaterfallBuilder.BuildDealStructures(waterfall, tranches);

        structures.First(s => s.ClassGroupName == "AIOS").PayFrom.Should().Be("ExcessServicing");
        structures.First(s => s.ClassGroupName == "A").PayFrom.Should().Be("Sequential");
    }

    [Fact]
    public void BuildDealStructures_ExcessSpreadXs_StaysSequentialNotExcessServicing()
    {
        // The Class XS excess-SPREAD strip is also an IO, but it is the
        // ResidualInterest sweeper — it must NOT be diverted to ExcessServicing.
        var waterfall = CreateMinimalWaterfall();
        var tranches = new List<TrancheDto>
        {
            new() { TrancheName = "A" },
            new()
            {
                TrancheName = "XS",
                CashflowType = "IO",
                TrancheType = "Offered",
                CouponType = "ResidualInterest"
            }
        };

        var structures = UnifiedWaterfallBuilder.BuildDealStructures(waterfall, tranches);

        structures.First(s => s.ClassGroupName == "XS").PayFrom.Should().Be("Sequential");
    }

    [Fact]
    public void BuildDealStructures_ExchangedTranche_PaysFromExchangeWithComponents()
    {
        // An Exchanged (combinable / MACR) class must route PayFrom = "Exchange"
        // AND carry its component tranches on ExchangableTranche (comma-joined,
        // the format PayExchangeables/ClassesByNameOrTag splits on) so the
        // exchange overlay derives its cashflow. The unified-waterfall transform
        // previously dropped both, leaving the class flat at issuance forever
        // (graam-harmony#2808).
        var waterfall = CreateMinimalWaterfall();
        var tranches = new List<TrancheDto>
        {
            new() { TrancheName = "A1A" },
            new() { TrancheName = "A1B" },
            new() { TrancheName = "A1", TrancheType = "Exchanged" }
        };
        var exchangeShares = new List<ExchangeShareDto>
        {
            new()
            {
                ExchangeTranche = "A1",
                Shares = new List<ExShareDto>
                {
                    new() { TrancheName = "A1A", ShareAmount = 60_000_000 },
                    new() { TrancheName = "A1B", ShareAmount = 40_000_000 }
                }
            }
        };

        var structures = UnifiedWaterfallBuilder.BuildDealStructures(waterfall, tranches, exchangeShares);

        var a1 = structures.First(s => s.ClassGroupName == "A1");
        a1.PayFrom.Should().Be("Exchange");
        a1.ExchangableTranche.Should().Be("A1A,A1B");
        // Non-exchange components keep the default sequential routing and no overlay.
        structures.First(s => s.ClassGroupName == "A1A").PayFrom.Should().Be("Sequential");
        structures.First(s => s.ClassGroupName == "A1A").ExchangableTranche.Should().BeNull();
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
    public void BuildPayRules_PrincipalUseStructure_ProducesDistinctRuleNamesPerSource()
    {
        // Regression: when unscheduled/recovery steps use `useStructure: "scheduled"`
        // and the scheduled step has trigger-conditional branches, the generated rule
        // names collided on Sched*Pass/Sched*Fail across sources, producing CS0111
        // duplicate methods in the Roslyn-compiled RulesHost.
        var waterfall = new UnifiedWaterfallDto
        {
            Steps = new List<WaterfallStepDto>
            {
                new()
                {
                    Type = "INTEREST",
                    Structure = new PayableStructureDto
                        { Type = "SEQ", Tranches = new List<string> { "A", "B" } }
                },
                new()
                {
                    Type = "PRINCIPAL", Source = "scheduled",
                    Default = new PayableStructureDto
                        { Type = "SEQ", Tranches = new List<string> { "A", "B" } },
                    OnTriggerFail = new TriggerConditionDto
                    {
                        Triggers = new List<string> { "CE_Test" },
                        Condition = "ANY",
                        Structure = new PayableStructureDto
                            { Type = "SEQ", Tranches = new List<string> { "A", "B" } }
                    }
                },
                new() { Type = "PRINCIPAL", Source = "unscheduled", UseStructure = "scheduled" },
                new() { Type = "PRINCIPAL", Source = "recovery", UseStructure = "scheduled" },
                new()
                {
                    Type = "WRITEDOWN",
                    Structure = new PayableStructureDto
                        { Type = "SEQ", Tranches = new List<string> { "B", "A" } }
                }
            }
        };

        var rules = UnifiedWaterfallBuilder.BuildPayRules(waterfall);

        rules.Select(r => r.RuleName).Should().OnlyHaveUniqueItems();
        rules.Should().Contain(r => r.RuleName == "SchedPrinPass");
        rules.Should().Contain(r => r.RuleName == "PrepayPrinPass");
        rules.Should().Contain(r => r.RuleName == "RecovPrinPass");
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

    // ---- MODIFICATION_LOSS ---------------------------------------------------------------

    [Fact]
    public void BuildPayRules_ModificationLossStep_EmitsTheLadderInDocumentOrder()
    {
        var rules = UnifiedWaterfallBuilder.BuildPayRules(WithModificationLoss(ModLossRungs()));

        var ladder = rules.Single(r => r.Formula.StartsWith("SET_MODLOSS_STRUCT"));
        // Each rung states the ARITY the builder emitted, so the engine can tell a pro-rata
        // rung that half-resolved from one that resolved: PRORATA drops names it cannot find,
        // and a one-class rung sized for two moves the allocation up the ladder.
        ladder.Formula.Should().Be(
            "SET_MODLOSS_STRUCT(ML_NOTIONAL(SINGLE('B3H'), 1), ML_INTEREST(SINGLE('B2H'), 'B2H', 1), " +
            "ML_NOTIONAL(SINGLE('B2H'), 1), ML_INTEREST(PRORATA('M2B','M2BH'), 'M2B', 2), " +
            "ML_NOTIONAL(PRORATA('M2B','M2BH'), 2))");
    }

    [Fact]
    public void BuildPayRules_ModificationLossStep_EmitsTheWriteUpTarget()
    {
        var step = WithModificationLoss(ModLossRungs());
        step.Steps.Last().WriteUpTranche = "AH";

        var rules = UnifiedWaterfallBuilder.BuildPayRules(step);

        rules.Should().Contain(r => r.Formula == "SET_MODLOSS_WRITEUP('AH')");
    }

    [Fact]
    public void BuildPayRules_ModificationLossStep_WithNoWriteUpTarget_EmitsNone()
    {
        var rules = UnifiedWaterfallBuilder.BuildPayRules(WithModificationLoss(ModLossRungs()));

        rules.Should().NotContain(r => r.Formula.Contains("SET_MODLOSS_WRITEUP"));
    }

    [Fact]
    public void BuildPayRules_ModificationLossStep_WithNoRungs_Throws()
    {
        var act = () => UnifiedWaterfallBuilder.BuildPayRules(
            WithModificationLoss(new List<ModificationLossRungDto>()));

        act.Should().Throw<InvalidOperationException>().WithMessage("*non-empty 'rungs'*");
    }

    [Fact]
    public void BuildPayRules_ModificationLossRung_WithUnknownEffect_Throws()
    {
        // Rejected rather than skipped: a dropped priority does not fail, it shifts every amount
        // below it up the ladder and quietly changes the answer.
        var act = () => UnifiedWaterfallBuilder.BuildPayRules(WithModificationLoss(
            new List<ModificationLossRungDto> { new() { Effect = "WRITEDOWN", Tranche = "B3H" } }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*expected 'NOTIONAL'*");
    }

    [Fact]
    public void BuildPayRules_ModificationLossRung_NamingNoTranche_Throws()
    {
        var act = () => UnifiedWaterfallBuilder.BuildPayRules(WithModificationLoss(
            new List<ModificationLossRungDto> { new() { Effect = "NOTIONAL" } }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*names no tranche*");
    }

    [Fact]
    public void BuildPayRules_ProRataInterestRung_WithNoCapTranche_Throws()
    {
        // The cap is stated on ONE member, so guessing the first name would silently size the
        // rung off whichever class the roster happened to list first.
        var act = () => UnifiedWaterfallBuilder.BuildPayRules(WithModificationLoss(
            new List<ModificationLossRungDto>
            {
                new() { Effect = "INTEREST", Tranches = new List<string> { "M2B", "M2BH" } }
            }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*states no 'capTranche'*");
    }

    [Fact]
    public void BuildPayRules_InterestRung_CappingOnAnOutsideClass_Throws()
    {
        var act = () => UnifiedWaterfallBuilder.BuildPayRules(WithModificationLoss(
            new List<ModificationLossRungDto>
            {
                new()
                {
                    Effect = "INTEREST",
                    Tranches = new List<string> { "M2B", "M2BH" },
                    CapTranche = "M1"
                }
            }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not one of the classes*");
    }

    private static List<ModificationLossRungDto> ModLossRungs()
    {
        // The shape of STACR 2025-DNA1's first five priorities.
        return new List<ModificationLossRungDto>
        {
            new() { Effect = "NOTIONAL", Tranche = "B3H" },
            new() { Effect = "INTEREST", Tranche = "B2H" },
            new() { Effect = "NOTIONAL", Tranche = "B2H" },
            new()
            {
                Effect = "INTEREST",
                Tranches = new List<string> { "M2B", "M2BH" },
                CapTranche = "M2B"
            },
            new() { Effect = "NOTIONAL", Tranches = new List<string> { "M2B", "M2BH" } }
        };
    }

    private static UnifiedWaterfallDto WithModificationLoss(List<ModificationLossRungDto> rungs)
    {
        var waterfall = CreateMinimalWaterfall();
        waterfall.Steps.Add(new WaterfallStepDto { Type = "MODIFICATION_LOSS", Rungs = rungs });
        return waterfall;
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
