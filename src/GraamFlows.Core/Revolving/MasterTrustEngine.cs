using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Util.Calender.Calendars;

namespace GraamFlows.Revolving;

/// <summary>
///     Runs a master trust of note series over one installment pool, one month at a time.
///     <para>
///         Each month the pool rolls (<see cref="InstallmentPoolLedger" />) and closes with the month's
///         additions. Each series then values the whole pool at its own discount rate:
///         <c>discounted invested = invested × round(pool / PV, 5)</c> as the month opens and at its
///         close, plus its share of the ineligible and excess concentration amounts, is its adjusted
///         invested amount. Its allocation numerator is that amount weighted by days across the month's
///         financing adjustment dates, and its allocation percentage is the numerator over the greater
///         of all numerators and the time-weighted pool. The series takes that percentage of the
///         trust's collections, of the servicing fee and of the pool's present value at its rate (its
///         allocated pool balance), and runs its own waterfall on the payment date.
///     </para>
///     <para>
///         The pool tests act on the payment date for every series. The payment tests act from the
///         next one. While revolving, what the waterfalls leave for the certificates buys the next
///         month's additions.
///     </para>
/// </summary>
public static class MasterTrustEngine
{
    private const double Cent = 0.005;

    public static MasterTrustResult Run(MasterTrustRun run, IRateProvider rates)
    {
        MasterTrustValidator.Validate(run);
        return new Loop(run, rates).Execute();
    }

    /// <summary>Half away from zero, as a statement rounds.</summary>
    public static double Round(double value, int decimals)
    {
        return (double)Math.Round((decimal)value, decimals, MidpointRounding.AwayFromZero);
    }

    public static double Cents(double value)
    {
        return Round(value, 2);
    }

    /// <summary>30/360 days between two dates, the US convention.</summary>
    public static int Days360(DateTime from, DateTime to)
    {
        var d1 = Math.Min(from.Day, 30);
        var d2 = to.Day == 31 && d1 == 30 ? 30 : to.Day;
        return (to.Year - from.Year) * 360 + (to.Month - from.Month) * 30 + (d2 - d1);
    }

    private sealed class ClassState
    {
        public required RevolvingClass Config { get; init; }
        public required string Tier { get; init; }
        public double Balance { get; set; }
        public double Shortfall { get; set; }

        /// <summary>The fixed coupon now, in percent: the stated one until a printed reset.</summary>
        public double? Coupon { get; set; }

        public double RatePct { get; set; }
        public double? IndexValue { get; set; }
        public int AccrualDays { get; set; }
        public double Accrued { get; set; }
        public double Due { get; set; }
        public double InterestPaid { get; set; }
        public double PrincipalPaid { get; set; }
        public double Begin { get; set; }
    }

    private sealed class SeriesState
    {
        public required MasterTrustSeries Config { get; init; }
        public required SeriesPhaseMachine Phase { get; init; }
        public required List<ClassState> Classes { get; init; }
        public string? Event { get; set; }
        public string? PendingEvent { get; set; }
        public double Reserve { get; set; }
        public double Pfa { get; set; }
        public double? FrozenNotes { get; set; }
        public double? FrozenNumerator { get; set; }
        public double PfaReleaseDue { get; set; }
        public double Rate { get; set; }
        public double PrivateNotes { get; set; }
        public bool Joined { get; set; }
        public string? Ended { get; set; }
        public double LastNumerator { get; set; }

        public bool Amortizing => Phase.Phase != SeriesPhase.Revolving;

        public double Notes => Config.Private ? PrivateNotes : Classes.Sum(c => c.Balance);

        /// <summary>Notes plus the target OC: on the notes while revolving, on the frozen notes after.</summary>
        public double Invested(double notes)
        {
            return notes + TargetOc(notes);
        }

        public double TargetOc(double notes)
        {
            var on = Amortizing ? FrozenNotes ?? notes : notes;
            var p = Config.OcPct;
            return notes <= 0.0 ? 0.0 : Cents(p * on / (1.0 - p));
        }
    }

    /// <summary>What a series holds as a month is valued, before its payment date.</summary>
    private sealed class Valued
    {
        public required SeriesState S { get; init; }
        public bool OpenOutstanding { get; init; }
        public double NotesStart { get; init; }
        public double NotesClose { get; init; }
        public double InvestedStart { get; init; }
        public double InvestedClose { get; init; }
        public double PvStart { get; init; }
        public double PvEnd { get; init; }
        public double? OwnPvEnd { get; init; }
        public double RatioStart { get; set; }
        public double RatioEnd { get; set; }
        public double DiscStart { get; set; }
        public double DiscEnd { get; set; }
        public double ShareStart { get; set; }
        public double ShareEnd { get; set; }
        public double ConcStart { get; set; }
        public double ConcEnd { get; set; }
        public double IncStart { get; set; }
        public double IncEnd { get; set; }
        public double AdjStart => DiscStart + IncStart;
        public double AdjEnd => DiscEnd + IncEnd;
        public double Numerator { get; set; }
        public double? OwnNumerator { get; set; }
        public double Alloc { get; set; }
    }

    private sealed class Loop
    {
        private readonly MasterTrustRun _run;
        private readonly MasterTrustConfig _trust;
        private readonly IRateProvider _rates;
        private readonly InstallmentPoolLedger.Stepper _pool;
        private readonly List<SeriesState> _series;
        private readonly List<string> _disclosures = new();
        private readonly List<MasterTrustPeriod> _rows = new();
        private readonly List<(string, SeriesPhase, int, DateTime, string?)> _path = new();
        private readonly List<double> _writeOffs;
        private readonly List<double> _delinquency;
        private readonly Dictionary<string, double> _concentration;
        private readonly HashSet<string> _heldConcentration = new();
        private double _certificateCash;

