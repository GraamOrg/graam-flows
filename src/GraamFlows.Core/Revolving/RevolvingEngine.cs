using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Revolving;

/// <summary>
///     The period loop for revolving trusts: roll the pool, allocate it across the series, run each
///     series' finance waterfall, share excess, write down what is left uncovered, run the principal
///     waterfall, share principal, and prove every dollar landed. It sits beside
///     <c>ComposableStructure</c> and shares nothing with that path except the rate provider and the
///     accrual basis, so no existing deal can be affected by it.
/// </summary>
public static class RevolvingEngine
{
    public static RevolvingResult Run(RevolvingRun run, IRateProvider rates)
    {
        RevolvingValidator.Validate(run);
        return new RevolvingLoop(run, rates).Execute();
    }

    private sealed class RevolvingLoop
    {
        private readonly RevolvingRun _run;
        private readonly RevolvingProgram _program;
        private readonly IRateProvider _rates;
        private readonly List<SeriesRun> _series;
        private readonly Dictionary<SeriesRun, List<SeriesPeriod>> _seriesRows;
        private readonly Dictionary<ClassRun, List<ClassPeriod>> _classRows;
        private readonly Dictionary<SeriesRun, List<PhaseMove>> _phasePaths;
        private readonly List<ProgramPeriod> _rows = new();
        private double _pool;

        public RevolvingLoop(RevolvingRun run, IRateProvider rates)
        {
            _run = run;
            _program = run.Program;
            _rates = rates;
            _pool = run.PoolBalance;
            _series = run.Series
                .Select(s => new SeriesRun(s, _program.ActionLag ?? 0, _program.Triggers))
                .ToList();
            _seriesRows = _series.ToDictionary(s => s, _ => new List<SeriesPeriod>(run.Periods));
            _classRows = _series.SelectMany(s => s.Classes).ToDictionary(c => c, _ => new List<ClassPeriod>(run.Periods));
            _phasePaths = _series.ToDictionary(s => s, _ => new List<PhaseMove>());
        }

        public RevolvingResult Execute()
        {
            foreach (var dates in RevolvingCalendar.Build(_run.StatementDate, _run.DistributionDay, _run.Periods))
                RunMonth(dates);

            return new RevolvingResult
            {
                Periods = _rows,
                Series = _series.Select(ToResult).ToList(),
                Disclosures = Disclosures()
            };
        }

