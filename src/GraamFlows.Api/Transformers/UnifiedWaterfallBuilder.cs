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
        List<TrancheDto> tranches,
        List<ExchangeShareDto>? exchangeShares = null)
    {
        // Find WRITEDOWN step and extract subordination order
        var writedownStep = waterfall.Steps.FirstOrDefault(s =>
            s.Type.Equals("WRITEDOWN", StringComparison.OrdinalIgnoreCase));

        var writedownOrder = writedownStep?.Structure != null
            ? ExtractTrancheOrder(writedownStep.Structure)
            : new List<string>();

        // Map each exchange (combinable/MACR) class to the comma-joined list of its
        // component tranche names. PayExchangeables/ClassesByNameOrTag expect this
        // "A1A,A1B" format (split on ',') on the exchange class's DealStructure.
        var exchangeComponents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (exchangeShares != null)
            foreach (var es in exchangeShares)
                if (!string.IsNullOrEmpty(es.ExchangeTranche) && es.Shares.Count > 0)
                    exchangeComponents[es.ExchangeTranche] =
                        string.Join(",", es.Shares.Select(s => s.TrancheName));

        // Create DealStructure for each tranche
        return tranches.Select((t, idx) =>
        {
            var writedownIdx = writedownOrder.IndexOf(t.TrancheName);
            exchangeComponents.TryGetValue(t.TrancheName, out var exchangableTranche);
            var payFrom = PayFromForTranche(t);
            // #3691: a class-cut IO (PayFrom=Notional) amortizes off its NotionalReference
            // bond — expose it as the ExchangableTranche so PayNotionalClasses reads that
            // bond's principal. (Carried on the tranche, NOT via ExchangeShares, so the
            // exchange-share pay-block doesn't also pay it — that double-counts.)
            if (payFrom == "Notional" && string.IsNullOrEmpty(exchangableTranche))
                exchangableTranche = t.NotionalReference;
            return new DealStructureDto
            {
                ClassGroupName = t.TrancheName,
                // Higher order = more junior. First in writedown list = most junior
                SubordinationOrder = writedownIdx >= 0
                    ? writedownOrder.Count - writedownIdx
                    : idx,
                PayFrom = payFrom,
                GroupNum = "1",
                ExchangableTranche = exchangableTranche
            };
        }).ToList();
    }

    /// <summary>
    ///     Pick the DealStructure pay source for a tranche. Tranches default to
    ///     "Sequential" (the unified-waterfall steps drive their order), but an
    ///     excess-servicing IO strip (Class A-IO-S) must pay from
    ///     "ExcessServicing" so it draws its strip from the servicing fee —
    ///     capped at the fee collected and WITHOUT reducing interest to the
    ///     offered classes (see <c>TrancheAllocator</c>). It is identified as a
    ///     non-residual IO strip that is either explicitly a Reference tranche
    ///     or carries an "IOS" class name; the excess-SPREAD strip (Class XS)
    ///     is a ResidualInterest IO and is deliberately excluded so it keeps
    ///     the interest sweep. An <c>Exchanged</c> (combinable / MACR) class pays
    ///     from "Exchange" so the exchange overlay (<c>PayExchangeables</c>) derives
    ///     its cashflow from its component tranches instead of leaving it flat at
    ///     its issuance balance.
    /// </summary>
    private static string PayFromForTranche(TrancheDto t)
    {
        var isIo = string.Equals(t.CashflowType, "IO", StringComparison.OrdinalIgnoreCase);
        var isResidualInterest =
            string.Equals(t.CouponType, "ResidualInterest", StringComparison.OrdinalIgnoreCase);
        var name = (t.TrancheName ?? "").ToUpperInvariant().Replace("-", "");
        var isReference =
            string.Equals(t.TrancheType, "Reference", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(t.TrancheType, "Exchanged", StringComparison.OrdinalIgnoreCase))
            return "Exchange";

        if (isIo && !isResidualInterest && (isReference || name.Contains("IOS")))
            return "ExcessServicing";

        // #3691: a class-cut IO strip carries a NotionalReference — the funded bond its
        // notional is cut off (A-1AX -> A-1A). Pay from Notional so PayNotionalClasses
        // amortizes it off that bond (via the ExchangableTranche set from NotionalReference
        // above) instead of the pool fallback. Kept BELOW the IOS/Reference case so an
        // A-IO-S excess-servicing pool strip is unaffected.
        if (isIo && !isResidualInterest && !string.IsNullOrEmpty(t.NotionalReference))
            return "Notional";

        return "Sequential";
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
    ///
    ///     Fallback rules (no <c>When</c>) are guarded with the negation of all prior
    ///     conditions so they only fire when none of the prior conditional rules
    ///     matched. Without this guard the fallback's unconditional formula would
    ///     execute every period and overwrite whatever the prior conditional rules
    ///     just set (issue #9, exposed by graam-harmony#1054 where the sequential
    ///     fallback overwrote the conditional PRORATA seniors). Mirrors the same
    ///     negation pattern used by <see cref="BuildComputedVariableRules"/>.
    /// </summary>
    private static List<PayRuleDto> BuildMultiBranchRules(
        List<WaterfallRuleDto> branchRules,
        string prefix,
        string setStructFunc,
        string groupName,
        ref int priority)
    {
        var rules = new List<PayRuleDto>();
        var priorConditions = new List<string>();

        for (var i = 0; i < branchRules.Count; i++)
        {
            var branch = branchRules[i];
            var structDsl = WaterfallBuilder.BuildStructureDsl(branch.Structure);
            var formula = $"{setStructFunc}({structDsl})";

            if (branch.When != null)
            {
                var condition = BuildConditionExpression(branch.When);
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