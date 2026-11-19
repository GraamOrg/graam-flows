using GraamFlows.Api.Models;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Revolving;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Turns a <see cref="RevolvingRequest" /> with <c>program.masterTrust</c> into a master trust run, and
///     its result into the response. Percents become fractions here, and a card program's fields are
///     refused by name: a master trust has none of them.
/// </summary>
public static class MasterTrustMapper
{
    public static bool IsMasterTrust(RevolvingRequest request)
    {
        return request.Program.MasterTrust != null;
    }

    public static MasterTrustRun ToRun(RevolvingRequest request)
    {
        var dto = request.Program.MasterTrust!;
        if (request.Program.OwnerTrust != null)
            throw new RevolvingConfigException("A program is an owner trust or a master trust, not both.");
        RefuseCardFields(request);

        var pool = request.Pool ?? throw new RevolvingConfigException(
            "A master trust states its pool: pool.model INSTALLMENT with its cohorts.");
        if (PoolModelParser.Parse(pool.Model) != PoolModel.Installment)
            throw new RevolvingConfigException("A master trust runs on an installment pool: state pool.model INSTALLMENT.");

        var classes = new Dictionary<string, RevolvingSeriesDto>();
        foreach (var s in request.Series)
        {
            var name = s.Name ?? throw new RevolvingConfigException("A series in request.series states no name.");
            if (!classes.TryAdd(name, s))
                throw new RevolvingConfigException($"request.series names '{name}' twice.");
        }

        var series = (dto.Series ?? throw new RevolvingConfigException("masterTrust states no series."))
            .Select((s, i) => ToSeries(s, i, classes)).ToList();
        foreach (var name in classes.Keys)
            if (series.All(s => s.Name != name))
                throw new RevolvingConfigException(
                    $"request.series names '{name}', which masterTrust.series does not: state its rules there.");

        return new MasterTrustRun
        {
            Name = request.Program.Name ?? "",
            Pool = InstallmentPoolMapper.ToPoolRun(pool, request.StatementDate, request.Periods),
            Trust = ToConfig(dto, series),
            State = ToState(dto.State),
            ActualPeriods = (dto.ActualPeriods ?? new List<MasterTrustActualDto>())
                .Select((a, i) => ToActual(a, i)).ToList(),
            DistributionDay = request.DistributionDay ?? 20
        };
    }

    /// <summary>The indices the notes pay on.</summary>
    public static IReadOnlyList<MarketDataInstEnum> FloatingIndices(MasterTrustRun run)
    {
        return run.Trust.Series.SelectMany(s => s.Classes).Where(c => c.Index != null).Select(c => c.Index!.Value)
            .Distinct().ToList();
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
        foreach (var s in request.Series)
        {
            var at = $"series['{s.Name}']";
            if (s.Group != null) stated.Add($"{at}.group");
            if (s.Fees != null) stated.Add($"{at}.fees");
            if (s.AccumulationStart != null) stated.Add($"{at}.accumulationStart");
            if (s.ControlledAmount != null) stated.Add($"{at}.controlledAmount");
            if (s.RequiredReserve != null) stated.Add($"{at}.requiredReserve");
            if (s.State != null) stated.Add($"{at}.state");
            if (s.ClosingDate != null) stated.Add($"{at}.closingDate");
        }

        if (stated.Count > 0)
            throw new RevolvingConfigException(
                $"A master trust states its rules in program.masterTrust and its pool in pool, so it takes no "
                + $"{string.Join(", ", stated)}.");
    }