        private void RunMonth(RevolvingPeriodDates calendarDates)
        {
            var month = RollPool(calendarDates);
            var pool = month.Pool;
            var dates = month.Dates;

            var readings = new Dictionary<SeriesRun, IReadOnlyList<TriggerReading>>();
            foreach (var s in _series)
            {
                s.OpenMonth();
                readings[s] = ReadTriggers(s, dates.Period);
                MoveByDate(s, dates);
            }

            var live = _series.Where(s => !s.PaidInFull).ToList();
            var allocations = SeriesAllocator.Allocate(pool, _program.PoolMeasure, _program.PrincipalFreeze!.Value,
                    live.Select(s => new SeriesClaim(s.Config.Name, s.AdjustedInvested, s.Phase,
                        s.FrozenPrincipalPct, s.FrozenNumerator)).ToList())
                .ToDictionary(a => a.Series);

            var openingAccounts = _series.Sum(s => s.Funding.Balance + s.Reserve + s.CarriedFinance);

            var opened = new Dictionary<SeriesRun, MonthOpen>();
            var earnings = 0.0;
            var allocatedFinance = 0.0;
            var allocatedPrincipal = 0.0;
            foreach (var s in _series)
            {
                var allocation = allocations.GetValueOrDefault(s.Config.Name);
                s.FrozenPrincipalPct = allocation.FrozenPrincipalPct ?? s.FrozenPrincipalPct;
                s.FrozenNumerator = allocation.FrozenNumerator ?? s.FrozenNumerator;
                var open = OpenSeries(s, allocation, pool, dates);
                opened[s] = open;
                earnings += open.Earnings;
                allocatedFinance += open.Finance;
                allocatedPrincipal += open.Principal;
            }

            var cashIn = pool.FinanceCash + pool.PrincipalCash + earnings + openingAccounts;

            var running = _series.Where(s => RunsWaterfall(s, dates)).ToList();
            var shared = new SharedCash();
            foreach (var step in _program.GroupSteps)
                switch (step.Verb)
                {
                    case WaterfallVerb.ReallocateGroupFinance:
                        GroupFinanceReallocation.Apply(running);
                        break;
                    default:
                        throw new InvalidOperationException($"{step.Verb} does not run across a group.");
                }

            RunFinance(running, shared);
            foreach (var s in running)
                RevolvingWaterfall.WriteDownUncovered(s);
            RunPrincipal(running, shared, dates);

            var transferorFinance = pool.FinanceCash - allocatedFinance + _series.Sum(s => s.TransferorFinance)
                                    + shared.Excess.Values.Sum();
            var transferorPrincipal = pool.PrincipalCash - allocatedPrincipal + _series.Sum(s => s.TransferorPrincipal)
                                      + shared.Principal.Values.Sum();

            foreach (var s in _series)
            {
                transferorFinance += SweepCents(s, dates, s.Finance, "finance", v => s.Finance = v);
                transferorPrincipal += SweepCents(s, dates, s.Principal, "principal", v => s.Principal = v);
            }

            foreach (var s in _series)
                CloseSeries(s, opened[s], allocations.GetValueOrDefault(s.Config.Name), readings[s], dates);

            var cashOut = transferorFinance + transferorPrincipal
                          + _series.Sum(s => s.Classes.Sum(c => c.InterestPaid + c.PrincipalPaid)
                                             + s.FeesPaid + s.Funding.Balance + s.Reserve + s.CarriedFinance);
            if (Math.Abs(cashIn - cashOut) > SeriesRun.Cent)
                throw new InvalidOperationException(
                    $"Period {dates.Period} of '{_program.Name}' collected {cashIn:F2} and placed {cashOut:F2}.");

            var invested = _series.Sum(s => s.AdjustedInvested);
            _rows.Add(month with
            {
                TransferorAmount = pool.Ending - invested,
                TransferorFinance = transferorFinance,
                TransferorPrincipal = transferorPrincipal,
                CashIn = cashIn,
                CashOut = cashOut,
                ReportingTests = ReportingTests(pool, invested)
            });
            _pool = pool.Ending;
        }

        private ProgramPeriod RollPool(RevolvingPeriodDates dates)
        {
            var bucket = _program.RecoveryBucket;
            if (dates.Period >= _run.ActualPeriods.Count)
                return new ProgramPeriod
                {
                    Dates = dates,
                    Pool = PoolLedger.Roll(_pool, _run.Drivers!.For(dates.Period, dates.Days), bucket)
                };

            var actual = _run.ActualPeriods[dates.Period];
            var replayedDates = actual.Days is { } days ? dates with { Days = days } : dates;
            var pool = PoolLedger.Replay(new PoolActuals
            {
                Beginning = actual.Beginning ?? _pool,
                NewReceivables = actual.NewReceivables,
                PrincipalCollections = actual.PrincipalCollections,
                FinanceCollections = actual.FinanceCollections,
                Recoveries = actual.Recoveries,
                ChargeOffs = actual.ChargeOffs,
                Dilution = actual.Dilution
            }, bucket);
            return new ProgramPeriod
            {
                Dates = replayedDates,
                Pool = pool,
                LedgerBreak = actual.Ending is { } ending ? pool.Ending - ending : null,
                RollBreak = actual.Beginning is { } beginning ? beginning - _pool : null
            };
        }

