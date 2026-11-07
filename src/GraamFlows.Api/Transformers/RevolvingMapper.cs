using GraamFlows.Api.Models;
using GraamFlows.Api.Validation;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using GraamFlows.Revolving;

namespace GraamFlows.Api.Transformers;

/// <summary>
///     Turns a <see cref="RevolvingRequest" /> into an engine run and its result into the response.
///     All parsing happens here, so anything the engine does not know is refused by name.
/// </summary>
public static class RevolvingMapper
{
    public static RevolvingRun ToRun(RevolvingRequest request)
    {
        var actuals = (request.ActualPeriods ?? new List<ActualPeriodDto>()).Select(ToActual).ToList();
        var pool = request.PoolBalance
                   ?? throw new RevolvingConfigException("The request states no poolBalance.");
        var periods = request.Periods ?? actuals.Count;
        return new RevolvingRun
        {
            Program = ToProgram(request.Program),
            StatementDate = request.StatementDate,
            DistributionDay = request.DistributionDay ?? 15,
            Periods = periods,
            PoolBalance = pool,
            Drivers = request.Drivers == null || periods <= actuals.Count ? null : ToDrivers(request.Drivers),
            ActualPeriods = actuals,
            Series = request.Series.Select(ToSeries).ToList(),
            FundingEarningsRate = request.FundingEarningsRate
        };
    }

    public static RevolvingResponse ToResponse(RevolvingResult result)
    {
        var response = new RevolvingResponse
        {
            Periods = result.Periods.Select(ToDto).ToList(),
            Disclosures = result.Disclosures.ToList()
        };

        foreach (var series in result.Series)
        {
            response.Series[series.Name] = new RevolvingSeriesResultDto
            {
                Periods = series.Periods.Select(ToDto).ToList(),
                PhasePath = series.PhasePath.Select(m => new RevolvingPhaseMoveDto
                {
                    Phase = PhaseNames.ToWire(m.Phase),
                    Period = m.Period,
                    DistributionDate = m.DistributionDate
                }).ToList(),
                FirstTrip = series.FirstTrip is { } trip
                    ? new RevolvingFirstTripDto
                    {
                        Test = trip.Test,
                        Period = trip.Period,
                        DistributionDate = trip.DistributionDate
                    }
                    : null,
                Closing = ToClosing(series.Closing)
            };

            foreach (var (key, rows) in series.Classes)
                response.TrancheCashflows[$"{series.Name}/{key}"] = rows.Select(ToTranche).ToList();
        }

        return response;
    }

    private static RevolvingClosingDto ToClosing(RevolvingSeries closing)
    {
        double[]? Months(TriggerMetric metric) => closing.History.GetValueOrDefault(metric)?.ToArray();

        return new RevolvingClosingDto
        {
            State = new RevolvingSeriesStateDto
            {
                Phase = PhaseNames.ToWire(closing.Phase),
                FrozenPrincipalPct = closing.FrozenPrincipalPct * 100.0,
                FrozenNumerator = closing.FrozenNumerator,
                FundingAccount = closing.FundingAccount,
                FundingDeficit = closing.FundingDeficit,
                Reserve = closing.Reserve,
                AccumulationMonthsRun = closing.AccumulationMonthsRun,
                CarriedFinance = closing.CarriedFinance,
                FeesDue = closing.FeesDue.ToDictionary(f => f.Key, f => f.Value),
                PriorNumerator = closing.PriorNumerator,
                PriorFunding = closing.PriorFunding,
                Usage = closing.Usage.ToDictionary(u => u.Key, u => u.Value),
                TriggerState = closing.TriggerStates.ToDictionary(t => t.Key, t => new RevolvingTriggerStateDto
                {
                    Latched = t.Value.Latched,
                    MonthsToAct = t.Value.MonthsToAct,
                    GoodMonths = t.Value.GoodMonths
                }),
                History = new RevolvingHistoryDto
                {
                    PortfolioYield = Months(TriggerMetric.PortfolioYield),
                    BaseRate = Months(TriggerMetric.BaseRate),
                    ExcessSpread = Months(TriggerMetric.ExcessSpread)
                }
            },
            Classes = closing.Classes.ToDictionary(c => c.Key, c => new RevolvingClassClosingDto
            {
                Balance = c.Balance,
                OriginalBalance = c.OriginalBalance ?? c.Balance,
                WrittenDown = c.WrittenDown,
                InterestShortfall = c.InterestShortfall,
                DefaultDue = c.DefaultDue
            })
        };
    }