    private static MasterTrustSeries ToSeries(MasterTrustSeriesDto dto, int i,
        IReadOnlyDictionary<string, RevolvingSeriesDto> classes)
    {
        var name = dto.Name ?? throw new RevolvingConfigException($"masterTrust.series[{i}] states no name.");
        var at = $"masterTrust.series['{name}']";
        var isPrivate = dto.Private ?? false;
        classes.TryGetValue(name, out var notes);
        if (isPrivate && notes != null)
            throw new RevolvingConfigException($"{at} is private, and request.series states its classes.");
        if (!isPrivate && notes == null)
            throw new RevolvingConfigException($"{at} is public: request.series states its classes under the same name.");

        var tests = dto.Tests ?? new MasterTrustSeriesTestsDto();
        var state = dto.State ?? new MasterTrustSeriesStateDto();
        return new MasterTrustSeries
        {
            Name = name,
            DiscountRate = (dto.DiscountRate ?? throw new RevolvingConfigException($"{at} states no discountRate.")) / 100.0,
            OcPct = (dto.OcPct ?? throw new RevolvingConfigException($"{at} states no ocPct.")) / 100.0,
            Private = isPrivate,
            PrivateNotes = isPrivate
                ? dto.PrivateNotes ?? throw new RevolvingConfigException($"{at} is private and states no privateNotes.")
                : dto.PrivateNotes == null
                    ? 0.0
                    : throw new RevolvingConfigException($"{at} is public: its notes are its classes, not privateNotes."),
            Classes = notes?.Classes.Select(c => RevolvingMapper.ToClass(name, c)).ToList()
                      ?? new List<RevolvingClass>(),
            Tiers = (dto.Tiers ?? new List<OwnerTrustTierDto>())
                .Select((t, k) => new OwnerTrustTier(
                    t.Name ?? throw new RevolvingConfigException($"{at}.tiers[{k}] states no name."),
                    t.Classes ?? throw new RevolvingConfigException($"{at}.tiers[{k}] states no classes."),
                    t.FinalMaturity))
                .ToList(),
            Steps = (dto.Steps ?? new List<OwnerTrustStepDto>())
                .Select(s => new OwnerTrustStep(OwnerTrustVerbs.Parse(s.Verb), s.Tier)).ToList(),
            ConcentrationGroup = dto.ConcentrationGroup ?? "",
            RequiredReserve = dto.RequiredReserve ?? 0.0,
            MonthlyFees = dto.MonthlyFees ?? 0.0,
            PfaLimitPct = (dto.PfaLimitPct ?? 50.0) / 100.0,
            ClosingDate = dto.ClosingDate,
            RedemptionDate = dto.RedemptionDate,
            RedemptionPrice = (dto.RedemptionPrice ?? 100.0) / 100.0,
            AnticipatedRedemption = dto.AnticipatedRedemption,
            Tests = new MasterTrustSeriesTests
            {
                InterestShortfall = tests.InterestShortfall ?? false,
                ReserveShortfall = tests.ReserveShortfall ?? false,
                PoolBalanceDeficit = tests.PoolBalanceDeficit ?? false,
                AllocatedPoolLimit = tests.AllocatedPoolLimit / 100.0,
                DiscountedInvested = tests.DiscountedInvested ?? false
            },
            State = new MasterTrustSeriesState
            {
                Phase = state.Phase == null ? SeriesPhase.Revolving : PhaseNames.Parse(state.Phase),
                Event = state.Event,
                Reserve = state.Reserve ?? 0.0,
                Pfa = state.Pfa ?? 0.0,
                FrozenNotes = state.FrozenNotes,
                FrozenNumerator = state.FrozenNumerator,
                PfaReleaseDue = state.PfaReleaseDue ?? 0.0
            }
        };
    }