        public Loop(MasterTrustRun run, IRateProvider rates)
        {
            _run = run;
            _trust = run.Trust;
            _rates = rates;
            var seriesRates = _trust.Series.Select(s => s.DiscountRate).Distinct()
                .Where(r => !run.Pool.DiscountRates.Contains(r)).ToList();
            var poolRun = seriesRates.Count == 0
                ? run.Pool
                : run.Pool with { DiscountRates = run.Pool.DiscountRates.Concat(seriesRates).ToList() };
            _pool = InstallmentPoolLedger.Step(poolRun);
            _series = _trust.Series.Select(s => new SeriesState
            {
                Config = s,
                Phase = new SeriesPhaseMachine(s.State.Phase),
                Classes = s.Classes.Select(c => new ClassState
                {
                    Config = c,
                    Tier = s.Tiers.First(t => t.Classes.Contains(c.Key)).Name,
                    Balance = c.Balance,
                    Shortfall = c.InterestShortfall,
                    Coupon = c.Coupon
                }).ToList(),
                Event = s.State.Event,
                Reserve = s.State.Reserve,
                Pfa = s.State.Pfa,
                FrozenNotes = s.State.FrozenNotes,
                FrozenNumerator = s.State.FrozenNumerator,
                PfaReleaseDue = s.State.PfaReleaseDue,
                Rate = s.DiscountRate,
                PrivateNotes = s.PrivateNotes
            }).ToList();
            _writeOffs = run.State.WriteOffHistory.ToList();
            _delinquency = run.State.DelinquencyHistory.ToList();
            _concentration = run.State.Concentration.ToDictionary(k => k.Key, k => k.Value);
            _certificateCash = run.State.CertificateCash;
        }

        private RoundingRules R => new(_trust.Rounding);

        public MasterTrustResult Execute()
        {
            var calendar = RevolvingCalendar.Build(_run.Pool.StatementDate, _run.DistributionDay, _run.Pool.Periods,
                _trust.Calendar == OwnerTrustCalendar.FederalReserve
                    ? new UnitedStates(UnitedStates.Market.FederalReserve)
                    : null);
            foreach (var dates in calendar)
                _rows.Add(Month(dates));

            Disclose();
            return new MasterTrustResult
            {
                Periods = _rows,
                PhasePath = _path,
                Closing = new MasterTrustState
                {
                    WriteOffHistory = _writeOffs.ToList(),
                    DelinquencyHistory = _delinquency.ToList(),
                    Concentration = new Dictionary<string, double>(_concentration),
                    CertificateCash = _certificateCash
                },
                ClosingSeries = _series.Select(s => new MasterTrustSeriesClosing(
                    s.Config.Name,
                    s.Ended != null
                        ? null
                        : new MasterTrustSeriesState
                        {
                            Phase = s.Phase.Phase,
                            Event = s.Event ?? s.PendingEvent,
                            Reserve = s.Reserve,
                            Pfa = s.Pfa,
                            FrozenNotes = s.FrozenNotes,
                            FrozenNumerator = s.FrozenNumerator,
                            PfaReleaseDue = s.PfaReleaseDue
                        },
                    s.Classes.Select(c => new OwnerTrustClassClosing(c.Config.Key, c.Balance, c.Shortfall)).ToList(),
                    s.Notes,
                    s.Rate,
                    s.Ended)).ToList(),
                ClosingPool = _pool.Closing(),
                Disclosures = _pool.Disclosures().Where(d => !d.StartsWith("No reinvestment"))
                    .Concat(_disclosures).Distinct().ToList()
            };
        }

        private readonly record struct RoundingRules(MasterTrustRounding Rules)
        {
            public double Ratio(double face, double pv)
            {
                return pv > 0.0 ? Round(face / pv, Rules.DiscountRatio) : 0.0;
            }

            public double Share(double value)
            {
                return Round(value, Rules.SeriesShare);
            }

            public double Allocation(double value)
            {
                return Round(value, Rules.Allocation);
            }
        }

        private void Trip(SeriesState s, string name, RevolvingPeriodDates dates)
        {
            if (s.Event != null)
                return;
            s.Event = name;
            if (s.Phase.TryEnterEarlyAmortization())
            {
                // Fixed as of the last day of revolving.
                s.FrozenNotes ??= s.Notes;
                s.FrozenNumerator ??= s.LastNumerator;
                _path.Add((s.Config.Name, SeriesPhase.EarlyAmortization, dates.Period, dates.DistributionDate, name));
            }
        }

        private bool JoinsBy(SeriesState s, DateTime periodEnd)
        {
            return s.Config.ClosingDate is not { } closing || closing.Date <= periodEnd;
        }

        private bool NewIn(SeriesState s, RevolvingPeriodDates dates)
        {
            return s.Config.ClosingDate is { } closing && closing.Date >= dates.PeriodStart
                                                       && closing.Date <= dates.PeriodEnd && !s.Joined;
        }