    /// <summary>The indices a run's floating classes pay on.</summary>
    public static IReadOnlyList<MarketDataInstEnum> FloatingIndices(RevolvingRun run)
    {
        return run.Series
            .SelectMany(s => s.Classes)
            .Where(c => c.Index != null)
            .Select(c => c.Index!.Value)
            .Distinct()
            .ToList();
    }

    private static RevolvingProgram ToProgram(RevolvingProgramDto dto)
    {
        return new RevolvingProgram
        {
            Name = dto.Name ?? "",
            RecoveryBucket = RecoveryBucketParser.Parse(dto.Name, dto.RecoveryBucket),
            PoolMeasure = PoolMeasureParser.Parse(dto.PoolMeasure),
            ActionLag = dto.ActionLag,
            PrincipalFreeze = PrincipalFreezeParser.Parse(dto.Name, dto.PrincipalFreeze),
            GroupSteps = (dto.GroupSteps ?? new List<RevolvingStepDto>()).Select(ToStep).ToList(),
            FinanceSteps = dto.FinanceSteps.Select(ToStep).ToList(),
            PrincipalSteps = dto.PrincipalSteps.Select(ToStep).ToList(),
            Triggers = (dto.Triggers ?? new List<RevolvingTriggerDto>()).Select(ToTrigger).ToList(),
            SeriesRequiredTransferorPct = dto.SeriesRequiredTransferorPct,
            RequiredTransferorPct = dto.RequiredTransferorPct,
            Delinked = dto.Delinked == null
                ? null
                : new DelinkedNotes
                {
                    ClassOrder = dto.Delinked.ClassOrder ?? new List<string>(),
                    SegregatedFinance = dto.Delinked.SegregatedFinance
                }
        };
    }

    private static WaterfallStep ToStep(RevolvingStepDto dto)
    {
        return new WaterfallStep(
            WaterfallVerbs.Parse(dto.Verb),
            dto.Class,
            dto.Fee,
            dto.From,
            dto.WhenPaid,
            dto.Phases?.Select(PhaseNames.Parse).ToList(),
            ReallocationCoversParser.Parse(dto.Covers));
    }

    private static TriggerTest ToTrigger(RevolvingTriggerDto dto)
    {
        var name = dto.Name ?? throw new RevolvingConfigException("A trigger must state its name.");
        return new TriggerTest
        {
            Name = name,
            Metric = TriggerNames.ParseMetric(name, dto.Metric),
            Window = dto.Window,
            Comparison = TriggerNames.ParseComparison(name, dto.Comparison),
            Level = dto.Level,
            AgainstMetric = dto.AgainstMetric == null ? null : TriggerNames.ParseMetric(name, dto.AgainstMetric),
            Action = TriggerNames.ParseAction(name, dto.Action),
            CurePeriods = dto.CurePeriods,
            ReserveAmount = dto.ReserveAmount
        };
    }

