using System.Text.Json;
using GraamFlows.Api.Models;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Transforms structured waterfall definitions into PayRule DSL strings.
///     This enables LLMs to generate structured JSON instead of DSL formulas.
/// </summary>
public static class WaterfallBuilder
{
    /// <summary>
    ///     Generates PayRule DTOs from a structured waterfall definition
    /// </summary>
    public static List<PayRuleDto> BuildPayRules(WaterfallStructureDto waterfall, string groupName = "GROUP_1")
    {
        var rules = new List<PayRuleDto>();
        var priority = 0;

        // Process scheduled principal
        if (waterfall.ScheduledPrincipal != null)
            rules.AddRange(BuildPrincipalRules(
                waterfall.ScheduledPrincipal,
                "Sched",
                "SET_SCHED_STRUCT",
                groupName,
                ref priority));

        // Process unscheduled principal
        var unschedPrincipal = ResolveWaterfallPrincipal(waterfall.UnscheduledPrincipal, waterfall.ScheduledPrincipal);
        if (unschedPrincipal != null)
            rules.AddRange(BuildPrincipalRules(
                unschedPrincipal,
                "Prepay",
                "SET_PREPAY_STRUCT",
                groupName,
                ref priority));

        // Process recovery principal
        var recovPrincipal = ResolveWaterfallPrincipal(waterfall.RecoveryPrincipal, waterfall.ScheduledPrincipal);
        if (recovPrincipal != null)
            rules.AddRange(BuildPrincipalRules(
                recovPrincipal,
                "Recov",
                "SET_RECOV_STRUCT",
                groupName,
                ref priority));

        // Process reserve principal
        if (waterfall.ReservePrincipal != null)
        {
            var structDsl = BuildStructureDsl(waterfall.ReservePrincipal);
            rules.Add(new PayRuleDto
            {
                RuleName = "ReserveStruct",
                ClassGroupName = groupName,
                Formula = $"SET_RESERVE_STRUCT({structDsl})",
                Priority = priority++
            });
        }

        return rules;
    }