        /// <summary>
        ///     Tests read the months before this one, so a test tripped on seeded history can act in
        ///     the first projected month.
        /// </summary>
        private static IReadOnlyList<TriggerReading> ReadTriggers(SeriesRun s, int period)
        {
            if (s.PaidInFull)
                return Array.Empty<TriggerReading>();

            var readings = new List<TriggerReading>(s.Latches.Count);
            foreach (var latch in s.Latches)
            {
                readings.Add(latch.Read(period, s.History));
                if (latch.Test.Action == TriggerAction.EarlyAmortization && latch.ActingIn(period))
                    s.Machine.TryEnterEarlyAmortization();
            }

            return readings;
        }

        private static void MoveByDate(SeriesRun s, RevolvingPeriodDates dates)
        {
            if (s.Phase == SeriesPhase.Revolving && s.Config.AccumulationStart is { } start
                                                 && dates.PeriodStart >= start)
                s.Machine.MoveTo(SeriesPhase.Accumulating);
        }

        private static bool RunsWaterfall(SeriesRun s, RevolvingPeriodDates dates)
        {
            return s.Config.FirstPayDate is not { } first
                   || RevolvingWaterfall.OnOrAfterMonth(dates.DistributionDate, first);
        }

        private MonthOpen OpenSeries(SeriesRun s, SeriesAllocation allocation, PoolPeriod pool, RevolvingPeriodDates dates)
        {
            var invested = s.TotalInvested;
            var adjusted = s.AdjustedInvested;

            foreach (var c in s.Classes)
                AccrueInterest(c, dates);
            foreach (var fee in s.Config.Fees)
                s.AccrueFee(fee, adjusted);

            var earnings = s.Funding.Earnings(EarningsRatePct(dates.Period));
            var finance = pool.FinanceCash * allocation.FloatingPct;
            var principal = pool.PrincipalCash * allocation.PrincipalPct;
            var investorDefault = pool.ChargeOffs * allocation.FloatingPct;
            AllocateDefault(s, investorDefault, invested);

            if (s.Phase == SeriesPhase.EarlyAmortization && s.Funding.Balance > 0)
                RevolvingWaterfall.ReleaseFunding(s);

            if (s.Phase == SeriesPhase.Accumulating)
            {
                s.ControlledThisMonth = s.Config.ControlledAmounts[
                    Math.Min(s.AccumulationMonths, s.Config.ControlledAmounts.Count - 1)];
                s.AccumulationMonths++;
            }
            else
            {
                s.ControlledThisMonth = 0.0;
            }

            s.ReserveRequired = RequiredReserve(s, dates.Period);
            var surplus = Math.Max(0.0, s.Reserve - s.ReserveRequired);
            s.Reserve -= surplus;
            s.ReserveRelease = surplus;

            if (RunsWaterfall(s, dates))
            {
                s.Finance = finance + earnings + surplus + s.CarriedFinance;
                s.InvestorFinance = finance + s.CarriedFinance;
                s.CarriedFinance = 0.0;
                s.Principal += principal;
            }
            else
            {
                // Before the first waterfall a series only collects. Finance waits for its first
                // distribution; principal goes on funding new receivables, so the pool is not
                // drained and nothing arrives all at once on the first pay date.
                s.CarriedFinance += finance + earnings + surplus;
                s.TransferorPrincipal += principal + s.Principal;
                s.Principal = 0.0;
            }

            return new MonthOpen(invested, finance, principal, investorDefault, earnings);
        }

        private void AccrueInterest(ClassRun c, RevolvingPeriodDates dates)
        {
            var config = c.Config;
            c.IndexValue = config.Index is { } index ? _rates.GetRate(index, dates.PeriodStart) : null;
            c.RatePct = c.IndexValue is { } value ? value + config.Margin : config.Coupon;
            var fraction = config.Basis == AccrualBasis.Actual360 ? dates.AccrualDays / 360.0 : 1.0 / 12.0;
            c.InterestAccrued = c.Outstanding * c.RatePct / 100.0 * fraction;
            c.InterestDue += c.InterestAccrued;
        }