    private static RevolvingSeries ToSeries(RevolvingSeriesDto dto)
    {
        var name = dto.Name ?? throw new RevolvingConfigException("A series must state its name.");
        var state = dto.State;
        return new RevolvingSeries
        {
            Name = name,
            Group = dto.Group ?? "",
            PrincipalSharing = dto.PrincipalSharing,
            ExcessSharing = dto.ExcessSharing,
            Classes = dto.Classes.Select(c => ToClass(name, c)).ToList(),
            Fees = (dto.Fees ?? new List<RevolvingFeeDto>())
                .Select(f => ToFee(name, f)).ToList(),
            WritedownOrder = dto.WritedownOrder ?? new List<string>(),
            FundedClasses = dto.FundedClasses ?? new List<string>(),
            AccumulationStart = dto.AccumulationStart,
            ExpectedDate = dto.ExpectedDate,
            ControlledAmounts = dto.ControlledAmount ?? Array.Empty<double>(),
            FirstPayDate = dto.FirstPayDate,
            ClosingDate = dto.ClosingDate,
            RequiredReserve = dto.RequiredReserve ?? 0.0,
            Phase = state?.Phase == null ? SeriesPhase.Revolving : PhaseNames.Parse(state.Phase),
            FrozenPrincipalPct = state?.FrozenPrincipalPct / 100.0,
            FrozenNumerator = state?.FrozenNumerator,
            FundingAccount = state?.FundingAccount ?? 0.0,
            FundingDeficit = state?.FundingDeficit ?? 0.0,
            Reserve = state?.Reserve ?? 0.0,
            AccumulationMonthsRun = state?.AccumulationMonthsRun ?? 0,
            CarriedFinance = state?.CarriedFinance ?? 0.0,
            FeesDue = state?.FeesDue ?? new Dictionary<string, double>(),
            TriggerStates = (state?.TriggerState ?? new Dictionary<string, RevolvingTriggerStateDto>())
                .ToDictionary(t => t.Key, t => new TriggerState(t.Value.Latched, t.Value.MonthsToAct, t.Value.GoodMonths)),
            History = ToHistory(state?.History),
            RequiredSubordination = dto.RequiredSubordination ?? new Dictionary<string, double>(),
            Usage = state?.Usage ?? new Dictionary<string, double>(),
            PriorNumerator = state?.PriorNumerator,
            PriorFunding = state?.PriorFunding
        };
    }

    private static RevolvingFee ToFee(string series, RevolvingFeeDto dto)
    {
        var rate = dto.Rate ?? throw new RevolvingConfigException(
            $"Fee '{dto.Name}' of series '{series}' states no rate.");
        return new RevolvingFee(dto.Name ?? "", rate);
    }

    private static IReadOnlyDictionary<TriggerMetric, IReadOnlyList<double>> ToHistory(RevolvingHistoryDto? dto)
    {
        var history = new Dictionary<TriggerMetric, IReadOnlyList<double>>();
        if (dto?.PortfolioYield != null)
            history[TriggerMetric.PortfolioYield] = dto.PortfolioYield;
        if (dto?.BaseRate != null)
            history[TriggerMetric.BaseRate] = dto.BaseRate;
        if (dto?.ExcessSpread != null)
            history[TriggerMetric.ExcessSpread] = dto.ExcessSpread;
        return history;
    }

    private static RevolvingClass ToClass(string series, RevolvingClassDto dto)
    {
        var key = dto.Key ?? throw new RevolvingConfigException($"A class of series '{series}' has no key.");
        MarketDataInstEnum? index = null;
        if (!string.IsNullOrWhiteSpace(dto.Index))
        {
            if (!MarketRateResolver.TryParseIndex(dto.Index, out var parsed))
                throw new RevolvingConfigException(
                    $"Class '{key}' of series '{series}' has unknown index '{dto.Index}'. "
                    + $"Supported: {MarketRateResolver.LegalIndexNames}.");
            index = parsed;

            if (dto.Margin == null)
                throw new RevolvingConfigException($"Floating class '{key}' of series '{series}' states no margin.");

            if (string.IsNullOrWhiteSpace(dto.DayCount))
                throw new RevolvingConfigException(
                    $"Floating class '{key}' of series '{series}' states no dayCount. 30/360 is the default "
                    + "for a fixed class only: state 30/360 or Actual/360.");
        }

        return new RevolvingClass
        {
            Key = key,
            Balance = dto.Balance ?? throw new RevolvingConfigException(
                $"Class '{key}' of series '{series}' states no balance."),
            OriginalBalance = dto.OriginalBalance,
            Coupon = dto.Coupon,
            Index = index,
            Margin = dto.Margin ?? 0.0,
            Basis = AccrualBasisParser.Parse(dto.DayCount),
            WrittenDown = dto.WrittenDown ?? 0.0,
            InterestShortfall = dto.InterestShortfall ?? 0.0,
            DefaultDue = dto.DefaultDue ?? 0.0
        };
    }

