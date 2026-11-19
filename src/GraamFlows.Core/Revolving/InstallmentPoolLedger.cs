namespace GraamFlows.Revolving;

/// <summary>
///     The monthly roll of a pool of fixed 0% installment contracts, valued at the present value of
///     their remaining payments at a discount rate. Each cohort is one schedule times a surviving
///     share: every removal scales a cohort's whole remaining schedule, so the value it takes at any
///     rate is that share of the cohort's value, exactly.
///     <para>
///         A month runs in this order. Write-offs, then repurchases, take their share of the whole
///         schedule as it opens. The surviving share pays what falls due. Prepayments and upgrades
///         take their share of what is left. Purchases join at the close, their first payment
///         falling due at the close of the next month.
///     </para>
///     <para>
///         A replayed month takes the printed lines instead. Write-offs and repurchases take
///         <c>(writeOffs + repurchases) / beginning face</c> of every cohort. What falls due falls
///         due. The rest of every cohort's schedule is then scaled by one factor so the face it loses
///         is exactly printed prepaid + upgrades + (printed scheduled − due). The face roll is then
///         the printed one to the cent, and when the printed scheduled is what fell due the month is
///         the forecast's roll with pool-wide rates.
///     </para>
/// </summary>
public static class InstallmentPoolLedger
{
    private const double Cent = 0.005;

    public static InstallmentPoolResult Run(InstallmentPoolRun run)
    {
        InstallmentPoolValidator.Validate(run);
        return new Loop(run).Execute();
    }

    /// <summary>
    ///     The same roll one month at a time, for a trust whose purchases depend on its waterfall: it
    ///     rolls a month, reads the pool before the month's purchases, and then closes the month with
    ///     them. <see cref="Run" /> is this, with each month's stated purchases and nothing else.
    /// </summary>
    internal static Stepper Step(InstallmentPoolRun run)
    {
        InstallmentPoolValidator.Validate(run);
        return new Stepper(new Loop(run));
    }

    /// <summary>A month rolled and not yet closed: its purchases have not joined.</summary>
    internal sealed class PoolMonth
    {
        internal PoolMonth(RevolvingPeriodDates dates, bool replayed, double beginningFace,
            IReadOnlyList<Opening> opened, Removals removed, double scheduledCollections, double repurchaseCash,
            IReadOnlyList<InstallmentPurchase> purchases, double? printedBeginning, double? printedEnding)
        {
            Dates = dates;
            Replayed = replayed;
            BeginningFace = beginningFace;
            Opened = opened;
            Removed = removed;
            ScheduledCollections = scheduledCollections;
            RepurchaseCash = repurchaseCash;
            Purchases = purchases;
            PrintedBeginning = printedBeginning;
            PrintedEnding = printedEnding;
        }

        public RevolvingPeriodDates Dates { get; }
        public bool Replayed { get; }
        public double BeginningFace { get; }
        internal IReadOnlyList<Opening> Opened { get; }
        internal Removals Removed { get; }
        public double ScheduledCollections { get; }
        public double RepurchaseCash { get; }

        /// <summary>The month's own purchases: a replayed month's printed additions, a forecast month's stated ones.</summary>
        public IReadOnlyList<InstallmentPurchase> Purchases { get; }

        public double? PrintedBeginning { get; }
        public double? PrintedEnding { get; }

        /// <summary>The one collections bucket: scheduled, prepaid and upgrades.</summary>
        public double Collections => ScheduledCollections + Removed.Face[(int)Line.Prepaid]
                                                          + Removed.Face[(int)Line.Upgrades];

        public double WriteOffs => Removed.Face[(int)Line.WriteOffs];
    }

    /// <summary>The roll, a month at a time.</summary>
    internal sealed class Stepper
    {
        private readonly Loop _loop;

        internal Stepper(Loop loop)
        {
            _loop = loop;
        }

        /// <summary>Face of the pool as it stands: before a rolled month's purchases join.</summary>
        public double Face => _loop.LiveFace();

        /// <summary>Present value of the pool as it stands, at an annual rate as a fraction.</summary>
        public double Value(double rate)
        {
            return _loop.LiveValue(rate);
        }

        public PoolMonth Roll(RevolvingPeriodDates dates)
        {
            return _loop.Roll(dates);
        }

        /// <summary>Closes a rolled month with its own purchases and <paramref name="more" />.</summary>
        public InstallmentPoolPeriod Close(PoolMonth month, IEnumerable<InstallmentPurchase> more)
        {
            return _loop.CloseMonth(month, month.Purchases.Concat(more).ToList());
        }

