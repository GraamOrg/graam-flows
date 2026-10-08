using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Maps the incentive-fee request DTO onto <see cref="IncentiveFeeConfig" />. Shared by the API
///     controller and the CLI runner (mirrors <see cref="CoverageCascadeMapper" />). Validates the
///     shape here; the two names are resolved against the built deal by the waterfall, which fails
///     loudly on an unknown class or expense.
/// </summary>
public static class IncentiveFeeMapper
{
    public static IncentiveFeeConfig? Map(IncentiveFeeDto? dto, string dealName)
    {
        if (dto == null)
            return null;

        string Fail(string what) => throw new InvalidOperationException($"Deal {dealName}: incentiveFee {what}");

        if (string.IsNullOrWhiteSpace(dto.ResidualTranche))
            Fail("requires 'residualTranche' (the equity class whose distributions are shared)");
        if (string.IsNullOrWhiteSpace(dto.FeeExpense))
            Fail("requires 'feeExpense' (the declared expense the fee is booked to)");
        if (dto.HurdleIrrPct is < 0 or >= 100 || double.IsNaN(dto.HurdleIrrPct))
            Fail($"hurdleIrrPct must be a percent in [0, 100), got {dto.HurdleIrrPct}");
        if (dto.SharePct is <= 0 or > 100 || double.IsNaN(dto.SharePct))
            Fail($"sharePct must be a percent in (0, 100], got {dto.SharePct}");
        if (!(dto.Investment > 0))
            Fail($"investment must be positive, got {dto.Investment}");
        if (dto.InvestmentDate == default)
            Fail("requires 'investmentDate' (the IRR's time zero)");

        var prior = (dto.PriorDistributions ?? new List<IncentiveFeeDistributionDto>())
            .Select(d =>
            {
                if (d.Date < dto.InvestmentDate)
                    Fail($"prior distribution on {d.Date:yyyy-MM-dd} precedes investmentDate {dto.InvestmentDate:yyyy-MM-dd}");
                return (d.Date, d.Amount);
            })
            .ToList();

        return new IncentiveFeeConfig
        {
            ResidualTranche = dto.ResidualTranche,
            FeeExpense = dto.FeeExpense,
            HurdleIrrPct = dto.HurdleIrrPct,
            SharePct = dto.SharePct,
            Investment = dto.Investment,
            InvestmentDate = dto.InvestmentDate,
            PriorDistributions = prior
        };
    }
}