        /// <summary>A class bears its share of the series' default amount.</summary>
        private static void AllocateDefault(SeriesRun s, double investorDefault, double invested)
        {
            if (invested <= 0)
                return;

            foreach (var c in s.Classes)
                c.DefaultDue += investorDefault * c.Invested / invested;
        }

        private double RequiredReserve(SeriesRun s, int period)
        {
            if (s.PaidInFull)
                return 0.0;

            return s.Latches
                .Where(l => l.Test.Action == TriggerAction.FundReserve && l.ActingIn(period))
                .Select(l => l.Test.ReserveAmount!.Value)
                .Append(s.Config.RequiredReserve)
                .Max();
        }

        private double EarningsRatePct(int period)
        {
            var rates = _run.FundingEarningsRate;
            return rates == null || rates.Count == 0 ? 0.0 : rates[Math.Min(period, rates.Count - 1)];
        }

        /// <summary>
        ///     Excess finance comes before principal, so reallocation waits for the second pass, once
        ///     each series has what the others could spare.
        /// </summary>
        private void RunFinance(List<SeriesRun> running, SharedCash shared)
        {
            var steps = _program.FinanceSteps;
            foreach (var s in running)
                RevolvingWaterfall.RunFinance(s, steps, shared, false);

            ShareOut(running, shared.Excess, s => s.Config.ExcessSharing, DryRunFinance, (s, amount) =>
            {
                s.Finance += amount;
                s.ExcessIn += amount;
            });

            foreach (var s in running)
                RevolvingWaterfall.RunFinance(s, steps, shared, true);
        }

        private void RunPrincipal(List<SeriesRun> running, SharedCash shared, RevolvingPeriodDates dates)
        {
            var steps = _program.PrincipalSteps;
            foreach (var s in running)
                RevolvingWaterfall.RunPrincipal(s, steps, shared, dates.DistributionDate);

            ShareOut(running, shared.Principal, s => s.Config.PrincipalSharing,
                s => DryRunPrincipal(s, dates), (s, amount) =>
                {
                    s.Principal += amount;
                    s.SharedPrincipalIn += amount;
                });

            foreach (var s in running)
                RevolvingWaterfall.RunPrincipal(s, steps, shared, dates.DistributionDate);
        }

        private double DryRunFinance(SeriesRun s)
        {
            var dry = s.DryRun();
            dry.Finance = double.PositiveInfinity;
            dry.Spent = 0.0;
            RevolvingWaterfall.RunFinance(dry, _program.FinanceSteps, null, false);
            return dry.Spent;
        }

        private double DryRunPrincipal(SeriesRun s, RevolvingPeriodDates dates)
        {
            var dry = s.DryRun();
            dry.Principal = double.PositiveInfinity;
            dry.Spent = 0.0;
            RevolvingWaterfall.RunPrincipal(dry, _program.PrincipalSteps, null, dates.DistributionDate);
            return dry.Spent;
        }

        /// <summary>
        ///     A group's surplus goes to its series with a shortfall, in proportion to their
        ///     shortfalls. Whatever is left stays in the pool for the transferor.
        /// </summary>
        private static void ShareOut(List<SeriesRun> running, Dictionary<string, double> pool,
            Func<SeriesRun, bool> shares, Func<SeriesRun, double> shortfall, Action<SeriesRun, double> give)
        {
            foreach (var group in pool.Keys.ToList())
            {
                var surplus = pool[group];
                if (surplus <= 0)
                    continue;

                var needs = running
                    .Where(s => s.Config.Group == group && shares(s))
                    .Select(s => (Series: s, Need: shortfall(s)))
                    .Where(n => n.Need > 0)
                    .ToList();
                var total = needs.Sum(n => n.Need);
                if (total <= 0)
                    continue;

                var paid = Math.Min(surplus, total);
                foreach (var (series, need) in needs)
                    give(series, paid * need / total);
                pool[group] = surplus - paid;
            }
        }

