using GraamFlows.Api.Models;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Transforms unified waterfall (steps-based) definitions into PayRules and DealStructures.
///     Eliminates classGroups dependency by inferring subordination from writedown order.
/// </summary>
public static class UnifiedWaterfallBuilder
{
    /// <summary>
    ///     Auto-generates DealStructure objects from tranche list.
    ///     Subordination order is inferred from WRITEDOWN step's structure order.
    /// </summary>
    public static List<DealStructureDto> BuildDealStructures(
        UnifiedWaterfallDto waterfall,
        List<TrancheDto> tranches)
    {
        // Find WRITEDOWN step and extract subordination order
        var writedownStep = waterfall.Steps.FirstOrDefault(s =>
            s.Type.Equals("WRITEDOWN", StringComparison.OrdinalIgnoreCase));

        var writedownOrder = writedownStep?.Structure != null
            ? ExtractTrancheOrder(writedownStep.Structure)
            : new List<string>();

        // Create DealStructure for each tranche
        return tranches.Select((t, idx) =>
        {
            var writedownIdx = writedownOrder.IndexOf(t.TrancheName);
            return new DealStructureDto
            {
                ClassGroupName = t.TrancheName,
                // Higher order = more junior. First in writedown list = most junior
                SubordinationOrder = writedownIdx >= 0
                    ? writedownOrder.Count - writedownIdx
                    : idx,
                PayFrom = "Sequential",
                GroupNum = "1"
            };
        }).ToList();
    }

    /// <summary>
    ///     Generates PayRule DTOs from a unified waterfall definition
    /// </summary>
    public static List<PayRuleDto> BuildPayRules(UnifiedWaterfallDto waterfall, string groupName = "GROUP_1")
    {
        var rules = new List<PayRuleDto>();
        var priority = 0;

        // Track principal structures for "useStructure" references
        var principalStructures = new Dictionary<string, WaterfallStepDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in waterfall.Steps)
            switch (step.Type.ToUpperInvariant())
            {
                case "INTEREST":
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "InterestStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_INTEREST_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    break;

                case "PRINCIPAL":
                    var source = step.Source?.ToLower() ?? "scheduled";
                    var setFunc = source switch
                    {
                        "scheduled" => "SET_SCHED_STRUCT",
                        "unscheduled" => "SET_PREPAY_STRUCT",
                        "recovery" => "SET_RECOV_STRUCT",
                        _ => "SET_SCHED_STRUCT"
                    };

                    // Handle useStructure reference
                    var effectiveStep = step;
                    if (!string.IsNullOrEmpty(step.UseStructure) &&
                        principalStructures.TryGetValue(step.UseStructure, out var refStep))
                        effectiveStep = refStep;

                    // Store this step for potential future references
                    if (effectiveStep.Default != null) principalStructures[source] = effectiveStep;

                    // Generate rules (with trigger conditions if present)
                    rules.AddRange(BuildPrincipalStepRules(effectiveStep, setFunc, groupName, ref priority));
                    break;

                case "WRITEDOWN":
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "WritedownStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_WRITEDOWN_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    break;

                case "EXCESS":
                    // EXCESS step defines where excess spread accretes (typically OC/residual tranche)
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "ExcessStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_EXCESS_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    break;
            }

        return rules;
    }

    /// <summary>
    ///     Builds PayRules for a PRINCIPAL step, handling trigger conditions
    /// </summary>
    private static List<PayRuleDto> BuildPrincipalStepRules(
        WaterfallStepDto step,
        string setStructFunc,
        string groupName,
        ref int priority)
    {
        var rules = new List<PayRuleDto>();
        var prefix = step.Source?.ToLower() switch
        {
            "scheduled" => "Sched",
            "unscheduled" => "Prepay",
            "recovery" => "Recov",
            _ => "Prin"
        };

        // If there's a trigger condition, generate conditional rules
        if (step.OnTriggerFail != null && step.Default != null)
        {
            var triggerNames = string.Join(",", step.OnTriggerFail.Triggers);
            var passedDsl = WaterfallBuilder.BuildStructureDsl(step.Default);
            var failedDsl = WaterfallBuilder.BuildStructureDsl(step.OnTriggerFail.Structure!);

            // Rule for when triggers pass
            rules.Add(new PayRuleDto
            {
                RuleName = $"{prefix}PrinPass",
                ClassGroupName = groupName,
                Formula = $"if (PASSED('{triggerNames}')) {setStructFunc}({passedDsl})",
                Priority = priority++
            });

            // Rule for when triggers fail
            rules.Add(new PayRuleDto
            {
                RuleName = $"{prefix}PrinFail",
                ClassGroupName = groupName,
                Formula = $"if (FAILED('{triggerNames}')) {setStructFunc}({failedDsl})",
                Priority = priority++
            });
        }
        else if (step.Default != null)
        {
            // Simple unconditional structure
            var structDsl = WaterfallBuilder.BuildStructureDsl(step.Default);
            rules.Add(new PayRuleDto
            {
                RuleName = $"{prefix}Struct",
                ClassGroupName = groupName,
                Formula = $"{setStructFunc}({structDsl})",
                Priority = priority++
            });
        }

        return rules;
    }

    /// <summary>
    ///     Extracts tranche names in order from a payable structure (depth-first)
    /// </summary>
    public static List<string> ExtractTrancheOrder(PayableStructureDto structure)
    {
        var tranches = new List<string>();
        ExtractTranchesRecursive(structure, tranches);
        return tranches;
    }

    private static void ExtractTranchesRecursive(PayableStructureDto structure, List<string> tranches)
    {
        // Handle SINGLE type
        if (structure.Type.Equals("SINGLE", StringComparison.OrdinalIgnoreCase))
        {
            var tranche = structure.Tranche ?? structure.Tranches?.FirstOrDefault();
            if (!string.IsNullOrEmpty(tranche)) tranches.Add(tranche);
            return;
        }

        // Handle shorthand Tranches list
        if (structure.Tranches != null && structure.Tranches.Count > 0) tranches.AddRange(structure.Tranches);

        // Handle Children
        if (structure.Children != null)
            foreach (var child in structure.Children)
                ExtractTranchesRecursive(child, tranches);

        // Handle SHIFTI seniors/subordinates
        if (structure.Seniors != null) ExtractTranchesRecursive(structure.Seniors, tranches);
        if (structure.Subordinates != null) ExtractTranchesRecursive(structure.Subordinates, tranches);
    }

    /// <summary>
    ///     Validates that required steps are present in the unified waterfall
    /// </summary>
    public static void ValidateSteps(UnifiedWaterfallDto waterfall, string dealName)
    {
        var stepTypes = waterfall.Steps.Select(s => s.Type.ToUpperInvariant()).ToHashSet();

        if (!stepTypes.Contains("INTEREST"))
            throw new InvalidOperationException(
                $"Deal {dealName}: UnifiedStructure requires INTEREST step in waterfall");

        if (!stepTypes.Contains("WRITEDOWN"))
            throw new InvalidOperationException(
                $"Deal {dealName}: UnifiedStructure requires WRITEDOWN step in waterfall");

        var hasPrincipal = waterfall.Steps.Any(s =>
            s.Type.Equals("PRINCIPAL", StringComparison.OrdinalIgnoreCase) &&
            (s.Source?.Equals("scheduled", StringComparison.OrdinalIgnoreCase) ?? true));

        if (!hasPrincipal)
            throw new InvalidOperationException(
                $"Deal {dealName}: UnifiedStructure requires PRINCIPAL (scheduled) step in waterfall");
    }
}