    private static MasterTrustConfig ToConfig(MasterTrustDto dto, IReadOnlyList<MasterTrustSeries> series)
    {
        var rounding = dto.Rounding ?? new MasterTrustRoundingDto();
        var tests = dto.Tests ?? new MasterTrustTestsDto();
        MasterTrustAcquisition? acquisition = null;
        if (dto.Acquisition is { } a)
            acquisition = new MasterTrustAcquisition
            {
                Price = InstallmentPoolMapper.ToPrice("masterTrust.acquisition.price",
                    a.Price ?? throw new RevolvingConfigException("masterTrust.acquisition states no price.")),
                Terms = (a.Terms ?? throw new RevolvingConfigException("masterTrust.acquisition states no terms."))
                    .Select((t, i) => new InstallmentTerm(
                        t.Term ?? throw new RevolvingConfigException($"masterTrust.acquisition.terms[{i}] states no term."),
                        (t.Weight ?? throw new RevolvingConfigException(
                            $"masterTrust.acquisition.terms[{i}] states no weight.")) / 100.0, t.Age ?? 0))
                    .ToList(),
                HoldPoolFace = a.HoldPoolFace ?? false
            };

        return new MasterTrustConfig
        {
            Series = series,
            ServicingRate = (dto.ServicingRate ?? 0.0) / 100.0,
            Rounding = new MasterTrustRounding
            {
                DiscountRatio = rounding.DiscountRatio ?? 5,
                SeriesShare = rounding.SeriesShare ?? 4,
                Allocation = rounding.Allocation ?? 7
            },
            Tests = new OwnerTrustTests
            {
                WriteOffLimit = tests.WriteOffLimit / 100.0,
                WriteOffWindow = tests.WriteOffWindow ?? 3,
                WriteOffMultiplier = tests.WriteOffMultiplier ?? 4.0,
                DelinquencyLimit = tests.DelinquencyLimit / 100.0,
                DelinquencyWindow = tests.DelinquencyWindow ?? 3
            },
            Acquisition = acquisition,
            Concentration = (dto.Concentration ?? new Dictionary<string, List<double>>())
                .ToDictionary(k => k.Key, k => (IReadOnlyList<double>)k.Value),
            Delinquency = (dto.Delinquency ?? new List<double>()).Select(d => d / 100.0).ToList(),
            Calendar = dto.Calendar?.Trim().ToUpperInvariant() switch
            {
                null or "SETTLEMENT" => OwnerTrustCalendar.Settlement,
                "FEDERAL_RESERVE" => OwnerTrustCalendar.FederalReserve,
                _ => throw new RevolvingConfigException(
                    $"Unknown masterTrust.calendar '{dto.Calendar}'. Supported: SETTLEMENT and FEDERAL_RESERVE.")
            }
        };
    }

    private static MasterTrustState ToState(MasterTrustStateDto? dto)
    {
        if (dto == null)
            return new MasterTrustState();
        return new MasterTrustState
        {
            WriteOffHistory = (dto.WriteOffHistory ?? new List<double>()).Select(v => v / 100.0).ToList(),
            DelinquencyHistory = (dto.DelinquencyHistory ?? new List<double>()).Select(v => v / 100.0).ToList(),
            Concentration = dto.Concentration ?? new Dictionary<string, double>(),
            CertificateCash = dto.CertificateCash ?? 0.0
        };
    }

    private static MasterTrustActual ToActual(MasterTrustActualDto dto, int i)
    {
        var at = $"masterTrust.actualPeriods[{i}]";
        return new MasterTrustActual
        {
            Fads = (dto.Fads ?? new List<MasterTrustFadDto>()).Select((f, k) => new MasterTrustFad
            {
                Date = f.Date ?? throw new RevolvingConfigException($"{at}.fads[{k}] states no date."),
                PoolBalance = f.PoolBalance ?? throw new RevolvingConfigException($"{at}.fads[{k}] states no poolBalance."),
                Concentration = f.Concentration ?? new Dictionary<string, double>()
            }).ToList(),
            WriteOffRatio = dto.WriteOffRatio / 100.0,
            DelinquencyRatio = dto.DelinquencyRatio / 100.0,
            Delinquency = dto.Delinquency / 100.0,
            PoolBalanceDeficit = dto.PoolBalanceDeficit,
            OtherFunds = dto.OtherFunds ?? 0.0,
            Series = (dto.Series ?? new Dictionary<string, MasterTrustSeriesActualDto>()).ToDictionary(k => k.Key,
                k => new MasterTrustSeriesActual
                {
                    DiscountRate = k.Value.DiscountRate / 100.0,
                    PresentValueStart = k.Value.PresentValueStart,
                    PresentValueEnd = k.Value.PresentValueEnd,
                    AllocationNumerator = k.Value.AllocationNumerator,
                    NoteBalance = k.Value.NoteBalance,
                    Fees = k.Value.Fees,
                    RedemptionDeposit = k.Value.RedemptionDeposit,
                    Event = k.Value.Event,
                    Coupons = k.Value.Coupons ?? new Dictionary<string, double>()
                })
        };
    }