        /// <summary>
        ///     A bucket a waterfall leaves full is a program with no last step, which is refused. Under
        ///     a cent is floating-point dust, and goes to the transferor.
        /// </summary>
        private double SweepCents(SeriesRun s, RevolvingPeriodDates dates, double amount, string bucket,
            Action<double> clear)
        {
            if (amount >= SeriesRun.Cent)
                throw new RevolvingConfigException(
                    $"Program '{_program.Name}' left {amount:F2} of {bucket} undistributed in series "
                    + $"'{s.Config.Name}' on {dates.DistributionDate:yyyy-MM-dd}. Its {bucket} steps must end "
                    + "by sharing it or releasing it to the transferor.");

            clear(0.0);
            return amount;
        }

        private void CloseSeries(SeriesRun s, MonthOpen open, SeriesAllocation allocation,
            IReadOnlyList<TriggerReading> readings, RevolvingPeriodDates dates)
        {
            if (s.Phase == SeriesPhase.Accumulating)
                s.Funding.CloseMonth(s.ControlledThisMonth, s.FundingRoom);
            else
                s.Funding.CloseMonth(0.0, 0.0);

            var yieldPct = RevolvingMetrics.PortfolioYieldPct(
                (s.ReallocatedFinance ?? open.Finance) + open.Earnings + s.ExcessIn - open.InvestorDefault,
                open.Invested, dates.Days);
            var baseRatePct = RevolvingMetrics.BaseRatePct(
                s.Classes.Sum(c => c.InterestAccrued) + s.FeesAccrued, open.Invested, dates.AccrualDays);
            if (open.Invested > 0)
            {
                Remember(s, TriggerMetric.PortfolioYield, yieldPct);
                Remember(s, TriggerMetric.BaseRate, baseRatePct);
                Remember(s, TriggerMetric.ExcessSpread, yieldPct - baseRatePct);
            }

            foreach (var c in s.Classes)
                _classRows[c].Add(new ClassPeriod
                {
                    Period = dates.Period,
                    DistributionDate = dates.DistributionDate,
                    BeginBalance = c.BeginInvested,
                    EndBalance = c.Invested,
                    Factor = c.Invested / (c.Config.OriginalBalance ?? c.Config.Balance),
                    InterestAccrued = c.InterestAccrued,
                    InterestPaid = c.InterestPaid,
                    InterestDueUnpaid = c.InterestDue,
                    PrincipalPaid = c.PrincipalPaid,
                    Writedown = c.NetWritedown,
                    CumWritedown = c.WrittenDown,
                    CouponPct = c.RatePct,
                    IndexValue = c.IndexValue,
                    Margin = c.Config.Margin,
                    AccrualDays = dates.AccrualDays
                });

            _seriesRows[s].Add(new SeriesPeriod
            {
                Period = dates.Period,
                DistributionDate = dates.DistributionDate,
                Phase = s.Phase,
                FloatingPct = allocation.FloatingPct * 100.0,
                PrincipalPct = allocation.PrincipalPct * 100.0,
                FinanceAllocation = open.Finance,
                ReallocatedFinance = s.ReallocatedFinance,
                PrincipalAllocation = open.Principal,
                InvestorDefault = open.InvestorDefault,
                InvestedAmount = s.TotalInvested,
                AdjustedInvested = s.AdjustedInvested,
                FundingAccount = s.Funding.Balance,
                FundingEarnings = open.Earnings,
                FundingDeposit = s.Funding.Deposited,
                FundingDeficit = s.Funding.Deficit,
                FundingRelease = s.FundingRelease,
                Reserve = s.Reserve,
                ReserveRequired = s.ReserveRequired,
                ReserveDeposit = s.ReserveDeposit,
                ReserveRelease = s.ReserveRelease,
                CarriedFinance = s.CarriedFinance,
                FeesAccrued = s.FeesAccrued,
                FeesPaid = s.FeesPaid,
                SharedPrincipalIn = s.SharedPrincipalIn,
                SharedPrincipalOut = s.SharedPrincipalOut,
                SharedExcessIn = s.ExcessIn,
                SharedExcessOut = s.ExcessOut,
                PortfolioYieldPct = yieldPct,
                BaseRatePct = baseRatePct,
                ExcessSpreadPct = yieldPct - baseRatePct,
                ExcessDollars = Math.Max(0.0, s.Surplus - s.ExcessIn),
                Triggers = readings
            });

            TrackPhase(s, dates);

            if (s.Phase == SeriesPhase.Accumulating && s.Config.ExpectedDate is { } expected
                                                    && RevolvingWaterfall.OnOrAfterMonth(dates.DistributionDate, expected))
                s.Machine.MoveTo(s.PaidInFull ? SeriesPhase.Amortizing : SeriesPhase.EarlyAmortization);
        }