        private MasterTrustPeriod Month(RevolvingPeriodDates dates)
        {
            var t = dates.Period;
            var replayed = t < _run.Pool.ActualPeriods.Count;
            var forecastMonth = t - _run.Pool.ActualPeriods.Count;
            var actual = t < _run.ActualPeriods.Count ? _run.ActualPeriods[t] : null;
            var r = R;

            // A series redeemed between payment dates leaves before this one: the deposit pays its notes
            // outside the trust's funds, and it takes no share of this month.
            foreach (var s in _series.Where(s => s.Ended == null && LeftBefore(s, dates)))
                Leave(s, dates);

            // A series joins in the month that holds its closing; one that has ended stays out.
            var live = _series.Where(s => s.Ended == null && JoinsBy(s, dates.PeriodEnd)).ToList();
            var fresh = live.Where(s => NewIn(s, dates)).ToHashSet();
            foreach (var s in live)
            {
                var printed = actual?.Series.GetValueOrDefault(s.Config.Name);
                if (printed?.DiscountRate is { } rate)
                    s.Rate = rate;
                if (s.PendingEvent != null)
                {
                    Trip(s, s.PendingEvent, dates);
                    s.PendingEvent = null;
                }

                if (s.Config.AnticipatedRedemption is { } ard && !s.Amortizing && s.Event == null
                    && OnOrAfter(dates.DistributionDate, ard) && !RedeemsNow(s, dates, printed))
                    Trip(s, "ANTICIPATED_REDEMPTION", dates);
            }

            // The pool as the month opens, and each series' present value of it.
            var beginningPool = _pool.Face;
            var openValues = live.Select(s => s.Rate).Distinct().ToDictionary(rate => rate, rate => _pool.Value(rate));
            var pvStart = live.ToDictionary(s => s, s =>
                actual?.Series.GetValueOrDefault(s.Config.Name)?.PresentValueStart ?? openValues[s.Rate]);

            var month = _pool.Roll(dates);
            var purchases = new List<InstallmentPurchase>();
            var additionsCash = 0.0;
            var certificateFace = 0.0;
            // The trust buys only while a series revolves: once every series amortizes, the pool runs off.
            if (!replayed && live.Any(s => !s.Amortizing))
                (additionsCash, certificateFace) = Acquire(dates, beginningPool, purchases);
            else
                additionsCash = month.Purchases.Sum(p => p.Cash ?? 0.0);
            var poolRow = _pool.Close(month, purchases);
            var endingPool = _pool.Face;
            var closeValues = openValues.Keys.ToDictionary(rate => rate, rate => _pool.Value(rate));

            // The financing adjustment dates and what each holds.
            var fads = Fads(dates, actual, beginningPool, endingPool, forecastMonth, out var concStartByGroup,
                out var concByFad);
            var twPool = Cents(fads.Sum(f => f.Days * f.PoolBalance) / fads.Sum(f => (double)f.Days));

            // Each series values the pool at its own rate, as the month opens and at its close.
            var valued = live.Select(s =>
            {
                var printed = actual?.Series.GetValueOrDefault(s.Config.Name);
                var own = closeValues[s.Rate];
                var notesStart = fresh.Contains(s) ? 0.0 : s.Notes;
                if (s.Config.Private && printed?.NoteBalance is { } printedNotes)
                    s.PrivateNotes = printedNotes;
                var notesClose = s.Notes;
                return new Valued
                {
                    S = s,
                    OpenOutstanding = !fresh.Contains(s),
                    NotesStart = notesStart,
                    NotesClose = notesClose,
                    InvestedStart = notesStart > 0.0 ? s.Invested(notesStart) : 0.0,
                    InvestedClose = notesClose > 0.0 ? s.Invested(notesClose) : 0.0,
                    PvStart = pvStart[s],
                    PvEnd = printed?.PresentValueEnd ?? own,
                    OwnPvEnd = printed?.PresentValueEnd != null ? own : null
                };
            }).ToList();

            foreach (var v in valued)
            {
                v.RatioStart = v.OpenOutstanding ? r.Ratio(beginningPool, v.PvStart) : 0.0;
                v.RatioEnd = r.Ratio(endingPool, v.PvEnd);
                v.DiscStart = Cents(v.InvestedStart * v.RatioStart);
                v.DiscEnd = Cents(v.InvestedClose * v.RatioEnd);
                v.ConcStart = concStartByGroup.GetValueOrDefault(v.S.Config.ConcentrationGroup);
                v.ConcEnd = concByFad[^1].GetValueOrDefault(v.S.Config.ConcentrationGroup);
            }

            var sumDiscStart = valued.Sum(v => v.DiscStart);
            var sumDiscEnd = valued.Sum(v => v.DiscEnd);
            foreach (var v in valued)
            {
                v.ShareStart = v.OpenOutstanding ? r.Share(v.DiscStart / Math.Max(beginningPool, sumDiscStart)) : 0.0;
                v.ShareEnd = r.Share(v.DiscEnd / Math.Max(endingPool, sumDiscEnd));
                v.IncStart = Cents(v.ConcStart * v.ShareStart);
                v.IncEnd = Cents(v.ConcEnd * v.ShareEnd);
            }

            // The allocation numerator: the adjusted invested amount weighted by days across the dates.
            Numerators(valued, fads, concByFad, fresh, actual);
            var sumNumerators = valued.Sum(v => v.Numerator);
            var denominator = Math.Max(sumNumerators, twPool);
            foreach (var v in valued)
                v.Alloc = denominator > 0.0 ? r.Allocation(v.Numerator / denominator) : 0.0;

            var totalFunds = month.Collections + month.RepurchaseCash + (actual?.OtherFunds ?? 0.0);
            var requiredPool = valued.Sum(v => v.AdjEnd)
                               - valued.Sum(v => Math.Min(v.S.Pfa, v.S.Config.PfaLimitPct * v.NotesClose));
            var computedDeficit = Math.Max(0.0, Cents(requiredPool - endingPool));
            var deficit = actual?.PoolBalanceDeficit ?? computedDeficit;
            var allocSum = valued.Sum(v => v.Alloc);
            var transferorPct = deficit > Cent ? 0.0 : Math.Max(0.0, 1.0 - allocSum);
            var transferorAllocation = Cents(transferorPct * totalFunds);

            // The pool tests act on this payment date, for every series.
            var tests = new List<OwnerTrustTestReading>();
            ReadPoolTests(dates, month, beginningPool, actual, forecastMonth, live, tests);

            var seriesRows = new List<MasterTrustSeriesPeriod>();
            foreach (var v in valued)
            {
                var printed = actual?.Series.GetValueOrDefault(v.S.Config.Name);
                seriesRows.Add(Pay(v, dates, beginningPool, endingPool, totalFunds, deficit, printed, fresh));
            }

            // After the distributions: the pool balance deficit, as the PFA deposits left it.
            var deficitAfter = Math.Max(0.0, Cents(valued.Where(v => v.S.Ended == null).Sum(v => v.AdjEnd)
                                                   - valued.Where(v => v.S.Ended == null)
                                                       .Sum(v => Math.Min(v.S.Pfa, v.S.Config.PfaLimitPct * v.S.Notes))
                                                   - endingPool));
            tests.Add(new OwnerTrustTestReading("POOL_BALANCE_DEFICIT", deficitAfter, 0.0, deficitAfter > Cent, false));
            for (var i = 0; i < valued.Count; i++)
            {
                var v = valued[i];
                if (v.S.Ended == null && v.S.Config.Tests.PoolBalanceDeficit && deficitAfter > Cent)
                    v.S.PendingEvent ??= "POOL_BALANCE_DEFICIT";
            }

            foreach (var s in live)
                s.Joined = true;

            var classR = seriesRows.Sum(x => x.ClassR);
            _certificateCash = classR + transferorAllocation;

            return new MasterTrustPeriod
            {
                Period = t,
                PeriodEnd = dates.PeriodEnd,
                DistributionDate = dates.DistributionDate,
                Replayed = replayed,
                BeginningPool = beginningPool,
                EndingPool = endingPool,
                Fads = fads,
                TimeWeightedPool = twPool,
                PresentValues = openValues.Keys.OrderBy(x => x).Select(rate => new InstallmentValuation
                {
                    Rate = rate,
                    Beginning = openValues[rate],
                    Ending = closeValues[rate]
                }).ToList(),
                TotalFunds = totalFunds,
                SumNumerators = sumNumerators,
                AllocationDenominator = denominator,
                TransferorPct = transferorPct,
                TransferorAllocation = transferorAllocation,
                RequiredPoolBalance = requiredPool,
                PoolBalanceDeficit = deficit,
                ComputedPoolBalanceDeficit = actual?.PoolBalanceDeficit != null ? computedDeficit : null,
                AdditionsFace = poolRow.AdditionsFace,
                AdditionsCash = additionsCash,
                CertificateFundedFace = certificateFace,
                CertificateCash = _certificateCash,
                Tests = tests,
                Series = seriesRows,
                Pool = poolRow
            };
        }