        public IReadOnlyList<InstallmentCohort> Closing()
        {
            return _loop.ClosingCohorts();
        }

        public IReadOnlyList<string> Disclosures()
        {
            return _loop.RunDisclosures();
        }

        /// <summary>Removes the whole pool, as when the receivables leave the trust.</summary>
        public void Transfer()
        {
            _loop.Clear();
        }
    }

    /// <summary>A cohort as the run holds it: the stated schedule, how far it has run, and what survives of it.</summary>
    private sealed class Cohort
    {
        public required string Name { get; init; }
        public required double[] Payments { get; init; }
        public int Next { get; set; }
        public double Survival { get; set; } = 1.0;
        public int Age { get; set; }

        public double Face
        {
            get
            {
                var face = 0.0;
                for (var k = Next; k < Payments.Length; k++)
                    face += Payments[k];
                return face * Survival;
            }
        }

        public double Due => Next < Payments.Length ? Payments[Next] * Survival : 0.0;

        /// <summary>Present value as the month opens: the next payment falls due at its close.</summary>
        public double PresentValue(double rate)
        {
            return InstallmentValuationMath.PresentValue(Payments, Next, rate) * Survival;
        }

        public bool Done => Next >= Payments.Length || Survival <= 0.0;

        public InstallmentCohort ToCohort()
        {
            return new InstallmentCohort
            {
                Name = Name,
                Age = Age,
                Schedule = Payments.Skip(Next).Select(p => p * Survival).ToArray()
            };
        }
    }

    /// <summary>What one cohort holds as the month opens, at each discount rate.</summary>
    internal sealed record Opening(double Face, double Due, double[] Values)
    {
        /// <summary>The value at the month's close of the whole opening schedule: what falls due counts in full.</summary>
        public double AtClose(int rate, IReadOnlyList<double> rates)
        {
            return Values[rate] * (1.0 + rates[rate] / 12.0);
        }
    }

    internal sealed class Loop
    {
        private readonly InstallmentPoolRun _run;
        private readonly IReadOnlyList<double> _rates;
        private readonly List<Cohort> _pool;
        private readonly List<InstallmentPoolPeriod> _rows = new();

        public Loop(InstallmentPoolRun run)
        {
            _run = run;
            // One valuation per distinct rate, in the order first listed.
            _rates = run.DiscountRates.Distinct().ToList();
            _pool = run.Cohorts.Select(c => new Cohort
            {
                Name = c.Name,
                Payments = c.Schedule.ToArray(),
                Age = c.Age
            }).ToList();
        }

        public InstallmentPoolResult Execute()
        {
            foreach (var dates in RevolvingCalendar.Build(_run.StatementDate, 15, _run.Periods))
            {
                var month = Roll(dates);
                _rows.Add(CloseMonth(month, month.Purchases));
            }

            return new InstallmentPoolResult
            {
                Periods = _rows,
                Closing = ClosingCohorts(),
                Disclosures = Disclosures()
            };
        }

        public PoolMonth Roll(RevolvingPeriodDates dates)
        {
            return dates.Period < _run.ActualPeriods.Count
                ? Replay(dates, _run.ActualPeriods[dates.Period])
                : Forecast(dates, dates.Period - _run.ActualPeriods.Count);
        }

        public InstallmentPoolPeriod CloseMonth(PoolMonth month, IReadOnlyList<InstallmentPurchase> purchases)
        {
            var row = Close(month.Dates, month.Replayed, month.BeginningFace, month.Opened, month.Removed,
                month.ScheduledCollections, month.RepurchaseCash, purchases, month.PrintedBeginning,
                month.PrintedEnding);
            _pool.RemoveAll(c => c.Done);
            return row;
        }

        public IReadOnlyList<InstallmentCohort> ClosingCohorts()
        {
            return _pool.Select(c => c.ToCohort()).ToList();
        }

        public IReadOnlyList<string> RunDisclosures()
        {
            return Disclosures();
        }

        /// <summary>Face of the cohorts with a payment left.</summary>
        public double LiveFace()
        {
            return _pool.Where(c => !c.Done).Sum(c => c.Face);
        }

        public double LiveValue(double rate)
        {
            return _pool.Where(c => !c.Done).Sum(c => c.PresentValue(rate));
        }

        public void Clear()
        {
            _pool.Clear();
        }

        private Opening Open(Cohort c)
        {
            return new Opening(c.Face, c.Due, _rates.Select(c.PresentValue).ToArray());
        }

