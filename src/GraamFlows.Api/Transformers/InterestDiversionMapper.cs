using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Maps the interest-diversion request DTO onto <see cref="InterestDiversionConfig" />. Shared by
///     the API controller and the CLI runner. The cure buys collateral, so the deal must reinvest:
///     without a reinvestment config there is nothing to buy with the diverted cash, and accepting
///     the test would take the cash out of the waterfall and put it nowhere.
/// </summary>
public static class InterestDiversionMapper
{
    public static InterestDiversionConfig? Map(InterestDiversionDto? dto, ReinvestmentConfig? reinvestment,
        string dealName)
    {
        if (dto == null)
            return null;

        string Fail(string what) => throw new InvalidOperationException($"Deal {dealName}: interestDiversion {what}");

        if (reinvestment == null || reinvestment.Templates.Count == 0)
            Fail("requires a reinvestment config with templates: its cure buys collateral");
        if (dto.Tranches == null || dto.Tranches.Count == 0 || dto.Tranches.Any(string.IsNullOrWhiteSpace))
            Fail("requires 'tranches' (the note classes whose balance is the ratio's denominator)");
        if (!(dto.TriggerPct > 0))
            Fail($"triggerPct must be a positive percent (e.g. 103.75), got {dto.TriggerPct}");
        if (dto.MaxPctOfInterest is <= 0 or > 100)
            Fail($"maxPctOfInterest must be a percent in (0, 100], got {dto.MaxPctOfInterest}");
        if (dto.PurchasePricePct is <= 0)
            Fail($"purchasePricePct must be positive, got {dto.PurchasePricePct}");

        var templates = reinvestment!.Templates;
        var totalAlloc = templates.Sum(t => t.AllocationPct);
        var weightedPrice = totalAlloc > 0
            ? templates.Sum(t => t.EffectivePrice * t.AllocationPct) / totalAlloc
            : 100.0;

        return new InterestDiversionConfig
        {
            Tranches = dto.Tranches!.ToList(),
            TriggerPct = dto.TriggerPct,
            MaxPctOfInterest = dto.MaxPctOfInterest ?? 100.0,
            EndDate = dto.EndDate ?? reinvestment.ReinvestEndDate,
            PurchasePricePct = dto.PurchasePricePct ?? weightedPrice
        };
    }
}