        private static bool OnOrAfter(DateTime paymentDate, DateTime month)
        {
            return paymentDate >= new DateTime(month.Year, month.Month, 1);
        }

        private bool RedeemsNow(SeriesState s, RevolvingPeriodDates dates, MasterTrustSeriesActual? printed)
        {
            if (printed?.RedemptionDeposit != null)
                return true;
            return s.Config.RedemptionDate is { } date && OnPaymentDate(date, dates);
        }

        /// <summary>A date from the payment date's nominal day to the business day it rolled to.</summary>
        private bool OnPaymentDate(DateTime date, RevolvingPeriodDates dates)
        {
            var paid = dates.DistributionDate.Date;
            var nominal = new DateTime(paid.Year, paid.Month, Math.Min(_run.DistributionDay,
                DateTime.DaysInMonth(paid.Year, paid.Month)));
            return date.Date >= nominal && date.Date <= paid;
        }

        /// <summary>Redeemed on a date before this payment date that is not itself a payment date.</summary>
        private bool LeftBefore(SeriesState s, RevolvingPeriodDates dates)
        {
            return s.Config.RedemptionDate is { } date && date.Date < dates.DistributionDate.Date
                                                       && !OnPaymentDate(date, dates);
        }

        /// <summary>
        ///     A series redeemed between payment dates: the certificateholder's deposit pays the notes at
        ///     par with their interest outside the trust's funds, and its accounts go with it.
        /// </summary>
        private void Leave(SeriesState s, RevolvingPeriodDates dates)
        {
            foreach (var c in s.Classes)
            {
                c.Balance = 0.0;
                c.Shortfall = 0.0;
            }

            s.PrivateNotes = 0.0;
            s.Reserve = 0.0;
            s.Pfa = 0.0;
            s.PfaReleaseDue = 0.0;
            s.Ended = "REDEEMED";
            _path.Add((s.Config.Name, s.Phase.Phase, dates.Period, s.Config.RedemptionDate!.Value.Date, s.Ended));
        }

        /// <summary>
        ///     The month's financing adjustment dates. A replayed month states them; otherwise the pool is
        ///     valued on the month's first day and its last, so the opening holds every day but the last.
        ///     Each date holds until the next; the last holds through the month's last day.
        /// </summary>
        private List<MasterTrustFadReading> Fads(RevolvingPeriodDates dates, MasterTrustActual? actual,
            double beginningPool, double endingPool, int forecastMonth,
            out Dictionary<string, double> concStart, out List<Dictionary<string, double>> concByFad)
        {
            var stated = actual?.Fads ?? Array.Empty<MasterTrustFad>();
            var list = stated.Count >= 2
                ? stated.ToList()
                : new List<MasterTrustFad>
                {
                    new() { Date = dates.PeriodStart, PoolBalance = beginningPool },
                    new() { Date = dates.PeriodEnd, PoolBalance = endingPool }
                };

            concStart = new Dictionary<string, double>(_concentration);
            concByFad = new List<Dictionary<string, double>>();
            var held = new Dictionary<string, double>(_concentration);
            for (var i = 0; i < list.Count; i++)
            {
                foreach (var (group, amount) in list[i].Concentration)
                    held[group] = amount;
                if (i == list.Count - 1 && stated.Count < 2 && forecastMonth >= 0)
                    foreach (var (group, path) in _trust.Concentration)
                        if (path.Count > 0)
                            held[group] = path[Math.Min(forecastMonth, path.Count - 1)];
                if (i == 0)
                    concStart = new Dictionary<string, double>(held);
                concByFad.Add(new Dictionary<string, double>(held));
            }

            foreach (var group in _trust.Series.Select(s => s.ConcentrationGroup).Distinct())
                if (!list.Any(f => f.Concentration.ContainsKey(group))
                    && !(forecastMonth >= 0 && _trust.Concentration.TryGetValue(group, out var p) && p.Count > 0))
                    _heldConcentration.Add(group);

            foreach (var (group, amount) in concByFad[^1])
                _concentration[group] = amount;

            var readings = new List<MasterTrustFadReading>(list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                var next = i + 1 < list.Count ? list[i + 1].Date.Date : dates.PeriodEnd.AddDays(1);
                readings.Add(new MasterTrustFadReading(list[i].Date.Date, (next - list[i].Date.Date).Days,
                    list[i].PoolBalance));
            }

            return readings;
        }

