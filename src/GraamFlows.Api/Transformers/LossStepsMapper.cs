using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Maps the two step fields that say how excess spread meets losses: the WRITEDOWN step's
///     <see cref="WaterfallStepDto.Size" /> and the EXCESS_TURBO step's
///     <see cref="WaterfallStepDto.Target" />. A value the engine has no meaning for is refused
///     here, not read as the default: a misspelt size would write the period's loss down on top
///     of the principal the turbo already paid for it.
/// </summary>
public static class LossStepsMapper
{
    public const string PeriodLoss = "periodLoss";
    public const string OverPool = "overPool";

    public static WritedownSize MapWritedownSize(UnifiedWaterfallDto? waterfall, string? dealName)
    {
        var steps = waterfall?.Steps ?? new List<WaterfallStepDto>();
        var misplaced = steps.FirstOrDefault(s => s.Size != null && !IsType(s, "WRITEDOWN"));
        if (misplaced != null)
            throw new ArgumentException(
                $"Deal {dealName}: a {misplaced.Type} step states size '{misplaced.Size}'. Only the " +
                "WRITEDOWN step is sized.");

        var sizes = steps.Where(s => IsType(s, "WRITEDOWN")).Select(s => Parse(s.Size, dealName)).Distinct().ToList();
        if (sizes.Count > 1)
            throw new ArgumentException(
                $"Deal {dealName}: its WRITEDOWN steps state different sizes. The write-down is sized one way.");
        return sizes.Count == 1 ? sizes[0] : WritedownSize.PeriodLoss;
    }

    public static bool MapExcessTurboToPool(UnifiedWaterfallDto? waterfall, string? dealName)
    {
        var steps = waterfall?.Steps ?? new List<WaterfallStepDto>();
        var misplaced = steps.FirstOrDefault(s => s.Target != null && !IsType(s, "EXCESS_TURBO"));
        if (misplaced != null)
            throw new ArgumentException(
                $"Deal {dealName}: a {misplaced.Type} step states a target. Only the EXCESS_TURBO step has one.");

        var turbo = steps.Where(s => IsType(s, "EXCESS_TURBO") && s.Target != null).ToList();
        if (turbo.Count == 0)
            return false;
        foreach (var step in turbo)
        {
            if (step.Target != 0)
                throw new ArgumentException(
                    $"Deal {dealName}: EXCESS_TURBO states target {step.Target}. Zero is the only target " +
                    "(pay the notes down to the pool); an overcollateralization target is stated in ocTarget.");
            if (step.OcTarget != null)
                throw new ArgumentException(
                    $"Deal {dealName}: EXCESS_TURBO states both target 0 and an ocTarget. A step pays the " +
                    "notes down to the pool or builds overcollateralization, not both.");
            if (step.Structure != null || step.Default != null || step.Rules is { Count: > 0 } ||
                step.UseStructure != null || step.OnTriggerFail != null)
                throw new ArgumentException(
                    $"Deal {dealName}: EXCESS_TURBO states target 0 and a structure of its own. With a " +
                    "target of zero it pays through the recovery-principal structure as each period's rules " +
                    "leave it, as the built-in excess-spread rule does. Remove the step's structure.");
        }

        return true;
    }

    /// <summary>
    ///     Whether the deal names a WRITEUP step, in its steps or its execution order. The step takes
    ///     no structure and no field: it writes the written-down notes back up, most senior first.
    /// </summary>
    public static bool MapWritesUp(UnifiedWaterfallDto? waterfall, string? dealName)
    {
        var steps = waterfall?.Steps ?? new List<WaterfallStepDto>();
        foreach (var step in steps.Where(s => IsType(s, "WRITEUP")))
            if (step.Structure != null || step.Default != null || step.Rules is { Count: > 0 } ||
                step.UseStructure != null || step.OnTriggerFail != null || step.Size != null ||
                step.Target != null || step.OcTarget != null)
                throw new ArgumentException(
                    $"Deal {dealName}: WRITEUP states a structure or a field. It takes neither: it writes the " +
                    "notes written down earlier back up, most senior first, by what the pool stands above them.");
        return steps.Any(s => IsType(s, "WRITEUP")) ||
               (waterfall?.ExecutionOrder ?? new List<string>()).Any(s =>
                   s.Equals("WRITEUP", StringComparison.OrdinalIgnoreCase));
    }

    private static WritedownSize Parse(string? size, string? dealName)
    {
        if (size == null || size.Equals(PeriodLoss, StringComparison.OrdinalIgnoreCase))
            return WritedownSize.PeriodLoss;
        if (size.Equals(OverPool, StringComparison.OrdinalIgnoreCase))
            return WritedownSize.OverPool;
        throw new ArgumentException(
            $"Deal {dealName}: WRITEDOWN states size '{size}'. Supported: {PeriodLoss} (the period's " +
            $"defaulted principal less recoveries; the default) or {OverPool} (what the funded classes " +
            "stand above the pool by after the steps ahead of it).");
    }

    private static bool IsType(WaterfallStepDto step, string type) =>
        step.Type.Equals(type, StringComparison.OrdinalIgnoreCase);
}
