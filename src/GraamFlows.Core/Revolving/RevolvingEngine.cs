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
        private readonly Dictionary<TriggerMetric, List<double>>? _noteHistory;
        private double _pool;

        public RevolvingLoop(RevolvingRun run, IRateProvider rates)
        {
            _run = run;
            _program = run.Program;
            _rates = rates;
            _pool = run.PoolBalance;
            _noteHistory = _program.Delinked == null ? null : NoteHistory(run.Series);
            _series = run.Series
                .Select(s => new SeriesRun(s, _program.ActionLag ?? 0, _program.Triggers, _noteHistory))
                .ToList();
            _seriesRows = _series.ToDictionary(s => s, _ => new List<SeriesPeriod>(run.Periods));
            _classRows = _series.SelectMany(s => s.Classes).ToDictionary(c => c, _ => new List<ClassPeriod>(run.Periods));
            _phasePaths = _series.ToDictionary(s => s, _ => new List<PhaseMove>());
        }

        /// <summary>
        ///     A note series' tests read the whole series, so its tranches read one history. The
        ///     validator has checked that any tranches stating it state the same months.
        /// </summary>
        private static Dictionary<TriggerMetric, List<double>> NoteHistory(IEnumerable<RevolvingSeries> tranches)
        {
            var seeded = tranches.FirstOrDefault(t => t.History.Count > 0);
            return seeded == null
                ? new Dictionary<TriggerMetric, List<double>>()
                : seeded.History.ToDictionary(h => h.Key, h => h.Value.ToList());
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

            var live = _series.Where(s => !Finished(s)).ToList();
            foreach (var s in live)
                s.Numerator = Delinked ? s.PriorNumerator ?? s.AdjustedInvested : s.AdjustedInvested;
            var allocations = SeriesAllocator.Allocate(pool, _program.PoolMeasure, _program.PrincipalFreeze!.Value,
                    live.Select(s => new SeriesClaim(s.Config.Name, s.Numerator, s.Phase,
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

            var accountEarnings = month.AccountEarnings;
            var cashIn = pool.FinanceCash + pool.PrincipalCash + earnings + openingAccounts + accountEarnings;

            var running = Delinked ? live : _series.Where(s => RunsWaterfall(s, dates)).ToList();
            var shared = new SharedCash();
            NoteMonth? notes = null;
            var segregated = 0.0;
            if (Delinked)
            {
                segregated = SegregatedFinance(live, earnings + month.PrincipalAccountEarnings,
                    pool.FinanceCash - allocatedFinance, dates);
                allocatedFinance += segregated;
                notes = RunNotes(live, opened, allocations, accountEarnings + segregated, dates);
            }
            else
            {
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
            }

            var transferorFinance = pool.FinanceCash - allocatedFinance + _series.Sum(s => s.TransferorFinance)
                                    + shared.Excess.Values.Sum() + (notes?.Excess ?? 0.0);
            var transferorPrincipal = pool.PrincipalCash - allocatedPrincipal + _series.Sum(s => s.TransferorPrincipal)
                                      + shared.Principal.Values.Sum() + (notes?.TransferorPrincipal ?? 0.0);

            foreach (var s in _series)
            {
                transferorFinance += SweepCents(s, dates, s.Finance, "finance", v => s.Finance = v);
                transferorPrincipal += SweepCents(s, dates, s.Principal, "principal", v => s.Principal = v);
            }

            var notePeriod = notes == null
                ? null
                : ClosedNotes(notes, opened, allocations, accountEarnings, segregated, month.InterestAccountEarnings, dates);

            foreach (var s in _series)
                CloseSeries(s, opened[s], allocations.GetValueOrDefault(s.Config.Name), readings[s], dates, notePeriod);

            if (notePeriod is { Numerator: >= SeriesRun.Cent })
            {
                Remember(_noteHistory!, TriggerMetric.PortfolioYield, notePeriod.PortfolioYieldPct);
                Remember(_noteHistory!, TriggerMetric.BaseRate, notePeriod.BaseRatePct);
                Remember(_noteHistory!, TriggerMetric.ExcessSpread, notePeriod.ExcessSpreadPct);
            }

            var cashOut = transferorFinance + transferorPrincipal
                          + _series.Sum(s => s.Classes.Sum(c => c.InterestPaid + c.PrincipalPaid)
                                             + s.FeesPaid + s.Funding.Balance + s.Reserve + s.CarriedFinance);
            // Written so that NaN fails: a comparison with NaN is false.
            if (!(Math.Abs(cashIn - cashOut) <= SeriesRun.Cent))
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
                ReportingTests = ReportingTests(pool, invested),
                Notes = notePeriod
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
                    Pool = PoolLedger.Roll(_pool,
                        _run.Drivers!.For(dates.Period - _run.ActualPeriods.Count, dates.Days), bucket)
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
                Dilution = actual.Dilution,
                AllocationBalance = actual.AllocationBalance
            }, bucket);
            return new ProgramPeriod
            {
                Dates = replayedDates,
                Pool = pool,
                LedgerBreak = actual.Ending is { } ending ? pool.Ending - ending : null,
                RollBreak = actual.Beginning is { } beginning ? beginning - _pool : null,
                InterestAccountEarnings = actual.InterestAccountEarnings,
                PrincipalAccountEarnings = actual.PrincipalAccountEarnings
            };
        }

        /// <summary>
        ///     Tests read the months before this one, so a test tripped on seeded history can act in
        ///     the first projected month.
        /// </summary>
        private IReadOnlyList<TriggerReading> ReadTriggers(SeriesRun s, int period)
        {
            if (Finished(s))
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

            var closing = ClosingInThisPeriod(s, dates);

            // A tranche is allocated collections from the month it is issued, and owes nothing
            // until its first payment month, when its whole first period falls due at once.
            var accrues = !Finished(s) && !AwaitsFirstPeriod(s, dates);
            s.InterestDays = !accrues ? 0
                : closing is { } from ? (dates.DistributionDate - from).Days
                : dates.AccrualDays;
            foreach (var c in s.Classes)
                AccrueInterest(c, dates, closing, accrues);
            foreach (var fee in s.Config.Fees)
                s.AccrueFee(fee, Delinked ? s.Numerator : adjusted);

            var earnings = s.Funding.Earnings(EarningsRatePct(dates.Period));
            var finance = pool.FinanceCash * allocation.FloatingPct;
            var principal = pool.PrincipalCash * allocation.PrincipalPct;
            var investorDefault = pool.DefaultAmount * allocation.FloatingPct;
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

            if (Delinked || RunsWaterfall(s, dates))
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

        private bool Delinked => _program.Delinked != null;

        /// <summary>
        ///     Out of the month's allocation and accruals. A note tranche written off stays in: it is
        ///     owed its amount back from finance.
        /// </summary>
        private bool Finished(SeriesRun s)
        {
            return Delinked ? s.Retired : s.Exhausted;
        }

        /// <summary>
        ///     Interest on what the funding accounts held at the close of the month before, on each
        ///     tranche's own accrual, less what those accounts earned, whether at the run's rate or
        ///     supplied. It comes from finance that would otherwise be the transferor's. It starts by
        ///     itself in the second month of deposits, and a tranche paid off has none.
        /// </summary>
        private double SegregatedFinance(List<SeriesRun> live, double earned, double transferorFinance,
            RevolvingPeriodDates dates)
        {
            if (_program.Delinked is not { SegregatedFinance: true })
                return 0.0;

            var carry = live.Sum(s => s.PriorFunding * s.Classes[0].RatePct / 100.0
                                      * AccrualFraction(s.Classes[0].Config.Basis, dates, null)) - earned;
            return Math.Min(Math.Max(0.0, carry), Math.Max(0.0, transferorFinance));
        }

        /// <summary>A note series' month: one finance bucket, one principal bucket, class by class.</summary>
        private NoteMonth RunNotes(List<SeriesRun> live, Dictionary<SeriesRun, MonthOpen> opened,
            Dictionary<string, SeriesAllocation> allocations, double addedFinance, RevolvingPeriodDates dates)
        {
            var notes = new NoteMonth(_program.Delinked!, live);
            Subordination.Measure(notes, s => s.OpeningAdjusted);

            // A retired tranche holds no claim, but its released reserve is still finance to apply.
            foreach (var s in _series)
            {
                notes.Finance += s.Finance;
                notes.Principal += s.Principal;
                if (s.ClassKey != notes.SeniorClass)
                    notes.JuniorPrincipalLeft += opened[s].Principal;
                s.Finance = 0.0;
                s.Principal = 0.0;
            }

            notes.Finance += addedFinance;
            NoteWaterfall.RunFinance(notes, _program.FinanceSteps);

            var uncovered = live.Sum(s => s.Classes[0].DefaultDue);
            foreach (var s in live)
                s.Classes[0].DefaultDue = 0.0;
            Subordination.AllocateCharges(notes, uncovered);

            NoteWaterfall.RunPrincipal(notes, _program.PrincipalSteps, dates.DistributionDate);
            return notes;
        }

        private NotePeriod ClosedNotes(NoteMonth notes, Dictionary<SeriesRun, MonthOpen> opened,
            Dictionary<string, SeriesAllocation> allocations, double earnings, double segregated,
            double interestAccountEarnings, RevolvingPeriodDates dates)
        {
            // A required amount stands on the month's opening adjusted amount, and a tranche paid
            // this month no longer requires any.
            Subordination.Measure(notes, s => s.Retired ? 0.0 : s.OpeningAdjusted);

            var numerator = notes.Tranches.Sum(s => s.Numerator);
            var frozen = notes.Tranches.Sum(s => s.Phase == SeriesPhase.Revolving
                ? s.Numerator
                : s.FrozenNumerator ?? s.Numerator);
            var finance = notes.Tranches.Sum(s => opened[s].Finance);
            var investorDefault = notes.Tranches.Sum(s => opened[s].InvestorDefault);
            var financeYield = RevolvingMetrics.FinanceYieldPct(finance + interestAccountEarnings, numerator, dates);
            var netLoss = RevolvingMetrics.MonthlyAnnualizedPct(investorDefault, numerator);
            var coupon = RevolvingMetrics.WeightedCouponPct(notes.Tranches.SelectMany(s => s.Classes));
            var servicing = RevolvingMetrics.FeePct(notes.Tranches);
            return new NotePeriod
            {
                Numerator = numerator,
                PrincipalNumerator = frozen,
                FinanceAllocation = finance,
                PrincipalAllocation = notes.Tranches.Sum(s => opened[s].Principal),
                InvestorDefault = investorDefault,
                FeesPaid = notes.Tranches.Sum(s => s.FeesPaid),
                InterestPaid = notes.Tranches.Sum(s => s.Classes[0].InterestPaid),
                AccountEarnings = earnings,
                SegregatedFinance = segregated,
                ExcessDollars = notes.Excess,
                UncoveredDefault = notes.UncoveredDefault,
                UnchargedDefault = notes.UnchargedDefault,
                PrincipalReallocated = notes.PrincipalReallocated,
                FinanceYieldPct = financeYield,
                NetLossPct = netLoss,
                PortfolioYieldPct = financeYield - netLoss,
                CouponPct = coupon,
                BaseRatePct = coupon + servicing,
                ExcessSpreadPct = financeYield - netLoss - coupon - servicing
            };
        }

        /// <summary>The series' closing date, in the one month its interest runs from there.</summary>
        private static DateTime? ClosingInThisPeriod(SeriesRun s, RevolvingPeriodDates dates)
        {
            return s.Config is { ClosingDate: { } closing, FirstPayDate: { } first }
                   && (dates.DistributionDate.Year, dates.DistributionDate.Month) == (first.Year, first.Month)
                ? closing
                : null;
        }

        /// <summary>
        ///     With a closing date, the first distribution's accrual is the whole first period however
        ///     many months away it is, so the months before it add nothing.
        /// </summary>
        private static bool AwaitsFirstPeriod(SeriesRun s, RevolvingPeriodDates dates)
        {
            return s.Config.ClosingDate != null && !RunsWaterfall(s, dates);
        }

        /// <param name="accrues">false reads the month's rate and adds no interest.</param>
        private void AccrueInterest(ClassRun c, RevolvingPeriodDates dates, DateTime? closing, bool accrues)
        {
            var config = c.Config;
            c.IndexValue = config.Index is { } index ? _rates.GetRate(index, dates.PeriodStart) : null;
            c.RatePct = c.IndexValue is { } value ? value + config.Margin : config.Coupon!.Value;
            c.InterestAccrued = accrues
                ? c.Outstanding * c.RatePct / 100.0 * AccrualFraction(config.Basis, dates, closing)
                : 0.0;
            c.InterestDue += c.InterestAccrued;
        }

        /// <summary>
        ///     A month's accrual. From closing, 30/360 counts to the stated distribution day, not the
        ///     business day it rolled to, which is how a printed first period runs.
        /// </summary>
        private double AccrualFraction(AccrualBasis basis, RevolvingPeriodDates dates, DateTime? closing)
        {
            if (closing is not { } from)
                return basis == AccrualBasis.Actual360 ? dates.AccrualDays / 360.0 : 1.0 / 12.0;

            if (basis == AccrualBasis.Actual360)
                return (dates.DistributionDate - from).Days / 360.0;

            // On 30/360 a closing on the 31st counts from the 30th.
            var pay = dates.DistributionDate;
            var days = 360 * (pay.Year - from.Year) + 30 * (pay.Month - from.Month)
                                                    + (_run.DistributionDay - Math.Min(from.Day, 30));
            return days / 360.0;
        }

        /// <summary>
        ///     A class bears its share of the series' default amount. A tranche's is its share of the
        ///     month's numerator, which can stand after its current amount has gone.
        /// </summary>
        private void AllocateDefault(SeriesRun s, double investorDefault, double invested)
        {
            if (Delinked)
            {
                s.Classes[0].DefaultDue += investorDefault;
                return;
            }

            if (invested <= 0)
                return;

            foreach (var c in s.Classes)
                c.DefaultDue += investorDefault * c.Invested / invested;
        }

        private double RequiredReserve(SeriesRun s, int period)
        {
            if (Finished(s))
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
            IReadOnlyList<TriggerReading> readings, RevolvingPeriodDates dates, NotePeriod? notes)
        {
            if (s.Phase == SeriesPhase.Accumulating)
                s.Funding.CloseMonth(s.ControlledThisMonth, s.FundingRoom);
            else
                s.Funding.CloseMonth(0.0, 0.0);

            var yieldPct = RevolvingMetrics.PortfolioYieldPct(
                (s.ReallocatedFinance ?? open.Finance) + open.Earnings + s.ExcessIn - open.InvestorDefault,
                open.Invested, dates.Days);

            // Interest and fees annualize on their own days, which differ only in a series' first
            // month. That reproduces all 11 first months from 2023 to 2026 on the American Express
            // trust to the 4 decimals printed.
            var baseRatePct = RevolvingMetrics.BaseRatePct(s.Classes.Sum(c => c.InterestAccrued), open.Invested, s.InterestDays)
                              + RevolvingMetrics.BaseRatePct(s.FeesAccrued, open.Invested, dates.AccrualDays);

            // A tranche reports the series' rates, which are the ones its tests read.
            if (notes != null)
            {
                yieldPct = notes.PortfolioYieldPct;
                baseRatePct = notes.BaseRatePct;
            }
            else if (open.Invested >= SeriesRun.Cent && s.InterestDays > 0)
            {
                Remember(s.History, TriggerMetric.PortfolioYield, yieldPct);
                Remember(s.History, TriggerMetric.BaseRate, baseRatePct);
                Remember(s.History, TriggerMetric.ExcessSpread, yieldPct - baseRatePct);
            }

            foreach (var c in s.Classes)
                _classRows[c].Add(new ClassPeriod
                {
                    Period = dates.Period,
                    DistributionDate = dates.DistributionDate,
                    BeginBalance = c.BeginInvested,
                    EndBalance = c.Invested,
                    Factor = FactorOf(c),
                    InterestAccrued = c.InterestAccrued,
                    InterestPaid = c.InterestPaid,
                    InterestDueUnpaid = c.InterestDue,
                    PrincipalPaid = c.PrincipalPaid,
                    Writedown = c.NetWritedown,
                    CumWritedown = c.WrittenDown,
                    CouponPct = c.RatePct,
                    IndexValue = c.IndexValue,
                    Margin = c.Config.Margin,
                    AccrualDays = s.InterestDays
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
                Triggers = readings,
                FrozenPrincipalPct = s.FrozenPrincipalPct * 100.0,
                FrozenNumerator = s.FrozenNumerator,
                Tranche = notes == null ? null : TrancheRow(s)
            });

            if (notes != null)
            {
                s.PriorNumerator = s.OpeningAdjusted;
                s.PriorFunding = s.OpeningFunding;
            }

            TrackPhase(s, dates);

            if (s.Phase == SeriesPhase.Accumulating && s.Config.ExpectedDate is { } expected
                                                    && RevolvingWaterfall.OnOrAfterMonth(dates.DistributionDate, expected))
                s.Machine.MoveTo(s.PaidInFull ? SeriesPhase.Amortizing : SeriesPhase.EarlyAmortization);
        }

        private static TranchePeriod TrancheRow(SeriesRun s)
        {
            return new TranchePeriod
            {
                Numerator = s.Numerator,
                OpeningNominalLiquidation = s.OpeningAdjusted,
                NextNumerator = s.OpeningAdjusted,
                NextPriorFunding = s.OpeningFunding,
                RequiredSubordination = new Dictionary<string, double>(s.Required),
                UsageThisMonth = new Dictionary<string, double>(s.UsageThisMonth),
                Usage = new Dictionary<string, double>(s.Usage)
            };
        }

        /// <summary>A rest-of-trust block can carry a class with no balance, which has no factor.</summary>
        private static double FactorOf(ClassRun c)
        {
            var original = c.Config.OriginalBalance ?? c.Config.Balance;
            return original > 0 ? c.Invested / original : 0.0;
        }

        private void TrackPhase(SeriesRun s, RevolvingPeriodDates dates)
        {
            var path = _phasePaths[s];
            if (path.Count == 0 || path[^1].Phase != s.Phase)
                path.Add(new PhaseMove(s.Phase, dates.Period, dates.DistributionDate));
        }

        private static void Remember(Dictionary<TriggerMetric, List<double>> history, TriggerMetric metric, double value)
        {
            if (!history.TryGetValue(metric, out var months))
                history[metric] = months = new List<double>();
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

            if (Delinked && _run.ActualPeriods.All(a => a is { InterestAccountEarnings: 0, PrincipalAccountEarnings: 0 }))
                disclosures.Add("No account earnings were supplied, so cash waiting in the funding accounts earned 0.");

            // A tranche with a closing date joins in this run, so the month before has nothing to say.
            foreach (var s in _run.Series.Where(s => Delinked && s.ClosingDate == null
                                                                && (s.PriorNumerator == null || s.PriorFunding == null)))
                disclosures.Add($"Series '{s.Name}' states no priorNumerator or priorFunding, so the month before "
                                + "the first period took its statement amount and an empty funding account.");

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