        /// <summary>
        ///     Each series' allocation numerator. An outstanding revolving series weights its adjusted
        ///     invested amount by days: the opening amount on the first date, the closing one on the last,
        ///     and between them the amount at that date's pool, valued at the closing discount ratio (no
        ///     present value is known between the month's ends). A series new this month takes its
        ///     closing amount; an amortizing one, its amount on the last day of revolving.
        /// </summary>
        private void Numerators(List<Valued> valued, List<MasterTrustFadReading> fads,
            List<Dictionary<string, double>> concByFad, HashSet<SeriesState> fresh, MasterTrustActual? actual)
        {
            var r = R;
            var n = fads.Count;
            var adjusted = valued.ToDictionary(v => v, _ => new double[n]);
            for (var i = 0; i < n; i++)
            {
                if (i == 0)
                {
                    foreach (var v in valued)
                        adjusted[v][0] = v.AdjStart;
                    continue;
                }

                if (i == n - 1)
                {
                    foreach (var v in valued)
                        adjusted[v][i] = v.AdjEnd;
                    continue;
                }

                var pool = fads[i].PoolBalance;
                var disc = valued.ToDictionary(v => v, v =>
                    v.OpenOutstanding || v.S.Config.ClosingDate!.Value.Date <= fads[i].Date
                        ? Cents(v.InvestedClose * v.RatioEnd)
                        : 0.0);
                var sum = disc.Values.Sum();
                foreach (var v in valued)
                {
                    var share = r.Share(disc[v] / Math.Max(pool, sum));
                    adjusted[v][i] = disc[v] + Cents(concByFad[i].GetValueOrDefault(v.S.Config.ConcentrationGroup) * share);
                }
            }

            var days = fads.Sum(f => (double)f.Days);
            foreach (var v in valued)
            {
                double own;
                if (v.S.Amortizing && v.S.FrozenNumerator is { } frozen)
                    own = frozen;
                else if (fresh.Contains(v.S))
                    own = v.AdjEnd;
                else
                    own = Cents(adjusted[v].Select((a, i) => a * fads[i].Days).Sum() / days);

                var printed = actual?.Series.GetValueOrDefault(v.S.Config.Name)?.AllocationNumerator;
                v.Numerator = printed ?? own;
                v.OwnNumerator = printed != null ? own : null;
                if (!v.S.Amortizing)
                    v.S.LastNumerator = v.Numerator;
            }
        }

        /// <summary>
        ///     A forecast month's additions. The cash the certificates were left on the last payment date
        ///     buys level contracts at the stated price. Holding the pool's face, the trust buys back to
        ///     its opening face, paying cash up to what it has and taking the rest through the certificates.
        /// </summary>
        private (double Cash, double CertificateFace) Acquire(RevolvingPeriodDates dates, double openingFace,
            List<InstallmentPurchase> purchases)
        {
            if (_trust.Acquisition is not { } acquisition)
                return (0.0, 0.0);

            var perFace = acquisition.Terms.Sum(term => term.Weight * CashFor(acquisition.Price,
                InstallmentCohort.LevelSchedule(1.0, term.Term)));
            if (perFace <= 0.0)
                return (0.0, 0.0);

            var cash = Math.Max(0.0, _certificateCash);
            double face;
            double paid;
            if (acquisition.HoldPoolFace)
            {
                face = Math.Max(0.0, openingFace - _pool.Face);
                paid = Math.Min(cash, face * perFace);
            }
            else
            {
                face = cash / perFace;
                paid = cash;
            }

            if (face <= Cent)
                return (0.0, 0.0);
            purchases.AddRange(InstallmentPoolLedger.Loop.Buy(acquisition.Price, acquisition.Terms,
                $"acquired-{dates.Period}", face * perFace));
            _certificateCash -= paid;
            return (paid, face - paid / perFace);
        }

        private static double CashFor(InstallmentPrice price, IReadOnlyList<double> schedule)
        {
            return price.PerFace is { } perFace
                ? perFace * schedule.Sum()
                : InstallmentValuationMath.PresentValue(schedule, price.PvRate!.Value);
        }

        private void ReadPoolTests(RevolvingPeriodDates dates, InstallmentPoolLedger.PoolMonth month,
            double beginningPool, MasterTrustActual? actual, int forecastMonth, List<SeriesState> live,
            List<OwnerTrustTestReading> tests)
        {
            var tested = _trust.Tests;
            _writeOffs.Add(beginningPool > 0.0 ? month.WriteOffs / beginningPool : 0.0);
            var delinquency = actual?.Delinquency ?? DelinquencyDriver(forecastMonth);
            _delinquency.Add(delinquency);

            var tripped = new List<string>();
            if (tested.WriteOffLimit is { } writeOffLimit)
            {
                double? computed = _writeOffs.Count >= tested.WriteOffWindow
                    ? _writeOffs.TakeLast(tested.WriteOffWindow).Sum() * tested.WriteOffMultiplier
                    : null;
                var value = actual?.WriteOffRatio ?? computed;
                var breached = value > writeOffLimit;
                tests.Add(new OwnerTrustTestReading("WRITE_OFF_RATIO", value, writeOffLimit, breached,
                    actual?.WriteOffRatio != null, computed));
                if (breached)
                    tripped.Add("WRITE_OFF_RATIO");
            }

            if (tested.DelinquencyLimit is { } delinquencyLimit)
            {
                double? computed = _delinquency.Count >= tested.DelinquencyWindow
                    ? _delinquency.TakeLast(tested.DelinquencyWindow).Average()
                    : null;
                var value = actual?.DelinquencyRatio ?? computed;
                var breached = value > delinquencyLimit;
                tests.Add(new OwnerTrustTestReading("DELINQUENCY_RATIO", value, delinquencyLimit, breached,
                    actual?.DelinquencyRatio != null, computed));
                if (breached)
                    tripped.Add("DELINQUENCY_RATIO");
            }

            foreach (var name in tripped)
            foreach (var s in live)
                Trip(s, name, dates);
        }

        private double DelinquencyDriver(int forecastMonth)
        {
            var path = _trust.Delinquency;
            if (forecastMonth >= 0 && path.Count > 0)
                return path[Math.Min(forecastMonth, path.Count - 1)];
            return _delinquency.Count > 0 ? _delinquency[^1] : 0.0;
        }

