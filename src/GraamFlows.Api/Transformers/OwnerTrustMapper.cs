using GraamFlows.Api.Models;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Revolving;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Turns a <see cref="RevolvingRequest" /> with <c>program.ownerTrust</c> into an owner trust run, and
///     its result into the response. Percents become fractions here, and a card program's fields are
///     refused by name: an owner trust has none of them.
/// </summary>
public static class OwnerTrustMapper
{
    public static bool IsOwnerTrust(RevolvingRequest request)
    {
        return request.Program.OwnerTrust != null;
    }

    public static OwnerTrustRun ToRun(RevolvingRequest request)
    {
        var dto = request.Program.OwnerTrust!;
        RefuseCardFields(request);

        var pool = request.Pool ?? throw new RevolvingConfigException(
            "An owner trust states its pool: pool.model INSTALLMENT with its cohorts.");
        if (PoolModelParser.Parse(pool.Model) != PoolModel.Installment)
            throw new RevolvingConfigException("An owner trust runs on an installment pool: state pool.model INSTALLMENT.");

        if (request.Series.Count != 1)
            throw new RevolvingConfigException(
                $"An owner trust is one series of notes, and the request states {request.Series.Count}.");
        var series = request.Series[0];
        var name = series.Name ?? throw new RevolvingConfigException("The series states no name.");

        return new OwnerTrustRun
        {
            Name = name,
            Pool = InstallmentPoolMapper.ToPoolRun(pool, request.StatementDate, request.Periods),
            Trust = ToConfig(dto),
            Classes = series.Classes.Select(c => RevolvingMapper.ToClass(name, c)).ToList(),
            State = ToState(dto.State),
            ActualPeriods = (dto.ActualPeriods ?? new List<OwnerTrustActualDto>()).Select(ToActual).ToList(),
            DistributionDay = request.DistributionDay ?? 15
        };
    }

    /// <summary>The indices the notes pay on.</summary>
    public static IReadOnlyList<MarketDataInstEnum> FloatingIndices(OwnerTrustRun run)
    {
        return run.Classes.Where(c => c.Index != null).Select(c => c.Index!.Value).Distinct().ToList();
    }

    private static void RefuseCardFields(RevolvingRequest request)
    {
        var program = request.Program;
        var stated = new List<string>();
        if (program.RecoveryBucket != null) stated.Add("program.recoveryBucket");
        if (program.PoolMeasure != null) stated.Add("program.poolMeasure");
        if (program.PrincipalFreeze != null) stated.Add("program.principalFreeze");
        if (program.ActionLag != null) stated.Add("program.actionLag");
        if (program.GroupSteps != null) stated.Add("program.groupSteps");
        if (program.FinanceSteps.Count > 0) stated.Add("program.financeSteps");
        if (program.PrincipalSteps.Count > 0) stated.Add("program.principalSteps");
        if (program.Triggers != null) stated.Add("program.triggers");
        if (program.Delinked != null) stated.Add("program.delinked");
        if (request.PoolBalance != null) stated.Add("poolBalance");
        if (request.Drivers != null) stated.Add("drivers");
        if (request.ActualPeriods != null) stated.Add("actualPeriods");
        if (request.FundingEarningsRate != null) stated.Add("fundingEarningsRate");
        if (stated.Count > 0)
            throw new RevolvingConfigException(
                $"An owner trust states its rules in program.ownerTrust and its pool in pool, so it takes no "
                + $"{string.Join(", ", stated)}.");
    }