    private static ActualPeriod ToActual(ActualPeriodDto dto, int month)
    {
        return new ActualPeriod
        {
            Beginning = dto.BeginningBalance,
            NewReceivables = Stated(month, "newReceivables", dto.NewReceivables),
            PrincipalCollections = Stated(month, "principalCollections", dto.PrincipalCollections),
            FinanceCollections = Stated(month, "financeCollections", dto.FinanceCollections),
            Recoveries = dto.Recoveries,
            ChargeOffs = Stated(month, "chargeOffs", dto.ChargeOffs),
            Dilution = dto.Dilution,
            Ending = dto.EndingBalance,
            Days = dto.Days,
            AllocationBalance = dto.AllocationBalance,
            InterestAccountEarnings = dto.InterestAccountEarnings ?? 0.0,
            PrincipalAccountEarnings = dto.PrincipalAccountEarnings ?? 0.0
        };
    }

    private static double Stated(int month, string name, double? value)
    {
        return value ?? throw new RevolvingConfigException($"actualPeriods[{month}] states no {name}.");
    }

    private static PoolDriverPath ToDrivers(RevolvingDriversDto dto)
    {
        return new PoolDriverPath
        {
            PaymentRate = Required("paymentRate", dto.PaymentRate),
            NewReceivablesRate = Optional("newReceivablesRate", dto.NewReceivablesRate),
            HoldPool = dto.HoldPool,
            Yield = Required("yield", dto.Yield),
            ChargeOffRate = Required("chargeOffRate", dto.ChargeOffRate),
            RecoveryRate = Optional("recoveryRate", dto.RecoveryRate),
            DilutionRate = Optional("dilutionRate", dto.DilutionRate)
        };
    }

    private static PoolDriver Required(string name, RevolvingDriverDto? dto)
    {
        return Optional(name, dto)
               ?? throw new RevolvingConfigException($"Forecast drivers must state {name}.");
    }

    private static PoolDriver? Optional(string name, RevolvingDriverDto? dto)
    {
        if (dto == null)
            return null;

        if (dto.Values.Length == 0)
            throw new RevolvingConfigException($"Driver {name} has no values.");

        return new PoolDriver(dto.Values.Select(v => v / 100.0).ToList(), dto.Annual);
    }

    private static RevolvingProgramPeriodDto ToDto(ProgramPeriod p)
    {
        return new RevolvingProgramPeriodDto
        {
            Period = p.Dates.Period,
            PeriodEnd = p.Dates.PeriodEnd,
            DistributionDate = p.Dates.DistributionDate,
            Days = p.Dates.Days,
            AccrualDays = p.Dates.AccrualDays,
            BeginningBalance = p.Pool.Beginning,
            NewReceivables = p.Pool.NewReceivables,
            PrincipalCollections = p.Pool.PrincipalCollections,
            FinanceCollections = p.Pool.FinanceCollections,
            Recoveries = p.Pool.Recoveries,
            RecoveriesToFinance = p.Pool.RecoveriesToFinance,
            RecoveriesToPrincipal = p.Pool.RecoveriesToPrincipal,
            ChargeOffs = p.Pool.ChargeOffs,
            Dilution = p.Pool.Dilution,
            EndingBalance = p.Pool.Ending,
            LedgerBreak = p.LedgerBreak,
            RollBreak = p.RollBreak,
            TransferorAmount = p.TransferorAmount,
            TransferorFinance = p.TransferorFinance,
            TransferorPrincipal = p.TransferorPrincipal,
            CashIn = p.CashIn,
            CashOut = p.CashOut,
            ReportingTests = p.ReportingTests.Select(t => new RevolvingReportingTestDto
            {
                Name = t.Name,
                Required = t.Required,
                Actual = t.Actual,
                Headroom = t.Headroom
            }).ToList(),
            Notes = p.Notes is { } n
                ? new RevolvingNotesDto
                {
                    Numerator = n.Numerator,
                    PrincipalNumerator = n.PrincipalNumerator,
                    FinanceAllocation = n.FinanceAllocation,
                    PrincipalAllocation = n.PrincipalAllocation,
                    InvestorDefault = n.InvestorDefault,
                    FeesPaid = n.FeesPaid,
                    InterestPaid = n.InterestPaid,
                    AccountEarnings = n.AccountEarnings,
                    SegregatedFinance = n.SegregatedFinance,
                    ExcessDollars = n.ExcessDollars,
                    UncoveredDefault = n.UncoveredDefault,
                    UnchargedDefault = n.UnchargedDefault,
                    PrincipalReallocated = n.PrincipalReallocated,
                    FinanceYieldPct = n.FinanceYieldPct,
                    NetLossPct = n.NetLossPct,
                    PortfolioYieldPct = n.PortfolioYieldPct,
                    CouponPct = n.CouponPct,
                    BaseRatePct = n.BaseRatePct,
                    ExcessSpreadPct = n.ExcessSpreadPct
                }
                : null
        };
    }