        /// <summary>One series' payment date: its share of the trust, then its own waterfall.</summary>
        private MasterTrustSeriesPeriod Pay(Valued v, RevolvingPeriodDates dates, double beginningPool,
            double endingPool, double totalFunds, double deficit, MasterTrustSeriesActual? printed,
            HashSet<SeriesState> fresh)
        {
            var s = v.S;
            var config = s.Config;
            var funds = Cents(v.Alloc * totalFunds);
            var allocatedPool = Cents(v.Alloc * v.PvEnd);
            var servicing = Cents(v.Alloc * _trust.ServicingRate / 12.0 * beginningPool);
            var tests = new List<OwnerTrustTestReading>();

            if (printed?.Event is { } stated)
            {
                tests.Add(new OwnerTrustTestReading(stated, null, null, true, true));
                Trip(s, stated, dates);
            }

            var row = new MasterTrustSeriesPeriod
            {
                Name = config.Name,
                Private = config.Private,
                DiscountRate = s.Rate,
                NotesStart = v.NotesStart,
                TargetOcStart = v.NotesStart > 0.0 ? s.TargetOc(v.NotesStart) : 0.0,
                InvestedStart = v.InvestedStart,
                PresentValueStart = v.PvStart,
                PresentValueEnd = v.PvEnd,
                ComputedPresentValueEnd = v.OwnPvEnd,
                DiscountRatioStart = v.RatioStart,
                DiscountRatioEnd = v.RatioEnd,
                DiscountedInvestedStart = v.DiscStart,
                DiscountedInvestedEnd = v.DiscEnd,
                SeriesShareStart = v.ShareStart,
                SeriesShareEnd = v.ShareEnd,
                ConcentrationStart = v.ConcStart,
                ConcentrationEnd = v.ConcEnd,
                IncrementalStart = v.IncStart,
                IncrementalEnd = v.IncEnd,
                AdjustedInvestedStart = v.AdjStart,
                AdjustedInvestedEnd = v.AdjEnd,
                AllocationNumerator = v.Numerator,
                ComputedAllocationNumerator = v.OwnNumerator,
                AllocationPct = v.Alloc,
                AllocatedPoolBalance = allocatedPool,
                Funds = funds,
                ServicingFee = servicing
            };

            if (config.Private)
            {
                if (RedeemsNow(s, dates, printed))
                {
                    s.PrivateNotes = 0.0;
                    s.Ended = "REDEEMED";
                    _path.Add((config.Name, s.Phase.Phase, dates.Period, dates.DistributionDate, s.Ended));
                }

                return row with
                {
                    Phase = s.Phase.Phase,
                    Event = s.Event,
                    NotesEnd = s.Notes,
                    TargetOcEnd = s.TargetOc(s.Notes),
                    InvestedEnd = s.Notes > 0.0 ? s.Invested(s.Notes) : 0.0,
                    AvailableFunds = funds,
                    Tests = tests
                };
            }

            foreach (var c in s.Classes)
            {
                if (printed != null && printed.Coupons.TryGetValue(c.Config.Key, out var coupon))
                    c.Coupon = coupon;
                c.Begin = c.Balance;
                c.InterestPaid = 0.0;
                c.PrincipalPaid = 0.0;
                Accrue(c, dates, fresh.Contains(s) ? config.ClosingDate : null);
            }

            var redeem = RedeemsNow(s, dates, printed);
            var amortizing = s.Amortizing;
            var notesBegin = s.Notes;

            // Cash in: the funds, a PFA release owed from the last payment date, the reserve above its
            // requirement, and on a redemption the deposit and the whole reserve.
            var available = funds;
            var pfaRelease = s.PfaReleaseDue;
            s.Pfa -= pfaRelease;
            available += pfaRelease;
            s.PfaReleaseDue = 0.0;
            var reserveRequired = redeem ? 0.0 : amortizing ? s.Reserve : config.RequiredReserve;
            var reserveDraw = 0.0;
            var reserveRelease = 0.0;
            var deposit = 0.0;
            if (redeem)
            {
                deposit = printed?.RedemptionDeposit ?? Cents(config.RedemptionPrice * notesBegin);
                available += deposit;
                reserveDraw = s.Reserve;
                available += s.Reserve;
                s.Reserve = 0.0;
                available += s.Pfa;
                s.Pfa = 0.0;
            }
            else if (s.Reserve > reserveRequired + Cent)
            {
                reserveRelease = s.Reserve - reserveRequired;
                s.Reserve -= reserveRelease;
                available += reserveRelease;
            }

            var w = new Waterfall(s, available);
            var fees = printed?.Fees ?? config.MonthlyFees;
            var feesPaid = 0.0;
            var servicingPaid = 0.0;
            var priority = config.Tiers.Select(_ => 0.0).ToArray();
            var priorityTotal = 0.0;
            var regular = 0.0;
            var reserveDeposit = 0.0;
            var pfaDeposit = 0.0;
            var classR = 0.0;
            var toNotes = amortizing || redeem;

            foreach (var step in config.Steps)
                switch (step.Verb)
                {
                    case OwnerTrustVerb.PayFees:
                        feesPaid += w.Spend(fees, step.Verb);
                        break;
                    case OwnerTrustVerb.PayServicing:
                        servicingPaid += w.Spend(servicing, step.Verb);
                        break;
                    case OwnerTrustVerb.PayInterest:
                        PayInterest(s, w, step.Tier!);
                        break;
                    case OwnerTrustVerb.PriorityPrincipal:
                    {
                        var index = config.Tiers.Select(x => x.Name).ToList().IndexOf(step.Tier!);
                        var need = PriorityNeed(s, index, allocatedPool, priorityTotal, dates);
                        var paid = toNotes ? PayPrincipal(s, w, need, step.Verb) : Deposit(s, w, need, step.Verb);
                        if (!toNotes)
                            pfaDeposit += paid;
                        priority[index] += paid;
                        priorityTotal += paid;
                        break;
                    }
                    case OwnerTrustVerb.FundReserve:
                    {
                        var paid = w.Spend(Math.Max(0.0, reserveRequired - s.Reserve), step.Verb);
                        reserveDeposit += paid;
                        s.Reserve += paid;
                        break;
                    }
                    case OwnerTrustVerb.RegularPrincipal:
                    {
                        if (toNotes)
                        {
                            regular += PayPrincipal(s, w, Math.Max(0.0, notesBegin - priorityTotal), step.Verb);
                        }
                        else
                        {
                            var need = Math.Max(0.0, Cents(v.Alloc * deficit) - priorityTotal);
                            var paid = Deposit(s, w, need, step.Verb);
                            regular += paid;
                            pfaDeposit += paid;
                        }

                        break;
                    }
                    case OwnerTrustVerb.PayCertificate:
                        classR += w.Spend(w.Available, step.Verb);
                        break;
                }

            classR += w.Available;
            var principalFromPfa = 0.0;
            if (toNotes && s.Pfa > 0.0 && s.Notes > Cent)
            {
                // Amortizing: what the PFA holds pays the notes on this payment date.
                var pay = Math.Min(s.Pfa, s.Notes);
                s.Pfa -= pay;
                principalFromPfa = PaySequential(s, pay);
            }

            if (toNotes && s.Notes > Cent && s.Reserve >= s.Notes - Cent && !redeem)
            {
                // The reserve pays the notes in full where it can.
                var pay = s.Notes;
                s.Reserve -= Math.Min(s.Reserve, pay);
                reserveDraw += pay;
                PaySequential(s, pay);
            }

            // Revolving: an allocated pool balance above the adjusted invested amount releases PFA cash
            // into the next payment date's funds.
            if (!toNotes && s.Pfa > Cent && allocatedPool > v.AdjEnd)
                s.PfaReleaseDue = Math.Min(s.Pfa, allocatedPool - v.AdjEnd);

            if (s.Notes <= Cent)
            {
                classR += s.Reserve + s.Pfa;
                reserveRelease += s.Reserve;
                s.Reserve = 0.0;
                s.Pfa = 0.0;
                s.Ended = redeem ? "REDEEMED" : "NOTES_PAID";
                _path.Add((config.Name, s.Phase.Phase, dates.Period, dates.DistributionDate, s.Ended));
            }

            ReadPaymentTests(s, v, allocatedPool, endingPool, amortizing, reserveRequired, tests);

            var notesEnd = s.Notes;
            return row with
            {
                Phase = s.Phase.Phase,
                Event = s.Event,
                NotesEnd = notesEnd,
                TargetOcEnd = notesEnd > 0.0 ? s.TargetOc(notesEnd) : 0.0,
                InvestedEnd = notesEnd > 0.0 ? s.Invested(notesEnd) : 0.0,
                PfaRelease = pfaRelease,
                RedemptionDeposit = deposit,
                ReserveDraw = reserveDraw + w.ReserveDrawn,
                AvailableFunds = available,
                Fees = feesPaid,
                ServicingFee = servicingPaid,
                InterestPaid = s.Classes.Sum(c => c.InterestPaid),
                PriorityPrincipal = priority,
                RegularPrincipal = regular,
                PrincipalPaid = s.Classes.Sum(c => c.PrincipalPaid),
                PfaDeposit = pfaDeposit,
                Pfa = s.Pfa,
                ReserveRequired = reserveRequired,
                ReserveDeposit = reserveDeposit,
                ReserveRelease = reserveRelease,
                Reserve = s.Reserve,
                ClassR = classR,
                Tests = tests,
                Classes = s.Classes.Select(c => new OwnerTrustClassPeriod
                {
                    Key = c.Config.Key,
                    BeginBalance = c.Begin,
                    CouponPct = c.RatePct,
                    IndexValue = c.IndexValue,
                    AccrualDays = c.AccrualDays,
                    InterestAccrued = c.Accrued,
                    InterestDue = c.Due,
                    InterestPaid = c.InterestPaid,
                    InterestShortfall = c.Shortfall,
                    PrincipalPaid = c.PrincipalPaid,
                    EndBalance = c.Balance
                }).ToList()
            };
        }