        private PoolMonth Forecast(RevolvingPeriodDates dates, int forecastMonth)
        {
            var drivers = _run.Drivers!;
            var opened = _pool.Select(Open).ToList();
            var beginningFace = opened.Sum(o => o.Face);
            var removed = new Removals(_rates.Count);

            // Write-offs first, on the whole opening schedule, by each cohort's age.
            var writeOffShares = _pool.Select(c => drivers.WriteOff.At(c.Age)).ToList();
            var afterWriteOffs = opened.Select((o, i) => o.Face * (1.0 - writeOffShares[i])).Sum();

            // Repurchases are an amount, taken pro rata from what the write-offs left.
            var repurchaseFace = forecastMonth < drivers.RepurchaseAmounts.Count
                ? drivers.RepurchaseAmounts[forecastMonth]
                : 0.0;
            if (repurchaseFace > afterWriteOffs + Cent)
                throw new RevolvingConfigException(
                    $"Forecast month {forecastMonth} repurchases {repurchaseFace:F2} of face from a pool with "
                    + $"{afterWriteOffs:F2} left after write-offs.");
            var repurchaseShare = afterWriteOffs > 0.0 ? Math.Min(1.0, repurchaseFace / afterWriteOffs) : 0.0;
            var repurchaseCash = 0.0;

            for (var i = 0; i < _pool.Count; i++)
            {
                var c = _pool[i];
                var o = opened[i];
                var writeOff = writeOffShares[i];
                var repurchase = (1.0 - writeOff) * repurchaseShare;
                var surviving = (1.0 - writeOff) * (1.0 - repurchaseShare);
                removed.Take(Line.WriteOffs, writeOff, o.Face, r => o.AtClose(r, _rates));
                removed.Take(Line.Repurchases, repurchase, o.Face, r => o.AtClose(r, _rates));
                if (repurchase > 0.0)
                    repurchaseCash += CashFor(drivers.RepurchasePrice!, repurchase * o.Face,
                        q => repurchase * c.PresentValue(q) * (1.0 + q / 12.0));

                // The survivors pay what falls due; prepayments and upgrades take their share of the rest.
                removed.Due += surviving * o.Due;
                var prepay = drivers.Prepayment.At(c.Age);
                var upgrade = drivers.Upgrade?.At(c.Age) ?? 0.0;
                var restFace = surviving * (o.Face - o.Due);
                removed.Take(Line.Prepaid, prepay, restFace, r => surviving * (o.AtClose(r, _rates) - o.Due));
                removed.Take(Line.Upgrades, upgrade, restFace, r => surviving * (o.AtClose(r, _rates) - o.Due));

                c.Survival *= surviving * (1.0 - prepay - upgrade);
                c.Next++;
                c.Age++;
            }

            var purchases = _run.Purchases.Where(p => p.Period == dates.Period).ToList();
            if (drivers.Reinvest is { } reinvest)
                purchases.AddRange(Reinvestment(reinvest, dates.Period,
                    removed.Due + removed.Face[(int)Line.Prepaid] + removed.Face[(int)Line.Upgrades]));

            return new PoolMonth(dates, false, beginningFace, opened, removed, removed.Due, repurchaseCash, purchases,
                null, null);
        }

