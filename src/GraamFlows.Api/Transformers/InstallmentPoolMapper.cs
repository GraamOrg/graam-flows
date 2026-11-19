using GraamFlows.Api.Models;
using GraamFlows.Revolving;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Turns a <see cref="RevolvingPoolRequest" /> into an installment pool run and its result into the
///     response. Percents become fractions here, and anything the engine does not know is refused by name.
/// </summary>
public static class InstallmentPoolMapper
{
    /// <summary>
    ///     On <c>/api/revolving</c>: a pool that is not the card pool is refused, since no series
    ///     allocates on it yet. An absent pool is the card pool and changes nothing.
    /// </summary>
    public static void RequireCardPool(RevolvingPoolDto? pool)
    {
        if (pool == null)
            return;

        if (PoolModelParser.Parse(pool.Model) == PoolModel.Installment)
            throw new RevolvingConfigException(
                "pool.model INSTALLMENT runs on /api/revolving/pool: no series allocates on an installment pool yet.");

        if (pool.Cohorts != null || pool.DiscountRates != null || pool.Drivers != null || pool.ActualPeriods != null
            || pool.Purchases != null)
            throw new RevolvingConfigException(
                "pool.model CARD states no cohorts, discountRates, drivers, actualPeriods or purchases: a card "
                + "pool states poolBalance, drivers and actualPeriods on the request.");
    }

    public static InstallmentPoolRun ToRun(RevolvingPoolRequest request)
    {
        var pool = request.Pool ?? throw new RevolvingConfigException("The request states no pool.");
        if (PoolModelParser.Parse(pool.Model) != PoolModel.Installment)
            throw new RevolvingConfigException(
                "/api/revolving/pool runs an installment pool: state pool.model INSTALLMENT. A card pool runs on "
                + "/api/revolving.");

        return ToPoolRun(pool, request.StatementDate, request.Periods);
    }

    /// <summary>An installment pool as a run: the pool route's, and an owner trust's on /api/revolving.</summary>
    public static InstallmentPoolRun ToPoolRun(RevolvingPoolDto pool, DateTime statementDate, int? requestedPeriods)
    {
        var actuals = (pool.ActualPeriods ?? new List<InstallmentActualDto>())
            .Select((a, i) => ToActual($"pool.actualPeriods[{i}]", a)).ToList();
        var periods = requestedPeriods ?? actuals.Count;
        return new InstallmentPoolRun
        {
            StatementDate = statementDate,
            Periods = periods,
            Cohorts = (pool.Cohorts ?? new List<InstallmentCohortDto>())
                .Select((c, i) => ToOpeningCohort($"pool.cohorts[{i}]", c)).ToList(),
            DiscountRates = (pool.DiscountRates ?? new List<double>()).Select(r => r / 100.0).ToList(),
            Drivers = pool.Drivers == null || periods <= actuals.Count ? null : ToDrivers(pool.Drivers),
            ActualPeriods = actuals,
            Purchases = (pool.Purchases ?? new List<InstallmentPurchaseDto>())
                .Select((p, i) => ToPurchase($"pool.purchases[{i}]", p)).ToList()
        };
    }

    public static RevolvingPoolResponse ToResponse(InstallmentPoolResult result)
    {
        return new RevolvingPoolResponse
        {
            Periods = result.Periods.Select(ToDto).ToList(),
            Closing = ToCohortDtos(result.Closing),
            Disclosures = result.Disclosures.ToList()
        };
    }

    public static List<InstallmentCohortDto> ToCohortDtos(IEnumerable<InstallmentCohort> cohorts)
    {
        return cohorts.Select(c => new InstallmentCohortDto
        {
            Name = c.Name,
            Age = c.Age,
            Schedule = c.Schedule.ToArray()
        }).ToList();
    }

    /// <summary>An opening cohort states its schedule: the engine never derives one from a term.</summary>
    private static InstallmentCohort ToOpeningCohort(string name, InstallmentCohortDto dto)
    {
        if (dto.Face != null || dto.Term != null)
            throw new RevolvingConfigException(
                $"{name} states a face or term. An opening cohort states its schedule of remaining payments.");
        return new InstallmentCohort
        {
            Name = dto.Name ?? "",
            Age = dto.Age ?? throw new RevolvingConfigException($"{name} states no age."),
            Schedule = dto.Schedule ?? throw new RevolvingConfigException($"{name} states no schedule.")
        };
    }

    private static InstallmentPurchase ToPurchase(string name, InstallmentPurchaseDto dto)
    {
        IReadOnlyList<double> schedule;
        if (dto.Schedule != null)
        {
            if (dto.Face != null || dto.Term != null)
                throw new RevolvingConfigException($"{name} must state a schedule or a face and term, not both.");
            schedule = dto.Schedule;
        }
        else
        {
            if (dto.Face is not { } face || dto.Term is not { } term)
                throw new RevolvingConfigException($"{name} must state a schedule, or a face and a term.");
            if (term < 1)
                throw new RevolvingConfigException($"{name}.term is {term}. It must be at least 1 month.");
            schedule = InstallmentCohort.LevelSchedule(face, term);
        }

        return new InstallmentPurchase
        {
            Cohort = new InstallmentCohort { Name = dto.Name ?? "", Age = dto.Age ?? 0, Schedule = schedule },
            Price = dto.Price == null ? null : ToPrice($"{name}.price", dto.Price),
            Cash = dto.Cash,
            Period = dto.Period
        };
    }