    private static RevolvingSeriesPeriodDto ToDto(SeriesPeriod p)
    {
        return new RevolvingSeriesPeriodDto
        {
            Period = p.Period,
            DistributionDate = p.DistributionDate,
            Phase = PhaseNames.ToWire(p.Phase),
            FloatingPct = p.FloatingPct,
            PrincipalPct = p.PrincipalPct,
            FinanceAllocation = p.FinanceAllocation,
            ReallocatedFinance = p.ReallocatedFinance,
            PrincipalAllocation = p.PrincipalAllocation,
            InvestorDefault = p.InvestorDefault,
            InvestedAmount = p.InvestedAmount,
            AdjustedInvested = p.AdjustedInvested,
            FundingAccount = p.FundingAccount,
            FundingEarnings = p.FundingEarnings,
            FundingDeposit = p.FundingDeposit,
            FundingDeficit = p.FundingDeficit,
            FundingRelease = p.FundingRelease,
            Reserve = p.Reserve,
            ReserveRequired = p.ReserveRequired,
            ReserveDeposit = p.ReserveDeposit,
            ReserveRelease = p.ReserveRelease,
            CarriedFinance = p.CarriedFinance,
            FeesAccrued = p.FeesAccrued,
            FeesPaid = p.FeesPaid,
            SharedPrincipalIn = p.SharedPrincipalIn,
            SharedPrincipalOut = p.SharedPrincipalOut,
            SharedExcessIn = p.SharedExcessIn,
            SharedExcessOut = p.SharedExcessOut,
            PortfolioYieldPct = p.PortfolioYieldPct,
            BaseRatePct = p.BaseRatePct,
            ExcessSpreadPct = p.ExcessSpreadPct,
            ExcessDollars = p.ExcessDollars,
            Triggers = p.Triggers.Select(t => new RevolvingTriggerReadingDto
            {
                Name = t.Name,
                Status = !t.Evaluated ? "NOT_EVALUATED" : t.Headroom < 0 ? "FAIL" : "PASS",
                Value = t.Value,
                Reference = t.Reference,
                Headroom = t.Headroom,
                Latched = t.Latched,
                Acting = t.Acting
            }).ToList(),
            FrozenPrincipalPct = p.FrozenPrincipalPct,
            FrozenNumerator = p.FrozenNumerator,
            Tranche = p.Tranche is { } t2
                ? new RevolvingTrancheDto
                {
                    Numerator = t2.Numerator,
                    OpeningNominalLiquidation = t2.OpeningNominalLiquidation,
                    NextNumerator = t2.NextNumerator,
                    NextPriorFunding = t2.NextPriorFunding,
                    RequiredSubordination = t2.RequiredSubordination.ToDictionary(x => x.Key, x => x.Value),
                    UsageThisMonth = t2.UsageThisMonth.ToDictionary(x => x.Key, x => x.Value),
                    Usage = t2.Usage.ToDictionary(x => x.Key, x => x.Value)
                }
                : null
        };
    }

    private static TrancheCashflowDto ToTranche(ClassPeriod c)
    {
        return new TrancheCashflowDto
        {
            Period = c.Period,
            CashflowDate = c.DistributionDate,
            BeginBalance = c.BeginBalance,
            Balance = c.EndBalance,
            Factor = c.Factor,
            ScheduledPrincipal = c.PrincipalPaid,
            Interest = c.InterestPaid,
            Coupon = c.CouponPct,
            EffectiveCoupon = c.CouponPct,
            Writedown = c.Writedown,
            CumWritedown = c.CumWritedown,
            InterestShortfall = Math.Max(0.0, c.InterestAccrued - c.InterestPaid),
            AccumInterestShortfall = c.InterestDueUnpaid,
            InterestShortfallPayback = Math.Max(0.0, c.InterestPaid - c.InterestAccrued),
            IndexValue = c.IndexValue,
            FloaterMargin = c.Margin,
            AccrualDays = c.AccrualDays
        };
    }
}