        /// <summary>The payment tests: they act from the next payment date.</summary>
        private void ReadPaymentTests(SeriesState s, Valued v, double allocatedPool, double endingPool,
            bool amortizing, double reserveRequired, List<OwnerTrustTestReading> tests)
        {
            if (s.Ended != null)
                return;
            var tested = s.Config.Tests;
            if (tested.InterestShortfall)
            {
                var shortBy = s.Classes.Sum(c => c.Shortfall);
                var breached = shortBy > Cent;
                tests.Add(new OwnerTrustTestReading("INTEREST_SHORTFALL", shortBy, 0.0, breached, false));
                if (breached)
                    s.PendingEvent ??= "INTEREST_SHORTFALL";
            }

            if (tested.ReserveShortfall && !amortizing)
            {
                var shortBy = Math.Max(0.0, reserveRequired - s.Reserve);
                var breached = shortBy > Cent;
                tests.Add(new OwnerTrustTestReading("RESERVE_SHORTFALL", shortBy, 0.0, breached, false));
                if (breached)
                    s.PendingEvent ??= "RESERVE_SHORTFALL";
            }

            if (tested.AllocatedPoolLimit is { } limit)
            {
                var base_ = s.Notes - s.Pfa;
                if (base_ > Cent)
                {
                    var ratio = allocatedPool / base_;
                    var breached = ratio < limit;
                    tests.Add(new OwnerTrustTestReading("ALLOCATED_POOL_TO_NOTES", ratio, limit, breached, false));
                    if (breached)
                        s.PendingEvent ??= "ALLOCATED_POOL_TO_NOTES";
                }
            }

            if (tested.DiscountedInvested)
            {
                var headroom = endingPool - v.ConcEnd;
                var breached = v.DiscEnd > headroom + Cent;
                tests.Add(new OwnerTrustTestReading("DISCOUNTED_INVESTED", v.DiscEnd, headroom, breached, false));
                if (breached)
                    s.PendingEvent ??= "DISCOUNTED_INVESTED";
            }
        }