    private static WaterfallPrincipalDto? ResolveWaterfallPrincipal(object? principal, WaterfallPrincipalDto? scheduled)
    {
        if (principal == null) return null;

        // Check if it's "same" (use scheduled principal structure)
        if (principal is string s && s.Equals("same", StringComparison.OrdinalIgnoreCase)) return scheduled;

        // Check if it's a JsonElement (from JSON deserialization)
        if (principal is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String &&
                element.GetString()?.Equals("same", StringComparison.OrdinalIgnoreCase) == true)
                return scheduled;

            // Deserialize as WaterfallPrincipalDto
            return JsonSerializer.Deserialize<WaterfallPrincipalDto>(element.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        // Already the right type
        if (principal is WaterfallPrincipalDto dto) return dto;

        return null;
    }

    private static List<PayRuleDto> BuildPrincipalRules(
        WaterfallPrincipalDto principal,
        string prefix,
        string setStructFunc,
        string groupName,
        ref int priority)
    {
        var rules = new List<PayRuleDto>();

        // If there's a trigger condition, generate conditional rules
        if (principal.OnTriggerFail != null && principal.Default != null)
        {
            var triggerNames = string.Join(",", principal.OnTriggerFail.Triggers);
            var passedDsl = BuildStructureDsl(principal.Default);
            var failedDsl = BuildStructureDsl(principal.OnTriggerFail.Structure!);

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
        else if (principal.Default != null)
        {
            // Simple unconditional structure
            var structDsl = BuildStructureDsl(principal.Default);
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
    ///     Converts a PayableStructureDto to DSL string format
    /// </summary>
    public static string BuildStructureDsl(PayableStructureDto structure)
    {
        switch (structure.Type.ToUpperInvariant())
        {
            case "SEQ":
                return BuildSeqDsl(structure);
            case "PRORATA":
                return BuildProrataDsl(structure);
            case "SINGLE":
                return BuildSingleDsl(structure);
            case "SHIFTI":
                return BuildShiftiDsl(structure);
            case "ACCRETE":
                return BuildAccreteDsl(structure);
            case "CSCAP":
                return BuildCscapDsl(structure);
            case "FIXED":
                return BuildFixedDsl(structure);
            case "FORCE_PAYDOWN":
                return BuildForcePaydownDsl(structure);
            default:
                throw new ArgumentException($"Unknown structure type: {structure.Type}");
        }
    }

    private static string BuildSeqDsl(PayableStructureDto structure)
    {
        var children = new List<string>();

        if (structure.Children != null)
            children.AddRange(structure.Children.Select(BuildStructureDsl));
        else if (structure.Tranches != null)
            // Shorthand: list of tranches becomes SINGLE for each
            children.AddRange(structure.Tranches.Select(t => $"SINGLE('{t}')"));

        return $"SEQ({string.Join(", ", children)})";
    }

    private static string BuildProrataDsl(PayableStructureDto structure)
    {
        // Check for shorthand tranches list
        if (structure.Tranches != null && structure.Tranches.Count > 0)
        {
            var trancheList = string.Join("','", structure.Tranches);
            return $"PRORATA('{trancheList}')";
        }

        // Full children structure
        if (structure.Children != null)
        {
            var children = structure.Children.Select(BuildStructureDsl);
            return $"PRORATA({string.Join(", ", children)})";
        }

        return "PRORATA()";
    }

    private static string BuildSingleDsl(PayableStructureDto structure)
    {
        var tranche = structure.Tranche ?? structure.Tranches?.FirstOrDefault() ?? "";
        return $"SINGLE('{tranche}')";
    }

    private static string BuildShiftiDsl(PayableStructureDto structure)
    {
        string shiftParam;
        if (!string.IsNullOrEmpty(structure.ShiftVariable))
            shiftParam = $"'{structure.ShiftVariable}'";
        else
            shiftParam = structure.ShiftPercent?.ToString("0.####") ?? "0";

        var seniors = structure.Seniors != null ? BuildStructureDsl(structure.Seniors) : "SINGLE('')";
        var subs = structure.Subordinates != null ? BuildStructureDsl(structure.Subordinates) : "SINGLE('')";

        return $"SHIFTI({shiftParam}, {seniors}, {subs})";
    }

    /// <summary>
    ///     Builds ACCRETE DSL for OC tranche balance accretion (Auto ABS excess step)
    /// </summary>
    private static string BuildAccreteDsl(PayableStructureDto structure)
    {
        var tranche = structure.Tranche ?? structure.Tranches?.FirstOrDefault() ?? "";
        return $"ACCRETE('{tranche}')";
    }

    /// <summary>
    ///     Builds CSCAP DSL: CSCAP('variable', primary, cap) or CSCAP(0.055, primary, cap)
    /// </summary>
    private static string BuildCscapDsl(PayableStructureDto structure)
    {
        string capParam;
        if (!string.IsNullOrEmpty(structure.CapVariable))
            capParam = $"'{structure.CapVariable}'";
        else
            capParam = structure.CapPercent?.ToString("0.####") ?? "0";

        var primary = structure.Primary != null ? BuildStructureDsl(structure.Primary) : "SINGLE('')";
        var cap = structure.Cap != null ? BuildStructureDsl(structure.Cap) : "SINGLE('')";

        return $"CSCAP({capParam}, {primary}, {cap})";
    }

    /// <summary>
    ///     Builds FIXED DSL: FIXED('variable', primary, overflow) or FIXED(12345, primary, overflow)
    /// </summary>
    private static string BuildFixedDsl(PayableStructureDto structure)
    {
        string fixedParam;
        if (!string.IsNullOrEmpty(structure.FixedVariable))
            fixedParam = $"'{structure.FixedVariable}'";
        else
            fixedParam = structure.FixedAmount?.ToString("0.####") ?? "0";

        var primary = structure.Primary != null ? BuildStructureDsl(structure.Primary) : "SINGLE('')";
        var overflow = structure.Overflow != null ? BuildStructureDsl(structure.Overflow) : "SINGLE('')";

        return $"FIXED({fixedParam}, {primary}, {overflow})";
    }

    /// <summary>
    ///     Builds FORCE_PAYDOWN DSL: FORCE_PAYDOWN(forced, support)
    /// </summary>
    private static string BuildForcePaydownDsl(PayableStructureDto structure)
    {
        var forced = structure.Forced != null ? BuildStructureDsl(structure.Forced) : "SINGLE('')";
        var support = structure.Support != null ? BuildStructureDsl(structure.Support) : "SINGLE('')";

        return $"FORCE_PAYDOWN({forced}, {support})";
    }
}