        private PoolMonth Replay(RevolvingPeriodDates dates, InstallmentActual actual)
        {
            var opened = _pool.Select(Open).ToList();
            var beginningFace = opened.Sum(o => o.Face);
            var name = $"actualPeriods[{dates.Period}]";
            var removed = new Removals(_rates.Count);

            var openingRemovals = actual.WriteOffs + actual.Repurchases;
            if (openingRemovals > beginningFace + Cent)
                throw new RevolvingConfigException(
                    $"{name} writes off and repurchases {openingRemovals:F2} of face from a pool of {beginningFace:F2}.");
            var writeOff = beginningFace > 0.0 ? actual.WriteOffs / beginningFace : 0.0;
            var repurchase = beginningFace > 0.0 ? actual.Repurchases / beginningFace : 0.0;
            var surviving = Math.Max(0.0, 1.0 - writeOff - repurchase);

            var repurchaseCash = actual.RepurchaseCash ?? 0.0;
            if (actual.RepurchaseCash == null && actual.Repurchases > 0.0)
                repurchaseCash = CashFor(actual.RepurchasePrice!, actual.Repurchases,
                    q => repurchase * _pool.Sum(c => c.PresentValue(q)) * (1.0 + q / 12.0));

            var due = 0.0;
            var restFace = 0.0;
            foreach (var o in opened)
            {
                removed.Take(Line.WriteOffs, writeOff, o.Face, r => o.AtClose(r, _rates));
                removed.Take(Line.Repurchases, repurchase, o.Face, r => o.AtClose(r, _rates));
                due += surviving * o.Due;
                restFace += surviving * (o.Face - o.Due);
            }

            removed.Due = due;

            // One factor on what is left after the due payment, so the face it loses is the printed lines'.
            var variance = actual.ScheduledCollections - due;
            var lost = actual.PrepaidCollections + actual.UpgradeCollections + variance;
            if (lost > restFace + Cent || (restFace <= 0.0 && Math.Abs(lost) > Cent))
                throw new RevolvingConfigException(
                    $"{name} collects {actual.ScheduledCollections:F2} scheduled, {actual.PrepaidCollections:F2} "
                    + $"prepaid and {actual.UpgradeCollections:F2} upgrades. {due:F2} fell due and {restFace:F2} "
                    + "was left after it, so the schedule cannot lose that much.");
            var kept = restFace > 0.0 ? Math.Max(0.0, 1.0 - lost / restFace) : 0.0;

            for (var i = 0; i < _pool.Count; i++)
            {
                var o = opened[i];
                var restShare = restFace > 0.0 ? 1.0 / restFace : 0.0;
                // Each line's face, as a share of what is left, of this cohort's rest.
                removed.Take(Line.Prepaid, actual.PrepaidCollections * restShare, surviving * (o.Face - o.Due),
                    r => surviving * (o.AtClose(r, _rates) - o.Due));
                removed.Take(Line.Upgrades, actual.UpgradeCollections * restShare, surviving * (o.Face - o.Due),
                    r => surviving * (o.AtClose(r, _rates) - o.Due));
                removed.Take(Line.Variance, variance * restShare, surviving * (o.Face - o.Due),
                    r => surviving * (o.AtClose(r, _rates) - o.Due));

                var c = _pool[i];
                c.Survival *= surviving * kept;
                c.Next++;
                c.Age++;
            }

            // The printed lines are the month's, not the shares' face times the rest.
            removed.Face[(int)Line.WriteOffs] = actual.WriteOffs;
            removed.Face[(int)Line.Repurchases] = actual.Repurchases;
            removed.Face[(int)Line.Prepaid] = actual.PrepaidCollections;
            removed.Face[(int)Line.Upgrades] = actual.UpgradeCollections;

            return new PoolMonth(dates, true, beginningFace, opened, removed, actual.ScheduledCollections,
                repurchaseCash, actual.Additions.ToList(), actual.BeginningFace, actual.EndingFace);
        }

        private InstallmentPoolPeriod Close(RevolvingPeriodDates dates, bool replayed, double beginningFace,
            IReadOnlyList<Opening> opened, Removals removed, double scheduledCollections, double repurchaseCash,
            IReadOnlyList<InstallmentPurchase> purchases, double? printedBeginning, double? printedEnding)
        {
            var additionsFace = 0.0;
            var additionsCash = 0.0;
            var additionsValue = new double[_rates.Count];
            foreach (var purchase in purchases)
            {
                var schedule = purchase.Cohort.Schedule;
                var face = purchase.Cohort.Face;
                additionsFace += face;
                additionsCash += purchase.Cash
                                 ?? CashFor(purchase.Price!, face, q => InstallmentValuationMath.PresentValue(schedule, q));
                for (var r = 0; r < _rates.Count; r++)
                    additionsValue[r] += InstallmentValuationMath.PresentValue(schedule, _rates[r]);
                _pool.Add(new Cohort
                {
                    Name = purchase.Cohort.Name,
                    Payments = schedule.ToArray(),
                    Age = purchase.Cohort.Age
                });
            }

            var live = _pool.Where(c => !c.Done).ToList();
            var endingFace = live.Sum(c => c.Face);
            var valuations = new List<InstallmentValuation>(_rates.Count);
            for (var r = 0; r < _rates.Count; r++)
                valuations.Add(new InstallmentValuation
                {
                    Rate = _rates[r],
                    Beginning = opened.Sum(o => o.Values[r]),
                    WriteOffs = removed.Value[(int)Line.WriteOffs][r],
                    Repurchases = removed.Value[(int)Line.Repurchases][r],
                    PrepaidCollections = removed.Value[(int)Line.Prepaid][r],
                    UpgradeCollections = removed.Value[(int)Line.Upgrades][r],
                    ScheduledVariance = removed.Value[(int)Line.Variance][r],
                    Additions = additionsValue[r],
                    // Valued afresh from the closed pool, not from the lines above.
                    Ending = live.Sum(c => c.PresentValue(_rates[r]))
                });

            return new InstallmentPoolPeriod
            {
                Period = dates.Period,
                PeriodEnd = dates.PeriodEnd,
                Replayed = replayed,
                BeginningFace = beginningFace,
                ScheduledDue = removed.Due,
                ScheduledCollections = scheduledCollections,
                PrepaidCollections = removed.Face[(int)Line.Prepaid],
                UpgradeCollections = removed.Face[(int)Line.Upgrades],
                WriteOffs = removed.Face[(int)Line.WriteOffs],
                Repurchases = removed.Face[(int)Line.Repurchases],
                RepurchaseCash = repurchaseCash,
                AdditionsFace = additionsFace,
                AdditionsCash = additionsCash,
                EndingFace = endingFace,
                Cohorts = live.Count,
                RollBreak = printedBeginning - beginningFace,
                LedgerBreak = endingFace - printedEnding,
                Valuations = valuations
            };
        }