        private void Accrue(ClassState c, RevolvingPeriodDates dates, DateTime? closing)
        {
            var config = c.Config;
            c.IndexValue = config.Index is { } index ? _rates.GetRate(index, dates.PeriodStart) : null;
            c.RatePct = c.IndexValue is { } value ? value + config.Margin : c.Coupon!.Value;
            double fraction;
            if (config.Basis == AccrualBasis.Actual360)
            {
                c.AccrualDays = closing is { } from ? (dates.DistributionDate - from.Date).Days : dates.AccrualDays;
                fraction = c.AccrualDays / 360.0;
            }
            else
            {
                // 30/360 runs to the payment day as scheduled, before any business-day roll.
                var paid = dates.DistributionDate;
                var scheduled = new DateTime(paid.Year, paid.Month,
                    Math.Min(_run.DistributionDay, DateTime.DaysInMonth(paid.Year, paid.Month)));
                c.AccrualDays = closing is { } from ? Days360(from.Date, scheduled) : 30;
                fraction = c.AccrualDays / 360.0;
            }

            c.Accrued = Cents(c.Balance * c.RatePct / 100.0 * fraction);
            c.Due = Cents(c.Shortfall * (1.0 + c.RatePct / 100.0 * fraction)) + c.Accrued;
            c.Shortfall = c.Due;
        }

        private static void PayInterest(SeriesState s, Waterfall w, string tier)
        {
            var members = s.Classes.Where(c => c.Tier == tier).ToList();
            var due = members.Sum(c => c.Shortfall);
            var paid = w.Spend(due, OwnerTrustVerb.PayInterest);
            foreach (var c in members)
            {
                var share = due > 0.0 ? c.Shortfall / due * paid : 0.0;
                c.InterestPaid += share;
                c.Shortfall -= share;
                if (c.Shortfall < 1e-9)
                    c.Shortfall = 0.0;
            }
        }

        /// <summary>
        ///     The notes of this tier and every tier above it at the last payment date, less the allocated
        ///     pool balance and the priority principal already paid this date. On and after the tier's
        ///     final maturity date, at least what the tier still owes.
        /// </summary>
        private static double PriorityNeed(SeriesState s, int index, double allocatedPool, double paidSoFar,
            RevolvingPeriodDates dates)
        {
            var through = s.Config.Tiers.Take(index + 1).Select(t => t.Name).ToHashSet();
            var notesThrough = s.Classes.Where(c => through.Contains(c.Tier)).Sum(c => c.Begin);
            var need = Math.Max(0.0, Cents(notesThrough - allocatedPool - paidSoFar));
            var tier = s.Config.Tiers[index];
            if (tier.FinalMaturity is { } maturity && dates.DistributionDate >= maturity)
                need = Math.Max(need, s.Classes.Where(c => c.Tier == tier.Name).Sum(c => c.Balance));
            return need;
        }

        /// <summary>Into the principal funding account while revolving, up to what the notes are.</summary>
        private static double Deposit(SeriesState s, Waterfall w, double need, OwnerTrustVerb verb)
        {
            var room = Math.Max(0.0, s.Notes - s.Pfa);
            var paid = w.Spend(Math.Min(need, room), verb);
            s.Pfa += paid;
            return paid;
        }

        private static double PayPrincipal(SeriesState s, Waterfall w, double need, OwnerTrustVerb verb)
        {
            var paid = w.Spend(Math.Min(need, s.Notes), verb);
            return PaySequential(s, paid);
        }

        /// <summary>Principal sequentially by tier, pro rata to balance within a tier.</summary>
        private static double PaySequential(SeriesState s, double amount)
        {
            var left = amount;
            foreach (var tier in s.Config.Tiers)
            {
                if (left <= 0.0)
                    break;
                var members = s.Classes.Where(c => c.Tier == tier.Name && c.Balance > 0.0).ToList();
                var owed = members.Sum(c => c.Balance);
                if (owed <= 0.0)
                    continue;
                var pay = Math.Min(left, owed);
                foreach (var c in members)
                {
                    var share = c.Balance / owed * pay;
                    c.Balance -= share;
                    c.PrincipalPaid += share;
                    if (c.Balance < 1e-7)
                        c.Balance = 0.0;
                }

                left -= pay;
            }

            return amount - left;
        }

        private void Disclose()
        {
            var forecast = _run.Pool.Periods > _run.Pool.ActualPeriods.Count;
            if (!forecast)
                return;
            if (_trust.Acquisition == null)
                _disclosures.Add("No acquisition was supplied, so forecast months buy nothing and the pool runs off.");
            if (_trust.Tests.DelinquencyLimit != null && _trust.Delinquency.Count == 0)
                _disclosures.Add(
                    "No delinquency path was supplied, so forecast months hold the last delinquency ratio the run knows.");
            foreach (var group in _heldConcentration.OrderBy(g => g))
                _disclosures.Add(
                    $"No concentration path was supplied for group '{group}', so forecast months hold its last known amount.");
            if (_trust.Series.Any(s => s.Private))
                _disclosures.Add(
                    "Private series take their share of the pool and of the funds, but run no waterfall, so what "
                    + "they leave for the certificates is not counted in the cash that buys additions.");
            _disclosures.Add(
                "Forecast months value the pool on the month's first and last days only: no financing adjustment "
                + "dates between them.");
        }

        /// <summary>
        ///     A series' available funds, spent step by step. A step the reserve covers (fees, servicing,
        ///     interest and priority principal) draws on the reserve when the funds run short.
        /// </summary>
        private sealed class Waterfall
        {
            private readonly SeriesState _series;

            public Waterfall(SeriesState series, double available)
            {
                _series = series;
                Available = available;
            }

            public double Available { get; private set; }
            public double ReserveDrawn { get; private set; }

            public double Spend(double need, OwnerTrustVerb verb)
            {
                if (need <= 0.0)
                    return 0.0;
                var paid = Math.Min(need, Available);
                Available -= paid;
                if (paid < need && OwnerTrustVerbs.ReserveCovers(verb))
                {
                    var draw = Math.Min(need - paid, _series.Reserve);
                    _series.Reserve -= draw;
                    ReserveDrawn += draw;
                    paid += draw;
                }

                return paid;
            }
        }
    }
}