    public static RevolvingResponse ToResponse(MasterTrustRun run, MasterTrustResult result)
    {
        var response = new RevolvingResponse
        {
            Disclosures = result.Disclosures.ToList(),
            MasterTrust = new MasterTrustResultDto
            {
                Periods = result.Periods.Select(ToDto).ToList(),
                PhasePath = result.PhasePath.Select(m => new MasterTrustPhaseMoveDto
                {
                    Series = m.Series,
                    Phase = PhaseNames.ToWire(m.Phase),
                    Period = m.Period,
                    DistributionDate = m.DistributionDate,
                    Event = m.Event
                }).ToList(),
                Closing = new MasterTrustClosingDto
                {
                    State = new MasterTrustStateDto
                    {
                        WriteOffHistory = result.Closing.WriteOffHistory.Select(v => v * 100.0).ToList(),
                        DelinquencyHistory = result.Closing.DelinquencyHistory.Select(v => v * 100.0).ToList(),
                        Concentration = result.Closing.Concentration.ToDictionary(k => k.Key, k => k.Value),
                        CertificateCash = result.Closing.CertificateCash
                    },
                    Series = result.ClosingSeries.Select(s => new MasterTrustSeriesClosingDto
                    {
                        Name = s.Name,
                        State = s.State == null
                            ? null
                            : new MasterTrustSeriesStateDto
                            {
                                Phase = PhaseNames.ToWire(s.State.Phase),
                                Event = s.State.Event,
                                Reserve = s.State.Reserve,
                                Pfa = s.State.Pfa,
                                FrozenNotes = s.State.FrozenNotes,
                                FrozenNumerator = s.State.FrozenNumerator,
                                PfaReleaseDue = s.State.PfaReleaseDue
                            },
                        Classes = s.Classes.ToDictionary(c => c.Key, c => new RevolvingClassClosingDto
                        {
                            Balance = c.Balance,
                            OriginalBalance = OriginalBalance(run, s.Name, c.Key, c.Balance),
                            InterestShortfall = c.InterestShortfall
                        }),
                        Notes = s.Notes,
                        DiscountRate = s.DiscountRate * 100.0,
                        Ended = s.Ended
                    }).ToList(),
                    Pool = InstallmentPoolMapper.ToCohortDtos(result.ClosingPool)
                }
            }
        };

        // The classes in the tranche output shape, keyed "series/class", over the months each series ran.
        foreach (var series in run.Trust.Series.Where(s => !s.Private))
        foreach (var config in series.Classes)
        {
            var original = config.OriginalBalance ?? config.Balance;
            response.TrancheCashflows[$"{series.Name}/{config.Key}"] = result.Periods
                .Select(p => (p, row: p.Series.FirstOrDefault(x => x.Name == series.Name)))
                .Where(x => x.row != null)
                .Select(x =>
                {
                    var c = x.row!.Classes.First(k => k.Key == config.Key);
                    return new TrancheCashflowDto
                    {
                        Period = x.p.Period,
                        CashflowDate = x.p.DistributionDate,
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

    private static double OriginalBalance(MasterTrustRun run, string series, string key, double fallback)
    {
        var config = run.Trust.Series.First(s => s.Name == series).Classes.FirstOrDefault(c => c.Key == key);
        return config == null ? fallback : config.OriginalBalance ?? config.Balance;
    }

    private static MasterTrustPeriodDto ToDto(MasterTrustPeriod p)
    {
        return new MasterTrustPeriodDto
        {
            Period = p.Period,
            PeriodEnd = p.PeriodEnd,
            DistributionDate = p.DistributionDate,
            Replayed = p.Replayed,
            BeginningPool = p.BeginningPool,
            EndingPool = p.EndingPool,
            Fads = p.Fads.Select(f => new MasterTrustFadReadingDto
            {
                Date = f.Date,
                Days = f.Days,
                PoolBalance = f.PoolBalance
            }).ToList(),
            TimeWeightedPool = p.TimeWeightedPool,
            PresentValues = p.PresentValues.Select(v => new MasterTrustValuationDto
            {
                Rate = Math.Round(v.Rate * 100.0, 10),
                Beginning = v.Beginning,
                Ending = v.Ending
            }).ToList(),
            TotalFunds = p.TotalFunds,
            SumNumerators = p.SumNumerators,
            AllocationDenominator = p.AllocationDenominator,
            TransferorPct = Math.Round(p.TransferorPct * 100.0, 10),
            TransferorAllocation = p.TransferorAllocation,
            RequiredPoolBalance = p.RequiredPoolBalance,
            PoolBalanceDeficit = p.PoolBalanceDeficit,
            ComputedPoolBalanceDeficit = p.ComputedPoolBalanceDeficit,
            AdditionsFace = p.AdditionsFace,
            AdditionsCash = p.AdditionsCash,
            CertificateFundedFace = p.CertificateFundedFace,
            CertificateCash = p.CertificateCash,
            Tests = p.Tests.Select(ToTestDto).ToList(),
            Series = p.Series.Select(ToDto).ToList(),
            Pool = InstallmentPoolMapper.ToDto(p.Pool)
        };
    }

    private static MasterTrustSeriesPeriodDto ToDto(MasterTrustSeriesPeriod s)
    {
        return new MasterTrustSeriesPeriodDto
        {
            Name = s.Name,
            Phase = PhaseNames.ToWire(s.Phase),
            Event = s.Event,
            Private = s.Private,
            DiscountRate = Math.Round(s.DiscountRate * 100.0, 10),
            NotesStart = s.NotesStart,
            NotesEnd = s.NotesEnd,
            TargetOcStart = s.TargetOcStart,
            TargetOcEnd = s.TargetOcEnd,
            InvestedStart = s.InvestedStart,
            InvestedEnd = s.InvestedEnd,
            PresentValueStart = s.PresentValueStart,
            PresentValueEnd = s.PresentValueEnd,
            ComputedPresentValueEnd = s.ComputedPresentValueEnd,
            DiscountRatioStart = s.DiscountRatioStart,
            DiscountRatioEnd = s.DiscountRatioEnd,
            DiscountedInvestedStart = s.DiscountedInvestedStart,
            DiscountedInvestedEnd = s.DiscountedInvestedEnd,
            SeriesShareStart = Math.Round(s.SeriesShareStart * 100.0, 10),
            SeriesShareEnd = Math.Round(s.SeriesShareEnd * 100.0, 10),
            ConcentrationStart = s.ConcentrationStart,
            ConcentrationEnd = s.ConcentrationEnd,
            IncrementalStart = s.IncrementalStart,
            IncrementalEnd = s.IncrementalEnd,
            AdjustedInvestedStart = s.AdjustedInvestedStart,
            AdjustedInvestedEnd = s.AdjustedInvestedEnd,
            AllocationNumerator = s.AllocationNumerator,
            ComputedAllocationNumerator = s.ComputedAllocationNumerator,
            AllocationPct = Math.Round(s.AllocationPct * 100.0, 10),
            AllocatedPoolBalance = s.AllocatedPoolBalance,
            Funds = s.Funds,
            PfaRelease = s.PfaRelease,
            RedemptionDeposit = s.RedemptionDeposit,
            ReserveDraw = s.ReserveDraw,
            AvailableFunds = s.AvailableFunds,
            Fees = s.Fees,
            ServicingFee = s.ServicingFee,
            InterestPaid = s.InterestPaid,
            PriorityPrincipal = s.PriorityPrincipal.ToList(),
            RegularPrincipal = s.RegularPrincipal,
            PrincipalPaid = s.PrincipalPaid,
            PfaDeposit = s.PfaDeposit,
            Pfa = s.Pfa,
            ReserveRequired = s.ReserveRequired,
            ReserveDeposit = s.ReserveDeposit,
            ReserveRelease = s.ReserveRelease,
            Reserve = s.Reserve,
            ClassR = s.ClassR,
            Tests = s.Tests.Select(ToTestDto).ToList(),
            Classes = s.Classes.Select(c => new OwnerTrustClassDto
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
            }).ToList()
        };
    }

    /// <summary>A ratio test reads in percents; a shortfall or amount test in dollars.</summary>
    private static OwnerTrustTestDto ToTestDto(OwnerTrustTestReading t)
    {
        var ratio = t.Name is "WRITE_OFF_RATIO" or "DELINQUENCY_RATIO" or "ALLOCATED_POOL_TO_NOTES";
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