        /// <summary>
        ///     Fresh level contracts bought with a share of the month's collections. The face bought is
        ///     the cash over the weighted price per dollar of face of the terms.
        /// </summary>
        private static IEnumerable<InstallmentPurchase> Reinvestment(InstallmentReinvest reinvest, int period,
            double collections)
        {
            return Buy(reinvest.Price, reinvest.Terms, $"reinvest-{period}", reinvest.Share * collections);
        }

        /// <summary>
        ///     Fresh level contracts that cost <paramref name="cash" /> at a price: the face bought is the
        ///     cash over the weighted price per dollar of face of the terms.
        /// </summary>
        internal static IEnumerable<InstallmentPurchase> Buy(InstallmentPrice price, IReadOnlyList<InstallmentTerm> terms,
            string name, double cash)
        {
            var perFace = terms.Sum(t =>
                t.Weight * CashFor(price, 1.0,
                    q => InstallmentValuationMath.PresentValue(InstallmentCohort.LevelSchedule(1.0, t.Term), q)));
            if (cash <= 0.0 || perFace <= 0.0)
                yield break;

            var face = cash / perFace;
            foreach (var term in terms)
                yield return new InstallmentPurchase
                {
                    Cohort = new InstallmentCohort
                    {
                        Name = $"{name}-{term.Term}",
                        Age = term.Age,
                        Schedule = InstallmentCohort.LevelSchedule(face * term.Weight, term.Term)
                    },
                    Price = price
                };
        }

        private static double CashFor(InstallmentPrice price, double face, Func<double, double> valueAt)
        {
            return price.PerFace is { } perFace ? perFace * face : valueAt(price.PvRate!.Value);
        }

        private IReadOnlyList<string> Disclosures()
        {
            var disclosures = new List<string>();
            if (_run.Drivers is { } drivers && _run.Periods > _run.ActualPeriods.Count)
            {
                if (drivers.Upgrade == null)
                    disclosures.Add("No upgrade rate was supplied, so forecast upgrades are 0.");
                if (drivers.Reinvest == null)
                    disclosures.Add("No reinvestment was supplied, so forecast months buy only the stated purchases.");
            }

            if (_rates.Count == 0)
                disclosures.Add("No discount rates were supplied, so the pool is not valued.");
            return disclosures;
        }
    }

    internal enum Line
    {
        WriteOffs,
        Repurchases,
        Prepaid,
        Upgrades,
        Variance
    }

    /// <summary>What the month took from the schedule, line by line: face, and value at each rate at the close.</summary>
    internal sealed class Removals
    {
        public Removals(int rates)
        {
            Face = new double[Enum.GetValues<Line>().Length];
            Value = Face.Select(_ => new double[rates]).ToArray();
        }

        public double[] Face { get; }
        public double[][] Value { get; }
        public double Due { get; set; }

        /// <param name="share">the share of the part taken from.</param>
        /// <param name="face">the part's face.</param>
        /// <param name="valueAtClose">the part's value at the month's close, at a rate's index.</param>
        public void Take(Line line, double share, double face, Func<int, double> valueAtClose)
        {
            if (share == 0.0)
                return;
            Face[(int)line] += share * face;
            for (var r = 0; r < Value[(int)line].Length; r++)
                Value[(int)line][r] += share * valueAtClose(r);
        }
    }
}