    private static OwnerTrustConfig ToConfig(OwnerTrustDto dto)
    {
        var oc = dto.Oc ?? throw new RevolvingConfigException("ownerTrust states no oc.");
        var tests = dto.Tests ?? new OwnerTrustTestsDto();
        return new OwnerTrustConfig
        {
            DiscountRate = (dto.DiscountRate ?? throw new RevolvingConfigException("ownerTrust states no discountRate."))
                           / 100.0,
            Tiers = (dto.Tiers ?? throw new RevolvingConfigException("ownerTrust states no tiers."))
                .Select((t, i) => new OwnerTrustTier(
                    t.Name ?? throw new RevolvingConfigException($"ownerTrust.tiers[{i}] states no name."),
                    t.Classes ?? throw new RevolvingConfigException($"ownerTrust.tiers[{i}] states no classes."),
                    t.FinalMaturity))
                .ToList(),
            Steps = (dto.Steps ?? throw new RevolvingConfigException("ownerTrust states no steps."))
                .Select(s => new OwnerTrustStep(OwnerTrustVerbs.Parse(s.Verb), s.Tier))
                .ToList(),
            RevolvingEnd = dto.RevolvingEnd,
            ServicingRate = (dto.ServicingRate ?? 0.0) / 100.0,
            MonthlyFees = dto.MonthlyFees ?? 0.0,
            Oc = new OwnerTrustOc
            {
                RevolvingPct = Pct("oc.revolvingPct", oc.RevolvingPct),
                RevolvingFailingPct = Pct("oc.revolvingFailingPct", oc.RevolvingFailingPct),
                AmortizingPct = Pct("oc.amortizingPct", oc.AmortizingPct),
                AmortizingFailingPct = Pct("oc.amortizingFailingPct", oc.AmortizingFailingPct),
                Floor = oc.Floor ?? 0.0
            },
            RequiredReserve = dto.RequiredReserve ?? 0.0,
            Tests = new OwnerTrustTests
            {
                WriteOffLimit = tests.WriteOffLimit / 100.0,
                WriteOffWindow = tests.WriteOffWindow ?? 3,
                WriteOffMultiplier = tests.WriteOffMultiplier ?? 4.0,
                DelinquencyLimit = tests.DelinquencyLimit / 100.0,
                DelinquencyWindow = tests.DelinquencyWindow ?? 3,
                ParityLimit = tests.ParityLimit / 100.0,
                OcShortfall = tests.OcShortfall ?? false,
                OcGraceMonths = tests.OcGraceMonths ?? 3,
                RevolvingShortfalls = tests.RevolvingShortfalls ?? false
            },
            CleanUp = dto.CleanUp == null ? null : ToCleanUp(dto.CleanUp),
            AcquisitionPrice = dto.Acquisition?.Price == null
                ? dto.Acquisition == null
                    ? null
                    : throw new RevolvingConfigException("ownerTrust.acquisition states no price.")
                : InstallmentPoolMapper.ToPrice("ownerTrust.acquisition.price", dto.Acquisition.Price),
            AcquisitionTerms = (dto.Acquisition?.Terms ?? new List<InstallmentTermDto>())
                .Select((t, i) => new InstallmentTerm(
                    t.Term ?? throw new RevolvingConfigException($"ownerTrust.acquisition.terms[{i}] states no term."),
                    (t.Weight ?? throw new RevolvingConfigException(
                        $"ownerTrust.acquisition.terms[{i}] states no weight.")) / 100.0, t.Age ?? 0))
                .ToList(),
            FloorTestsPass = dto.FloorTestsPass ?? new List<bool>(),
            Delinquency = (dto.Delinquency ?? new List<double>()).Select(d => d / 100.0).ToList(),
            ReserveEarningsRate = (dto.ReserveEarningsRate ?? 0.0) / 100.0,
            Calendar = dto.Calendar?.Trim().ToUpperInvariant() switch
            {
                null or "SETTLEMENT" => OwnerTrustCalendar.Settlement,
                "FEDERAL_RESERVE" => OwnerTrustCalendar.FederalReserve,
                _ => throw new RevolvingConfigException(
                    $"Unknown ownerTrust.calendar '{dto.Calendar}'. Supported: SETTLEMENT and FEDERAL_RESERVE.")
            }
        };
    }

    private static OwnerTrustCleanUp ToCleanUp(OwnerTrustCleanUpDto dto)
    {
        return new OwnerTrustCleanUp
        {
            PoolFactor = Pct("cleanUp.poolFactor", dto.PoolFactor),
            ClosingPoolBalance = dto.ClosingPoolBalance
                                 ?? throw new RevolvingConfigException("ownerTrust.cleanUp states no closingPoolBalance."),
            PriceBasis = dto.PriceBasis?.Trim().ToUpperInvariant() switch
            {
                null or "POOL_FACE" => CleanUpPriceBasis.PoolFace,
                "NOTE_BALANCE" => CleanUpPriceBasis.NoteBalance,
                _ => throw new RevolvingConfigException(
                    $"Unknown cleanUp.priceBasis '{dto.PriceBasis}'. Supported: POOL_FACE and NOTE_BALANCE.")
            },
            Price = (dto.Price ?? 100.0) / 100.0,
            DelayMonths = dto.DelayMonths ?? 0,
            Date = dto.Date
        };
    }

