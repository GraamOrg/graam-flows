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

        // Emit computed variable rules first (they run before structure selection)
        if (waterfall.ComputedVariables != null && waterfall.ComputedVariables.Count > 0)
            rules.AddRange(BuildComputedVariableRules(waterfall.ComputedVariables, groupName, ref priority));

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
                    var prefix = source switch
                    {
                        "scheduled" => "Sched",
                        "unscheduled" => "Prepay",
                        "recovery" => "Recov",
                        _ => "Prin"
                    };

                    // Handle useStructure reference
                    var effectiveStep = step;
                    if (!string.IsNullOrEmpty(step.UseStructure) &&
                        principalStructures.TryGetValue(step.UseStructure, out var refStep))
                        effectiveStep = refStep;

                    // Store this step for potential future references
                    if (effectiveStep.Default != null) principalStructures[source] = effectiveStep;

                    // Generate rules (with trigger conditions if present).
                    // Prefix is derived from the *current* step's source, not effectiveStep's,
                    // so useStructure references produce distinct rule names per source.
                    rules.AddRange(BuildPrincipalStepRules(effectiveStep, setFunc, prefix, groupName, ref priority));
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

                case "EXCESS_TURBO":
                    // EXCESS_TURBO pays down notes up to OC shortfall
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "TurboStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_TURBO_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    break;

                case "EXCESS_RELEASE":
                    // EXCESS_RELEASE releases remainder to certificates
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "ReleaseStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_RELEASE_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    break;

                case "CAP_CARRYOVER":
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "CapCarryoverStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_CAP_CARRYOVER_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    break;

                case "SUPPLEMENTAL_REDUCTION":
                    if (step.Structure != null)
                    {
                        var dsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "SupplStruct",
                            ClassGroupName = groupName,
                            Formula = $"SET_SUPPL_STRUCT({dsl})",
                            Priority = priority++
                        });
                    }

                    if (!string.IsNullOrEmpty(step.CapVariable) && step.OfferedTranches != null && step.SeniorTranches != null)
                    {
                        var subList = string.Join(",", step.OfferedTranches);
                        var senList = string.Join(",", step.SeniorTranches);
                        rules.Add(new PayRuleDto
                        {
                            RuleName = "SupplConfig",
                            ClassGroupName = groupName,
                            Formula = $"SET_SUPPL_CONFIG('{step.CapVariable}', '{subList}', '{senList}')",
                            Priority = priority++
                        });
                    }

                    break;
            }

        return rules;
    }

    /// <summary>
    ///     Builds PayRules for a PRINCIPAL step, handling trigger conditions.
    ///     Supports both legacy Default/OnTriggerFail and new multi-branch Rules array.
    /// </summary>
    private static List<PayRuleDto> BuildPrincipalStepRules(
        WaterfallStepDto step,
        string setStructFunc,
        string prefix,
        string groupName,
        ref int priority)
    {
        var rules = new List<PayRuleDto>();

        // Multi-branch rules array (new format for complex deals like STACR)
        if (step.Rules != null && step.Rules.Count > 0)
        {
            rules.AddRange(BuildMultiBranchRules(step.Rules, prefix, setStructFunc, groupName, ref priority));
            return rules;
        }

        // Legacy: simple Default/OnTriggerFail two-branch model
        if (step.OnTriggerFail != null && step.Default != null)
        {
            var triggerNames = string.Join(",", step.OnTriggerFail.Triggers);
            var passedDsl = WaterfallBuilder.BuildStructureDsl(step.Default);
            var failedDsl = WaterfallBuilder.BuildStructureDsl(step.OnTriggerFail.Structure!);

            rules.Add(new PayRuleDto
            {
                RuleName = $"{prefix}PrinPass",
                ClassGroupName = groupName,
                Formula = $"if (PASSED('{triggerNames}')) {setStructFunc}({passedDsl})",
                Priority = priority++
            });

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
            var structDsl = WaterfallBuilder.BuildStructureDsl(step.Default);
            rules.Add(new PayRuleDto
            {
                RuleName = $"{prefix}Struct",
                ClassGroupName = groupName,
                Formula = $"{setStructFunc}({structDsl})",
                Priority = priority++
            });
        }
        else if (step.Structure != null)
        {
            // Fallback: use Structure directly (e.g., recovery with unconditional structure)
            var structDsl = WaterfallBuilder.BuildStructureDsl(step.Structure);
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
    ///     Builds PayRules from a multi-branch rules array.
    ///     Each rule becomes a separate PayRule with its condition compiled to DSL.
    ///     Rules are emitted in order - last matching rule wins (Payscen convention).
    /// </summary>
    private static List<PayRuleDto> BuildMultiBranchRules(
        List<WaterfallRuleDto> branchRules,
        string prefix,
        string setStructFunc,
        string groupName,
        ref int priority)
    {
        var rules = new List<PayRuleDto>();

        for (var i = 0; i < branchRules.Count; i++)
        {
            var branch = branchRules[i];
            var structDsl = WaterfallBuilder.BuildStructureDsl(branch.Structure);
            var formula = $"{setStructFunc}({structDsl})";

            if (branch.When != null)
            {
                var condition = BuildConditionExpression(branch.When);
                formula = $"if ({condition}) {formula}";
            }

            rules.Add(new PayRuleDto
            {
                RuleName = $"{prefix}Rule{i}",
                ClassGroupName = groupName,
                Formula = formula,
                Priority = priority++
            });
        }

        return rules;
    }

    /// <summary>
    ///     Converts a RuleConditionDto to a DSL condition expression string.
    ///     All conditions are ANDed together.
    /// </summary>
    private static string BuildConditionExpression(RuleConditionDto condition)
    {
        var parts = new List<string>();

        if (condition.Pass != null && condition.Pass.Count > 0)
            parts.Add($"PASSED('{string.Join(",", condition.Pass)}')");

        if (condition.Fail != null && condition.Fail.Count > 0)
            parts.Add($"FAILED('{string.Join(",", condition.Fail)}')");

        if (condition.Vars != null)
        {
            foreach (var vc in condition.Vars)
            {
                if (vc.Op is not (">" or "<" or ">=" or "<=" or "==" or "!="))
                    throw new ArgumentException($"Unknown comparison operator: '{vc.Op}'");
                parts.Add($"VAR('{vc.Var}') {vc.Op} {vc.Value}");
            }
        }

        return string.Join(" && ", parts);
    }

    /// <summary>
    ///     Builds PayRules for computed variables (evaluated before waterfall each period).
    ///     Since all pay rules execute (last matching wins), fallback rules (no "when")
    ///     must be guarded with the negation of preceding conditions to avoid overwriting.
    /// </summary>
    public static List<PayRuleDto> BuildComputedVariableRules(
        List<ComputedVariableDto> computedVars,
        string groupName,
        ref int priority)
    {
        var rules = new List<PayRuleDto>();

        foreach (var cv in computedVars)
        {
            // Collect all conditions from prior rules to build negation for fallback
            var priorConditions = new List<string>();

            for (var i = 0; i < cv.Rules.Count; i++)
            {
                var rule = cv.Rules[i];
                var formula = $"SET_VAR('{cv.Name}', {rule.Formula})";

                if (rule.When != null)
                {
                    var condition = BuildConditionExpression(rule.When);
                    formula = $"if ({condition}) {formula}";
                    priorConditions.Add(condition);
                }
                else if (priorConditions.Count > 0)
                {
                    // Fallback rule: negate all prior conditions so it only fires
                    // when none of the prior conditional rules matched.
                    var negation = string.Join(" && ", priorConditions.Select(c => $"!({c})"));
                    formula = $"if ({negation}) {formula}";
                }

                rules.Add(new PayRuleDto
                {
                    RuleName = $"ComputeVar_{cv.Name}_{i}",
                    ClassGroupName = groupName,
                    Formula = formula,
                    Priority = priority++
                });
            }
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

        // Handle CSCAP primary/cap
        if (structure.Primary != null) ExtractTranchesRecursive(structure.Primary, tranches);
        if (structure.Cap != null) ExtractTranchesRecursive(structure.Cap, tranches);

        // Handle FIXED primary/overflow
        if (structure.Overflow != null) ExtractTranchesRecursive(structure.Overflow, tranches);

        // Handle FORCE_PAYDOWN forced/support
        if (structure.Forced != null) ExtractTranchesRecursive(structure.Forced, tranches);
        if (structure.Support != null) ExtractTranchesRecursive(structure.Support, tranches);
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