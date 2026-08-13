using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Maps the reinvestment request DTO onto the domain
///     <see cref="ReinvestmentConfig" />. Shared by the API controller and the
///     CLI runner so the two stay in sync (unlike the OC-config mapping, which is
///     duplicated inline in both). Validates the result before returning.
/// </summary>
public static class ReinvestmentConfigMapper
{
    public static ReinvestmentConfig? Map(ReinvestmentDto? dto, string dealName)
    {
        if (dto == null)
            return null;

        var eligible = EligibleProceeds.None;
        if (dto.ReinvestScheduledPrincipal ?? true) eligible |= EligibleProceeds.ScheduledPrincipal;
        if (dto.ReinvestPrepayments ?? true) eligible |= EligibleProceeds.Prepayments;
        if (dto.ReinvestRecoveries ?? false) eligible |= EligibleProceeds.Recoveries;

        var templates = (dto.Templates ?? new List<ReinvestTemplateDto>())
            .Select(t => new ReinvestTemplate
            {
                AllocationPct = t.AllocationPct,
                Price = t.Price,
                IsSynthetic = t.IsSynthetic,
                InterestRateType = t.InterestRateType,
                AmortizationType = t.AmortizationType,
                CouponRate = t.CouponRate,
                IndexName = t.IndexName,
                IndexMargin = t.IndexMargin,
                TermMonths = t.TermMonths,
                ServiceFee = t.ServiceFee
            })
            .ToList();

        var config = new ReinvestmentConfig
        {
            ReinvestEndDate = dto.ReinvestEndDate,
            ReinvestStartDate = dto.ReinvestStartDate,
            Target = dto.Target,
            TargetSchedule = dto.TargetSchedule,
            Holdback = dto.Holdback,
            EligibleProceeds = eligible,
            Templates = templates
        };

        config.Validate(dealName);
        return config;
    }
}