    private static OwnerTrustState ToState(OwnerTrustStateDto? dto)
    {
        if (dto == null)
            return new OwnerTrustState();
        return new OwnerTrustState
        {
            Phase = dto.Phase == null ? SeriesPhase.Revolving : PhaseNames.Parse(dto.Phase),
            Event = dto.Event,
            Reserve = dto.Reserve ?? 0.0,
            AcquisitionAccount = dto.AcquisitionAccount ?? 0.0,
            NegativeCarry = dto.NegativeCarry ?? 0.0,
            AdjustedPool = dto.AdjustedPool,
            WriteOffHistory = (dto.WriteOffHistory ?? new List<double>()).Select(v => v / 100.0).ToList(),
            DelinquencyHistory = (dto.DelinquencyHistory ?? new List<double>()).Select(v => v / 100.0).ToList(),
            OcPctHistory = (dto.OcPctHistory ?? new List<double>()).Select(v => v / 100.0).ToList(),
            CleanUpEligibleMonths = dto.CleanUpEligibleMonths ?? 0
        };
    }

    private static OwnerTrustActual ToActual(OwnerTrustActualDto dto)
    {
        return new OwnerTrustActual
        {
            Fees = dto.Fees,
            FloorTestsPass = dto.FloorTestsPass,
            WriteOffRatio = dto.WriteOffRatio / 100.0,
            DelinquencyRatio = dto.DelinquencyRatio / 100.0,
            Delinquency = dto.Delinquency / 100.0,
            OtherFunds = dto.OtherFunds ?? 0.0,
            Event = dto.Event,
            CleanUpDeposit = dto.CleanUpDeposit,
            AdjustedPool = dto.AdjustedPool
        };
    }

    private static double Pct(string name, double? pct)
    {
        return (pct ?? throw new RevolvingConfigException($"ownerTrust.{name} is not stated.")) / 100.0;
    }

    public static RevolvingResponse ToResponse(OwnerTrustRun run, OwnerTrustResult result)
    {
        var response = new RevolvingResponse
        {
            Disclosures = result.Disclosures.ToList(),
            OwnerTrust = new OwnerTrustResultDto
            {
                Periods = result.Periods.Select(ToDto).ToList(),
                PhasePath = result.PhasePath.Select(m => new OwnerTrustPhaseMoveDto
                {
                    Phase = PhaseNames.ToWire(m.Phase),
                    Period = m.Period,
                    DistributionDate = m.DistributionDate,
                    Event = m.Event
                }).ToList(),
                Ended = result.Ended,
                Closing = new OwnerTrustClosingDto
                {
                    State = result.Closing == null ? null : ToStateDto(result.Closing),
                    Classes = result.ClosingClasses.ToDictionary(c => c.Key, c => new RevolvingClassClosingDto
                    {
                        Balance = c.Balance,
                        OriginalBalance = run.Classes.First(k => k.Key == c.Key) is var k2
                            ? k2.OriginalBalance ?? k2.Balance
                            : c.Balance,
                        InterestShortfall = c.InterestShortfall
                    }),
                    Pool = InstallmentPoolMapper.ToCohortDtos(result.ClosingPool)
                }
            }
        };

        // The classes in the tranche output shape, keyed "series/class", as a card run's are.
        foreach (var config in run.Classes)
        {
            var original = config.OriginalBalance ?? config.Balance;
            response.TrancheCashflows[$"{run.Name}/{config.Key}"] = result.Periods.Select(p =>
            {
                var c = p.Classes.First(x => x.Key == config.Key);
                return new TrancheCashflowDto
                {
                    Period = p.Period,
                    CashflowDate = p.DistributionDate,
                    BeginBalance = c.BeginBalance,
                    Balance = c.EndBalance,
                    Factor = original > 0.0 ? c.EndBalance / original : 0.0,
                    ScheduledPrincipal = c.PrincipalPaid,
                    Interest = c.InterestPaid,
                    Coupon = c.CouponPct,
                    EffectiveCoupon = c.CouponPct,
                    InterestShortfall = Math.Max(0.0, c.InterestAccrued - c.InterestPaid),
                    AccumInterestShortfall = c.InterestShortfall,
                    InterestShortfallPayback = Math.Max(0.0, c.InterestPaid - c.InterestAccrued),
                    IndexValue = c.IndexValue,
                    FloaterMargin = config.Margin,
                    AccrualDays = c.AccrualDays
                };
            }).ToList();
        }

        return response;
    }

    private static OwnerTrustStateDto ToStateDto(OwnerTrustState s)
    {
        return new OwnerTrustStateDto
        {
            Phase = PhaseNames.ToWire(s.Phase),
            Event = s.Event,
            Reserve = s.Reserve,
            AcquisitionAccount = s.AcquisitionAccount,
            NegativeCarry = s.NegativeCarry,
            AdjustedPool = s.AdjustedPool,
            WriteOffHistory = s.WriteOffHistory.Select(v => v * 100.0).ToList(),
            DelinquencyHistory = s.DelinquencyHistory.Select(v => v * 100.0).ToList(),
            OcPctHistory = s.OcPctHistory.Select(v => v * 100.0).ToList(),
            CleanUpEligibleMonths = s.CleanUpEligibleMonths
        };
    }