    internal static InstallmentPrice ToPrice(string name, InstallmentPriceDto dto)
    {
        if ((dto.PercentOfFace == null) == (dto.PvRate == null))
            throw new RevolvingConfigException($"{name} must state percentOfFace or pvRate, not both.");
        return new InstallmentPrice { PerFace = dto.PercentOfFace / 100.0, PvRate = dto.PvRate / 100.0 };
    }

    private static InstallmentActual ToActual(string name, InstallmentActualDto dto)
    {
        return new InstallmentActual
        {
            BeginningFace = dto.BeginningFace,
            ScheduledCollections = Stated(name, "scheduledCollections", dto.ScheduledCollections),
            PrepaidCollections = Stated(name, "prepaidCollections", dto.PrepaidCollections),
            UpgradeCollections = dto.UpgradeCollections ?? 0.0,
            WriteOffs = Stated(name, "writeOffs", dto.WriteOffs),
            Repurchases = dto.Repurchases ?? 0.0,
            RepurchaseCash = dto.RepurchaseCash,
            RepurchasePrice = dto.RepurchasePrice == null ? null : ToPrice($"{name}.repurchasePrice", dto.RepurchasePrice),
            Additions = (dto.Additions ?? new List<InstallmentPurchaseDto>())
                .Select((a, i) => ToPurchase($"{name}.additions[{i}]", a)).ToList(),
            EndingFace = dto.EndingFace
        };
    }

    private static double Stated(string name, string field, double? value)
    {
        return value ?? throw new RevolvingConfigException($"{name} states no {field}.");
    }

    private static InstallmentDrivers ToDrivers(InstallmentDriversDto dto)
    {
        return new InstallmentDrivers
        {
            Prepayment = Curve("prepaymentRate", dto.PrepaymentRate)
                         ?? throw new RevolvingConfigException("Forecast drivers must state prepaymentRate."),
            Upgrade = Curve("upgradeRate", dto.UpgradeRate),
            WriteOff = Curve("writeOffRate", dto.WriteOffRate)
                       ?? throw new RevolvingConfigException("Forecast drivers must state writeOffRate."),
            RepurchaseAmounts = dto.RepurchaseAmounts ?? Array.Empty<double>(),
            RepurchasePrice = dto.RepurchasePrice == null ? null : ToPrice("drivers.repurchasePrice", dto.RepurchasePrice),
            Reinvest = dto.Reinvest == null ? null : ToReinvest(dto.Reinvest)
        };
    }

    private static InstallmentReinvest ToReinvest(InstallmentReinvestDto dto)
    {
        return new InstallmentReinvest
        {
            Share = (dto.Share ?? throw new RevolvingConfigException("drivers.reinvest states no share.")) / 100.0,
            Price = ToPrice("drivers.reinvest.price",
                dto.Price ?? throw new RevolvingConfigException("drivers.reinvest states no price.")),
            Terms = (dto.Terms ?? throw new RevolvingConfigException("drivers.reinvest states no terms."))
                .Select((t, i) => new InstallmentTerm(
                    t.Term ?? throw new RevolvingConfigException($"drivers.reinvest.terms[{i}] states no term."),
                    (t.Weight ?? throw new RevolvingConfigException($"drivers.reinvest.terms[{i}] states no weight."))
                    / 100.0, t.Age ?? 0))
                .ToList()
        };
    }

    private static InstallmentCurve? Curve(string name, RevolvingDriverDto? dto)
    {
        if (dto == null)
            return null;
        if (dto.Values.Length == 0)
            throw new RevolvingConfigException($"Driver {name} has no values.");
        return new InstallmentCurve(dto.Values.Select(v => v / 100.0).ToList(), dto.Annual);
    }

    internal static InstallmentPoolPeriodDto ToDto(InstallmentPoolPeriod p)
    {
        return new InstallmentPoolPeriodDto
        {
            Period = p.Period,
            PeriodEnd = p.PeriodEnd,
            Replayed = p.Replayed,
            BeginningFace = p.BeginningFace,
            ScheduledDue = p.ScheduledDue,
            ScheduledCollections = p.ScheduledCollections,
            PrepaidCollections = p.PrepaidCollections,
            UpgradeCollections = p.UpgradeCollections,
            Collections = p.Collections,
            WriteOffs = p.WriteOffs,
            Repurchases = p.Repurchases,
            RepurchaseCash = p.RepurchaseCash,
            AdditionsFace = p.AdditionsFace,
            AdditionsCash = p.AdditionsCash,
            EndingFace = p.EndingFace,
            Cohorts = p.Cohorts,
            RollBreak = p.RollBreak,
            LedgerBreak = p.LedgerBreak,
            Valuations = p.Valuations.Select(v => new InstallmentValuationDto
            {
                Rate = Math.Round(v.Rate * 100.0, 10),
                Beginning = v.Beginning,
                WriteOffs = v.WriteOffs,
                Repurchases = v.Repurchases,
                PrepaidCollections = v.PrepaidCollections,
                UpgradeCollections = v.UpgradeCollections,
                ScheduledVariance = v.ScheduledVariance,
                Additions = v.Additions,
                Ending = v.Ending
            }).ToList()
        };
    }
}