        private void TrackPhase(SeriesRun s, RevolvingPeriodDates dates)
        {
            var path = _phasePaths[s];
            if (path.Count == 0 || path[^1].Phase != s.Phase)
                path.Add(new PhaseMove(s.Phase, dates.Period, dates.DistributionDate));
        }

        private static void Remember(SeriesRun s, TriggerMetric metric, double value)
        {
            if (!s.History.TryGetValue(metric, out var months))
                s.History[metric] = months = new List<double>();
            months.Add(value);
        }

        private IReadOnlyList<ReportingTest> ReportingTests(PoolPeriod pool, double invested)
        {
            var tests = new List<ReportingTest>(2);
            if (_program.SeriesRequiredTransferorPct is { } seriesPct)
            {
                var required = invested * (1.0 + seriesPct / 100.0);
                tests.Add(new ReportingTest("MINIMUM_POOL", required, pool.Ending, pool.Ending - required));
            }

            if (_program.RequiredTransferorPct is { } transferorPct)
            {
                var required = pool.Ending * transferorPct / 100.0;
                var transferor = pool.Ending - invested;
                tests.Add(new ReportingTest("MINIMUM_TRANSFEROR", required, transferor, transferor - required));
            }

            return tests;
        }

        private SeriesResult ToResult(SeriesRun s)
        {
            var trips = s.Latches.Where(l => l.FirstTrip != null).OrderBy(l => l.FirstTrip).ToList();
            var first = trips.FirstOrDefault();
            return new SeriesResult
            {
                Name = s.Config.Name,
                Periods = _seriesRows[s],
                Classes = s.Classes.ToDictionary(c => c.Config.Key, c => (IReadOnlyList<ClassPeriod>)_classRows[c]),
                PhasePath = _phasePaths[s],
                FirstTrip = first == null
                    ? null
                    : new FirstTrip(first.Test.Name, first.FirstTrip!.Value,
                        _rows[first.FirstTrip.Value].Dates.DistributionDate)
            };
        }

        private IReadOnlyList<string> Disclosures()
        {
            var disclosures = new List<string>();
            if (_run.FundingEarningsRate == null || _run.FundingEarningsRate.Count == 0)
                disclosures.Add("No funding account earnings rate was supplied, so funding accounts earned 0.");

            if (_run.Drivers is { } drivers)
            {
                if (drivers.RecoveryRate == null)
                    disclosures.Add("No recovery rate was supplied, so forecast recoveries are 0.");
                if (drivers.DilutionRate == null)
                    disclosures.Add("No dilution rate was supplied, so forecast dilution is 0.");
            }

            foreach (var (series, fee) in _program.FinanceSteps
                         .Where(step => step.Verb == WaterfallVerb.PayFee)
                         .SelectMany(step => _run.Series
                             .Where(s => s.Fees.All(f => f.Name != step.Fee))
                             .Select(s => (s.Name, step.Fee!))))
                disclosures.Add($"Series '{series}' states no fee '{fee}', so it is 0.");

            return disclosures;
        }

        private sealed record MonthOpen(
            double Invested,
            double Finance,
            double Principal,
            double InvestorDefault,
            double Earnings);
    }
}