    private static OwnerTrustPeriodDto ToDto(OwnerTrustPeriod p)
    {
        return new OwnerTrustPeriodDto
        {
            Period = p.Period,
            PeriodEnd = p.PeriodEnd,
            DistributionDate = p.DistributionDate,
            Replayed = p.Replayed,
            Phase = PhaseNames.ToWire(p.Phase),
            Event = p.Event,
            BeginningPool = p.BeginningPool,
            PoolBeforeAdditions = p.PoolBeforeAdditions,
            EndingPool = p.EndingPool,
            BeginningAdjustedPool = p.BeginningAdjustedPool,
            AdjustedPoolBeforeAdditions = p.AdjustedPoolBeforeAdditions,
            EndingAdjustedPool = p.EndingAdjustedPool,
            Ysoc = p.Ysoc,
            ComputedAdjustedPool = p.ComputedAdjustedPool,
            Collections = p.Collections,
            WriteOffs = p.WriteOffs,
            RepurchaseCash = p.RepurchaseCash,
            OtherFunds = p.OtherFunds,
            ReserveEarnings = p.ReserveEarnings,
            CleanUpDeposit = p.CleanUpDeposit,
            AvailableFunds = p.AvailableFunds,
            Fees = p.Fees,
            ServicingFee = p.ServicingFee,
            FloorTestsPass = p.FloorTestsPass,
            OcTargetPct = Math.Round(p.OcTargetPct * 100.0, 10),
            OcTarget = p.OcTarget,
            Oc = p.Oc,
            PriorityPrincipal = p.PriorityPrincipal,
            RegularPrincipal = p.RegularPrincipal,
            AcceleratedPrincipal = p.AcceleratedPrincipal,
            PrincipalPaid = p.PrincipalPaid,
            InterestPaid = p.InterestPaid,
            ReserveRequired = p.ReserveRequired,
            ReserveDraw = p.ReserveDraw,
            ReserveRelease = p.ReserveRelease,
            ReserveDeposit = p.ReserveDeposit,
            Reserve = p.Reserve,
            AcquisitionDeposit = p.AcquisitionDeposit,
            AcquisitionCash = p.AcquisitionCash,
            AcquisitionRelease = p.AcquisitionRelease,
            AcquisitionAccount = p.AcquisitionAccount,
            AdditionsFace = p.AdditionsFace,
            AdditionsValue = p.AdditionsValue,
            NegativeCarryRequired = p.NegativeCarryRequired,
            NegativeCarryDeposit = p.NegativeCarryDeposit,
            NegativeCarryDraw = p.NegativeCarryDraw,
            NegativeCarryRelease = p.NegativeCarryRelease,
            NegativeCarry = p.NegativeCarry,
            CertificatePaid = p.CertificatePaid,
            NoteBalance = p.NoteBalance,
            Tests = p.Tests.Select(ToTestDto).ToList(),
            Classes = p.Classes.Select(c => new OwnerTrustClassDto
            {
                Key = c.Key,
                BeginBalance = c.BeginBalance,
                Coupon = c.CouponPct,
                IndexValue = c.IndexValue,
                AccrualDays = c.AccrualDays,
                InterestAccrued = c.InterestAccrued,
                InterestDue = c.InterestDue,
                InterestPaid = c.InterestPaid,
                InterestShortfall = c.InterestShortfall,
                PrincipalPaid = c.PrincipalPaid,
                EndBalance = c.EndBalance
            }).ToList(),
            Pool = InstallmentPoolMapper.ToDto(p.Pool)
        };
    }

    /// <summary>A ratio test reads in percents; a shortfall test in dollars.</summary>
    private static OwnerTrustTestDto ToTestDto(OwnerTrustTestReading t)
    {
        var ratio = t.Name is "WRITE_OFF_RATIO" or "DELINQUENCY_RATIO" or "ADJUSTED_POOL_TO_NOTES";
        double? Scale(double? v) => ratio ? v * 100.0 : v;
        return new OwnerTrustTestDto
        {
            Name = t.Name,
            Value = Scale(t.Value),
            Limit = Scale(t.Limit),
            Status = t.Value == null && !t.Breached ? "NOT_EVALUATED" : t.Breached ? "FAIL" : "PASS",
            Printed = t.Printed,
            Computed = Scale(t.Computed)
        };
    }
}
