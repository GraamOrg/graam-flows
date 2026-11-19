using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Util.Calender.Calendars;

namespace GraamFlows.Revolving;

/// <summary>
///     Runs an amortizing owner trust over an installment pool, one payment date at a time.
///     <para>
///         Each month the pool rolls first (<see cref="InstallmentPoolLedger" />). The trust then reads
///         its adjusted pool, the pool's present value at the discount rate, before the month's
///         purchases. Tests that read the pool act on this payment date. The waterfall pays the stated
///         steps from available funds, drawing on the negative carry account and the reserve account
///         for the steps each covers. What the acquisition account holds buys the month's purchases,
///         which join the pool at the month's close. Tests that read the payments act from the next
///         payment date.
///     </para>
/// </summary>
public static class OwnerTrustEngine
{
    private const double Cent = 0.005;

    public static OwnerTrustResult Run(OwnerTrustRun run, IRateProvider rates)
    {
        OwnerTrustValidator.Validate(run);
        return new Loop(run, rates).Execute();
    }

    private sealed class ClassState
    {
        public required RevolvingClass Config { get; init; }
        public required string Tier { get; init; }
        public double Balance { get; set; }
        public double Shortfall { get; set; }
        public double RatePct { get; set; }
        public double? IndexValue { get; set; }
        public double Accrued { get; set; }
        public double Due { get; set; }
        public double InterestPaid { get; set; }
        public double PrincipalPaid { get; set; }
        public double Begin { get; set; }
    }

    private sealed class Loop
    {
        private readonly OwnerTrustRun _run;
        private readonly OwnerTrustConfig _trust;
        private readonly IRateProvider _rates;
        private readonly InstallmentPoolLedger.Stepper _pool;
        private readonly List<ClassState> _classes;
        private readonly List<string> _disclosures = new();
        private readonly List<OwnerTrustPeriod> _rows = new();
        private readonly List<(SeriesPhase, int, DateTime, string?)> _path = new();
        private readonly List<double> _writeOffs;
        private readonly List<double> _delinquency;
        private readonly List<double> _ocPcts;

        private SeriesPhase _phase;
        private string? _event;
        private string? _pendingEvent;
        private double _reserve;
        private double _acquisition;
        private double _negativeCarry;
        private double _adjusted;
        private int _eligibleMonths;
        private string? _ended;

        public Loop(OwnerTrustRun run, IRateProvider rates)
        {
            _run = run;
            _trust = run.Trust;
            _rates = rates;
            var poolRun = run.Pool.DiscountRates.Contains(_trust.DiscountRate)
                ? run.Pool
                : run.Pool with { DiscountRates = run.Pool.DiscountRates.Append(_trust.DiscountRate).ToList() };
            _pool = InstallmentPoolLedger.Step(poolRun);
            _classes = run.Classes.Select(c => new ClassState
            {
                Config = c,
                Tier = _trust.Tiers.First(t => t.Classes.Contains(c.Key)).Name,
                Balance = c.Balance,
                Shortfall = c.InterestShortfall
            }).ToList();
            var state = run.State;
            _phase = state.Phase;
            _event = state.Event;
            _reserve = state.Reserve;
            _acquisition = state.AcquisitionAccount;
            _negativeCarry = state.NegativeCarry;
            _adjusted = state.AdjustedPool ?? _pool.Value(_trust.DiscountRate);
            _writeOffs = state.WriteOffHistory.ToList();
            _delinquency = state.DelinquencyHistory.ToList();
            _ocPcts = state.OcPctHistory.ToList();
            _eligibleMonths = state.CleanUpEligibleMonths;
        }

        private double Notes => _classes.Sum(c => c.Balance);

        public OwnerTrustResult Execute()
        {
            var calendar = RevolvingCalendar.Build(_run.Pool.StatementDate, _run.DistributionDay, _run.Pool.Periods,
                _trust.Calendar == OwnerTrustCalendar.FederalReserve
                    ? new UnitedStates(UnitedStates.Market.FederalReserve)
                    : null);
            foreach (var dates in calendar)
            {
                _rows.Add(Month(dates));
                if (_ended != null)
                    break;
            }

            if (_ended != null && _rows.Count < calendar.Count)
                _disclosures.Add(
                    $"The trust ended in period {_rows.Count - 1} ({_ended}), so the run stops there, "
                    + $"{calendar.Count - _rows.Count} periods before the {calendar.Count} asked for.");

            Disclose();
            return new OwnerTrustResult
            {
                Periods = _rows,
                PhasePath = _path,
                Closing = _ended != null ? null : ClosingState(),
                ClosingClasses = _classes.Select(c => new OwnerTrustClassClosing(c.Config.Key, c.Balance, c.Shortfall))
                    .ToList(),
                ClosingPool = _pool.Closing(),
                Ended = _ended,
                // The acquisition account does the buying here, so the pool's note on reinvestment does not apply.
                Disclosures = _pool.Disclosures().Where(d => !d.StartsWith("No reinvestment"))
                    .Concat(_disclosures).Distinct().ToList()
            };
        }

        private OwnerTrustState ClosingState()
        {
            return new OwnerTrustState
            {
                Phase = _phase,
                Event = _event ?? _pendingEvent,
                Reserve = _reserve,
                AcquisitionAccount = _acquisition,
                NegativeCarry = _negativeCarry,
                AdjustedPool = _adjusted,
                WriteOffHistory = _writeOffs.ToList(),
                DelinquencyHistory = _delinquency.ToList(),
                OcPctHistory = _ocPcts.ToList(),
                CleanUpEligibleMonths = _eligibleMonths
            };
        }

        private void MoveTo(SeriesPhase phase, RevolvingPeriodDates dates, string? reason)
        {
            if (phase == _phase)
                return;
            _phase = phase;
            _path.Add((phase, dates.Period, dates.DistributionDate, reason));
        }

        /// <summary>An amortization event: the trust amortizes from now on and pays accelerated principal.</summary>
        private void Trip(string name, RevolvingPeriodDates dates)
        {
            if (_event != null)
                return;
            _event = name;
            if (_phase == SeriesPhase.Revolving)
                MoveTo(SeriesPhase.EarlyAmortization, dates, name);
            else
                _path.Add((_phase, dates.Period, dates.DistributionDate, name));
        }

        private OwnerTrustPeriod Month(RevolvingPeriodDates dates)
        {
            var t = dates.Period;
            var replayed = t < _run.Pool.ActualPeriods.Count;
            var forecastMonth = t - _run.Pool.ActualPeriods.Count;
            var actual = t < _run.ActualPeriods.Count ? _run.ActualPeriods[t] : null;
            var rate = _trust.DiscountRate;

            // An event that read last month's payments, and the scheduled end of revolving, act now.
            if (_pendingEvent != null)
            {
                Trip(_pendingEvent, dates);
                _pendingEvent = null;
            }

            if (_phase == SeriesPhase.Revolving && _trust.RevolvingEnd is { } end
                                                && dates.DistributionDate >= new DateTime(end.Year, end.Month, 1))
                MoveTo(SeriesPhase.Amortizing, dates, null);

            var beginningPool = _pool.Face;
            var beginningAdjusted = _adjusted;
            foreach (var c in _classes)
            {
                c.Begin = c.Balance;
                c.InterestPaid = 0.0;
                c.PrincipalPaid = 0.0;
            }

            var month = _pool.Roll(dates);
            var poolBefore = _pool.Face;
            var computedBefore = _pool.Value(rate);

            // A replayed month's printed additions, valued at the discount rate.
            var printedAdditionsValue = month.Purchases.Sum(p =>
                InstallmentValuationMath.PresentValue(p.Cohort.Schedule, rate));
            var printedAdditionsCash = month.Purchases.Sum(p => p.Cash ?? CashFor(p));
            var adjustedBefore = actual?.AdjustedPool is { } printedEnd
                ? printedEnd - printedAdditionsValue
                : computedBefore;

            var tests = new List<OwnerTrustTestReading>();
            ReadPoolTests(dates, month, beginningPool, poolBefore, actual, forecastMonth, tests);

            // Interest accrues on every class at the opening balance.
            foreach (var c in _classes)
                Accrue(c, dates);

            var other = actual?.OtherFunds ?? 0.0;
            var earnings = _reserve * _trust.ReserveEarningsRate / 12.0;
            var available = month.Collections + month.RepurchaseCash + other + earnings;
            var fees = actual?.Fees ?? _trust.MonthlyFees;
            var servicing = _trust.ServicingRate / 12.0 * beginningAdjusted;

            // The clean-up: the certificateholders buy the receivables and the notes are redeemed. A
            // forecast one goes ahead only if the price and the trust's cash pay everything owed.
            var cleanUpDeposit = CleanUp(dates, actual, poolBefore, forecastMonth);
            if (cleanUpDeposit is { } offered && actual?.CleanUpDeposit == null)
            {
                var cash = available + offered + _reserve + _acquisition + _negativeCarry;
                var owed = fees + servicing + _classes.Sum(c => c.Shortfall) + Notes;
                if (cash + Cent < owed)
                {
                    _disclosures.Add(
                        $"The clean-up on {dates.DistributionDate:yyyy-MM-dd} was not exercised: its price and the "
                        + $"trust's cash come to {cash:F2} against {owed:F2} owed.");
                    cleanUpDeposit = null;
                }
            }

            var floorPass = actual?.FloorTestsPass ?? FloorPass(forecastMonth);
            var amortizing = _phase != SeriesPhase.Revolving || cleanUpDeposit != null;

            // Accounts released: the acquisition and negative carry accounts on the first amortizing
            // date, every account at a clean-up, and the reserve above its required amount.
            var acquisitionRelease = 0.0;
            var negativeCarryRelease = 0.0;
            if (amortizing && (_acquisition > 0.0 || _negativeCarry > 0.0))
            {
                acquisitionRelease = _acquisition;
                negativeCarryRelease = _negativeCarry;
                available += _acquisition + _negativeCarry;
                _acquisition = 0.0;
                _negativeCarry = 0.0;
            }

            // At a clean-up the reserve is required no more: it covers any step the cash does not, and
            // what is left of it is released after the waterfall, as the statements print it.
            var reserveRequired = cleanUpDeposit != null ? 0.0 : _trust.RequiredReserve;
            var reserveRelease = cleanUpDeposit != null ? 0.0 : Math.Max(0.0, _reserve - reserveRequired);
            available += reserveRelease;
            _reserve -= reserveRelease;
            if (cleanUpDeposit is { } deposit)
                available += deposit;

            // The OC target, on the notes while revolving and on the adjusted pool after.
            var ocPct = amortizing
                ? floorPass ? _trust.Oc.AmortizingPct : _trust.Oc.AmortizingFailingPct
                : floorPass ? _trust.Oc.RevolvingPct : _trust.Oc.RevolvingFailingPct;
            var notesBegin = Notes;
            var ocTarget = Math.Max(_trust.Oc.Floor,
                amortizing ? ocPct * adjustedBefore : notesBegin / (1.0 - ocPct) - notesBegin);

            var w = new Waterfall(this, available, _phase == SeriesPhase.Revolving && cleanUpDeposit == null,
                cleanUpDeposit != null);
            // A clean-up redeems the notes in accelerated principal, or in regular principal where the
            // trust states no accelerated step.
            var redeemInRegular = cleanUpDeposit != null
                                  && _trust.Steps.All(s => s.Verb != OwnerTrustVerb.AcceleratedPrincipal);
            var priorityTotal = 0.0;
            var regular = 0.0;
            var accelerated = 0.0;
            var reserveDeposit = 0.0;
            var acquisitionDeposit = 0.0;
            var negativeCarryRequired = 0.0;
            var negativeCarryDeposit = 0.0;
            var acquisitionCash = 0.0;
            var feesPaid = 0.0;
            var servicingPaid = 0.0;
            var certificate = 0.0;
            var purchases = new List<InstallmentPurchase>();
            var bought = false;

            foreach (var step in _trust.Steps)
                switch (step.Verb)
                {
                    case OwnerTrustVerb.PayFees:
                        feesPaid += w.Spend(fees, step.Verb);
                        break;
                    case OwnerTrustVerb.PayServicing:
                        servicingPaid += w.Spend(servicing, step.Verb);
                        break;
                    case OwnerTrustVerb.PayInterest:
                        PayInterest(w, step.Tier!);
                        break;
                    case OwnerTrustVerb.PriorityPrincipal when amortizing:
                    {
                        var need = PriorityNeed(step.Tier!, adjustedBefore, priorityTotal, dates, cleanUpDeposit != null);
                        var paid = PayPrincipal(w, need, step.Verb);
                        priorityTotal += paid;
                        break;
                    }
                    case OwnerTrustVerb.RegularPrincipal when amortizing:
                    {
                        var need = redeemInRegular
                            ? Notes
                            : Math.Max(Math.Max(0.0, notesBegin - priorityTotal - (adjustedBefore - ocTarget)),
                                MaturedLeft(dates));
                        regular += PayPrincipal(w, need, step.Verb);
                        break;
                    }
                    case OwnerTrustVerb.AcceleratedPrincipal when amortizing && (_event != null || cleanUpDeposit != null):
                        accelerated += PayPrincipal(w, Notes, step.Verb);
                        break;
                    case OwnerTrustVerb.FundReserve:
                    {
                        var paid = w.Spend(Math.Max(0.0, reserveRequired - _reserve), step.Verb);
                        reserveDeposit += paid;
                        _reserve += paid;
                        break;
                    }
                    case OwnerTrustVerb.DepositAcquisition when !amortizing:
                    {
                        var required = Math.Max(0.0, notesBegin - (adjustedBefore - ocTarget));
                        var paid = w.Spend(Math.Max(0.0, required - _acquisition), step.Verb);
                        acquisitionDeposit += paid;
                        _acquisition += paid;
                        acquisitionCash += Buy(dates, replayed, printedAdditionsCash, purchases);
                        bought = true;
                        break;
                    }
                    case OwnerTrustVerb.FundNegativeCarry when !amortizing:
                    {
                        negativeCarryRequired = _acquisition * WeightedRatePct() / 100.0 / 12.0;
                        var paid = w.Spend(Math.Max(0.0, negativeCarryRequired - _negativeCarry), step.Verb);
                        negativeCarryDeposit += paid;
                        _negativeCarry += paid;
                        break;
                    }
                    case OwnerTrustVerb.PayCertificate:
                        certificate += w.Spend(w.Available, step.Verb);
                        break;
                }

            // A replayed month's additions are printed whatever the steps say, and the account pays for them.
            if (!bought && replayed && month.Purchases.Count > 0)
                acquisitionCash += Buy(dates, true, printedAdditionsCash, purchases);

            // The excess negative carry is released while revolving, after the steps.
            if (!amortizing && _negativeCarry > negativeCarryRequired + Cent)
            {
                var excess = _negativeCarry - negativeCarryRequired;
                _negativeCarry -= excess;
                negativeCarryRelease += excess;
                certificate += excess;
            }

            var poolRow = _pool.Close(month, purchases);
            var computedEnd = _pool.Value(rate);
            var adjustedEnd = actual?.AdjustedPool ?? computedEnd;
            var additionsValue = adjustedEnd - adjustedBefore;
            var poolEnd = _pool.Face;

            // Whatever is left over is the certificate's. At the end the accounts are released to the
            // certificateholders too, on their own lines rather than through the waterfall.
            certificate += w.Available;
            var cleanedUp = cleanUpDeposit != null;
            if (cleanedUp || Notes <= Cent)
            {
                reserveRelease += _reserve;
                acquisitionRelease += _acquisition;
                negativeCarryRelease += _negativeCarry;
                _reserve = 0.0;
                _acquisition = 0.0;
                _negativeCarry = 0.0;
                _ended = cleanedUp ? "CLEAN_UP" : "NOTES_PAID";
                if (cleanedUp)
                    _pool.Transfer();
            }

            var notesEnd = Notes;
            var oc = adjustedEnd + _acquisition - notesEnd;
            ReadPaymentTests(amortizing, ocTarget, ocPct, oc, notesEnd, adjustedEnd, negativeCarryRequired, tests);
            _ocPcts.Add(ocPct);
            _adjusted = adjustedEnd;

            return new OwnerTrustPeriod
            {
                Period = t,
                PeriodEnd = dates.PeriodEnd,
                DistributionDate = dates.DistributionDate,
                Replayed = replayed,
                Phase = cleanedUp && _phase == SeriesPhase.Revolving ? SeriesPhase.Amortizing : _phase,
                Event = _event,
                BeginningPool = beginningPool,
                PoolBeforeAdditions = poolBefore,
                EndingPool = poolEnd,
                BeginningAdjustedPool = beginningAdjusted,
                AdjustedPoolBeforeAdditions = adjustedBefore,
                EndingAdjustedPool = adjustedEnd,
                Ysoc = poolEnd - adjustedEnd,
                ComputedAdjustedPool = actual?.AdjustedPool != null ? computedEnd : null,
                Collections = month.Collections,
                WriteOffs = month.WriteOffs,
                RepurchaseCash = month.RepurchaseCash,
                OtherFunds = other,
                ReserveEarnings = earnings,
                CleanUpDeposit = cleanUpDeposit ?? 0.0,
                AvailableFunds = month.Collections + month.RepurchaseCash + other + earnings + (cleanUpDeposit ?? 0.0),
                Fees = feesPaid,
                ServicingFee = servicingPaid,
                FloorTestsPass = floorPass,
                OcTargetPct = ocPct,
                OcTarget = ocTarget,
                Oc = oc,
                PriorityPrincipal = priorityTotal,
                RegularPrincipal = regular,
                AcceleratedPrincipal = accelerated,
                PrincipalPaid = _classes.Sum(c => c.PrincipalPaid),
                InterestPaid = _classes.Sum(c => c.InterestPaid),
                ReserveRequired = reserveRequired,
                ReserveDraw = w.ReserveDrawn,
                ReserveRelease = reserveRelease,
                ReserveDeposit = reserveDeposit,
                Reserve = _reserve,
                AcquisitionDeposit = acquisitionDeposit,
                AcquisitionCash = acquisitionCash,
                AcquisitionRelease = acquisitionRelease,
                AcquisitionAccount = _acquisition,
                AdditionsFace = poolRow.AdditionsFace,
                AdditionsValue = additionsValue,
                NegativeCarryRequired = negativeCarryRequired,
                NegativeCarryDeposit = negativeCarryDeposit,
                NegativeCarryDraw = w.NegativeCarryDrawn,
                NegativeCarryRelease = negativeCarryRelease,
                NegativeCarry = _negativeCarry,
                CertificatePaid = certificate,
                NoteBalance = notesEnd,
                Tests = tests,
                Classes = _classes.Select(c => new OwnerTrustClassPeriod
                {
                    Key = c.Config.Key,
                    BeginBalance = c.Begin,
                    CouponPct = c.RatePct,
                    IndexValue = c.IndexValue,
                    AccrualDays = c.Config.Basis == AccrualBasis.Actual360 ? dates.AccrualDays : 30,
                    InterestAccrued = c.Accrued,
                    InterestDue = c.Due,
                    InterestPaid = c.InterestPaid,
                    InterestShortfall = c.Shortfall,
                    PrincipalPaid = c.PrincipalPaid,
                    EndBalance = c.Balance
                }).ToList(),
                Pool = poolRow
            };

            // Local helpers that read the month.
            double CashFor(InstallmentPurchase p)
            {
                return p.Price!.PerFace is { } perFace
                    ? perFace * p.Cohort.Face
                    : InstallmentValuationMath.PresentValue(p.Cohort.Schedule, p.Price.PvRate!.Value);
            }
        }

        /// <summary>
        ///     What the acquisition account pays for the month's purchases. A replayed month pays for its
        ///     printed additions, up to what the account holds. A forecast month spends the whole account
        ///     on fresh contracts at the stated price and terms.
        /// </summary>
        private double Buy(RevolvingPeriodDates dates, bool replayed, double printedCash,
            List<InstallmentPurchase> purchases)
        {
            if (replayed)
            {
                var paid = Math.Min(printedCash, _acquisition);
                _acquisition -= paid;
                return paid;
            }

            if (_trust.AcquisitionPrice is not { } price || _acquisition <= Cent)
                return 0.0;
            var cash = _acquisition;
            purchases.AddRange(InstallmentPoolLedger.Loop.Buy(price, _trust.AcquisitionTerms, $"acquired-{dates.Period}",
                cash));
            _acquisition = 0.0;
            return cash;
        }

        /// <summary>
        ///     The tests that read the pool: they act on this payment date. A printed value is taken as it
        ///     is in a replayed month; the run's own goes beside it.
        /// </summary>
        private void ReadPoolTests(RevolvingPeriodDates dates, InstallmentPoolLedger.PoolMonth month,
            double beginningPool, double poolBefore, OwnerTrustActual? actual, int forecastMonth,
            List<OwnerTrustTestReading> tests)
        {
            var tested = _trust.Tests;
            _writeOffs.Add(beginningPool > 0.0 ? month.WriteOffs / beginningPool : 0.0);
            var delinquency = actual?.Delinquency ?? DelinquencyDriver(forecastMonth);
            _delinquency.Add(delinquency);

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
                    Trip("WRITE_OFF_RATIO", dates);
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
                    Trip("DELINQUENCY_RATIO", dates);
            }

            if (actual?.Event is { } stated)
            {
                tests.Add(new OwnerTrustTestReading(stated, null, null, true, true));
                Trip(stated, dates);
            }
        }

        /// <summary>The tests that read the payments: they act from the next payment date.</summary>
        private void ReadPaymentTests(bool amortizing, double ocTarget, double ocPct, double oc, double notes,
            double adjustedEnd, double negativeCarryRequired, List<OwnerTrustTestReading> tests)
        {
            var tested = _trust.Tests;
            if (tested.ParityLimit is { } parity && notes > Cent)
            {
                var ratio = adjustedEnd / notes;
                var breached = ratio < parity;
                tests.Add(new OwnerTrustTestReading("ADJUSTED_POOL_TO_NOTES", ratio, parity, breached, false));
                if (breached)
                    _pendingEvent ??= "ADJUSTED_POOL_TO_NOTES";
            }

            if (tested.OcShortfall && notes > Cent)
            {
                var shortBy = Math.Max(0.0, ocTarget - oc);
                // A shortfall only from a new percent has its grace months.
                var recent = _ocPcts.TakeLast(tested.OcGraceMonths).ToList();
                var changed = recent.Count > 0 && recent.Any(p => Math.Abs(p - ocPct) > 1e-12);
                var breached = shortBy > Cent && !changed;
                tests.Add(new OwnerTrustTestReading("OVERCOLLATERALIZATION", shortBy, 0.0, breached, false));
                if (breached)
                    _pendingEvent ??= "OVERCOLLATERALIZATION";
            }

            if (tested.RevolvingShortfalls && !amortizing)
            {
                var interestShort = _classes.Sum(c => c.Shortfall);
                var reserveShort = Math.Max(0.0, _trust.RequiredReserve - _reserve);
                var carryShort = Math.Max(0.0, negativeCarryRequired - _negativeCarry);
                foreach (var (name, value) in new[]
                         {
                             ("INTEREST_SHORTFALL", interestShort), ("RESERVE_SHORTFALL", reserveShort),
                             ("NEGATIVE_CARRY_SHORTFALL", carryShort)
                         })
                {
                    var breached = value > Cent;
                    tests.Add(new OwnerTrustTestReading(name, value, 0.0, breached, false));
                    if (breached)
                        _pendingEvent ??= name;
                }
            }
        }

        /// <summary>
        ///     The clean-up deposit, if exercised this payment date. A replayed month exercises it where the
        ///     statement prints a deposit. A forecast month exercises it on the stated date, or after the
        ///     stated delay from the first eligible date, if the price and the trust's cash pay everything.
        /// </summary>
        private double? CleanUp(RevolvingPeriodDates dates, OwnerTrustActual? actual, double poolBefore,
            int forecastMonth)
        {
            if (actual?.CleanUpDeposit is { } printed)
                return printed;
            if (_trust.CleanUp is not { } cleanUp || forecastMonth < 0)
                return null;

            var eligible = poolBefore <= cleanUp.PoolFactor * cleanUp.ClosingPoolBalance + Cent;
            if (!eligible)
                return null;
            _eligibleMonths++;
            var exercise = cleanUp.Date is { } date
                ? dates.DistributionDate.Year == date.Year && dates.DistributionDate.Month == date.Month
                  || dates.DistributionDate > date
                : _eligibleMonths > cleanUp.DelayMonths;
            if (!exercise)
                return null;

            var basis = cleanUp.PriceBasis == CleanUpPriceBasis.PoolFace ? poolBefore : Notes;
            var price = cleanUp.Price * basis;
            return price;
        }

        private bool FloorPass(int forecastMonth)
        {
            var path = _trust.FloorTestsPass;
            if (forecastMonth < 0 || path.Count == 0)
                return true;
            return path[Math.Min(forecastMonth, path.Count - 1)];
        }

        private double DelinquencyDriver(int forecastMonth)
        {
            var path = _trust.Delinquency;
            if (forecastMonth >= 0 && path.Count > 0)
                return path[Math.Min(forecastMonth, path.Count - 1)];
            return _delinquency.Count > 0 ? _delinquency[^1] : 0.0;
        }

        private void Accrue(ClassState c, RevolvingPeriodDates dates)
        {
            var config = c.Config;
            c.IndexValue = config.Index is { } index ? _rates.GetRate(index, dates.PeriodStart) : null;
            c.RatePct = c.IndexValue is { } value ? value + config.Margin : config.Coupon!.Value;
            var fraction = config.Basis == AccrualBasis.Actual360 ? dates.AccrualDays / 360.0 : 1.0 / 12.0;
            c.Accrued = c.Balance * c.RatePct / 100.0 * fraction;
            c.Due = c.Shortfall * (1.0 + c.RatePct / 100.0 * fraction) + c.Accrued;
            c.Shortfall = c.Due;
        }

        private double WeightedRatePct()
        {
            var notes = Notes;
            return notes > 0.0 ? _classes.Sum(c => c.Balance * c.RatePct) / notes : 0.0;
        }

        private void PayInterest(Waterfall w, string tier)
        {
            var members = _classes.Where(c => c.Tier == tier).ToList();
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
        ///     The notes of this tier and every tier above it at the last payment date, less the adjusted
        ///     pool and the priority principal already paid this date. On and after the tier's final
        ///     maturity date, at least what the tier still owes.
        /// </summary>
        private double PriorityNeed(string tier, double adjusted, double paidSoFar, RevolvingPeriodDates dates,
            bool cleanUp)
        {
            var index = _trust.Tiers.Select(t => t.Name).ToList().IndexOf(tier);
            var through = _trust.Tiers.Take(index + 1).Select(t => t.Name).ToHashSet();
            var notesThrough = _classes.Where(c => through.Contains(c.Tier)).Sum(c => c.Begin);
            var need = Math.Max(0.0, notesThrough - adjusted - paidSoFar);
            var config = _trust.Tiers[index];
            if (config.FinalMaturity is { } maturity && dates.DistributionDate >= maturity)
                need = Math.Max(need, _classes.Where(c => c.Tier == tier).Sum(c => c.Balance));
            return need;
        }

        /// <summary>What the tiers past their final maturity still owe.</summary>
        private double MaturedLeft(RevolvingPeriodDates dates)
        {
            var matured = _trust.Tiers
                .Where(t => t.FinalMaturity is { } m && dates.DistributionDate >= m)
                .Select(t => t.Name)
                .ToHashSet();
            return _classes.Where(c => matured.Contains(c.Tier)).Sum(c => c.Balance);
        }

        /// <summary>Principal sequentially by tier, pro rata to balance within a tier.</summary>
        private double PayPrincipal(Waterfall w, double need, OwnerTrustVerb verb)
        {
            var paid = w.Spend(Math.Min(need, Notes), verb);
            var left = paid;
            foreach (var tier in _trust.Tiers)
            {
                if (left <= 0.0)
                    break;
                var members = _classes.Where(c => c.Tier == tier.Name && c.Balance > 0.0).ToList();
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

            return paid;
        }

        private void Disclose()
        {
            var forecast = _run.Pool.Periods > _run.Pool.ActualPeriods.Count;
            if (!forecast)
                return;
            if (_trust.FloorTestsPass.Count == 0)
                _disclosures.Add("No floor test path was supplied, so every forecast month passes the floor tests.");
            if (_trust.Tests.DelinquencyLimit != null && _trust.Delinquency.Count == 0)
                _disclosures.Add(
                    "No delinquency path was supplied, so forecast months hold the last delinquency ratio the run knows.");
            if (_trust.AcquisitionPrice == null && _rows.Any(r => !r.Replayed && r.Phase == SeriesPhase.Revolving))
                _disclosures.Add(
                    "No acquisition price was supplied, so the acquisition account buys nothing in forecast months.");
            if (_trust.ReserveEarningsRate == 0.0)
                _disclosures.Add("No reserve earnings rate was supplied, so the reserve account earns nothing.");
            if (_trust.CleanUp == null)
                _disclosures.Add("No clean-up was supplied, so the trust runs until the notes are paid.");
        }

        /// <summary>
        ///     The month's available funds, spent step by step. A step the negative carry account covers
        ///     draws on it while revolving, and a step the reserve covers draws on the reserve.
        /// </summary>
        private sealed class Waterfall
        {
            private readonly Loop _loop;
            private readonly bool _revolving;

            private readonly bool _endOfTrust;

            /// <param name="endOfTrust">a clean-up: the reserve then covers any step but the reserve's and the certificate's.</param>
            public Waterfall(Loop loop, double available, bool revolving, bool endOfTrust)
            {
                _loop = loop;
                Available = available;
                _revolving = revolving;
                _endOfTrust = endOfTrust;
            }

            public double Available { get; private set; }
            public double ReserveDrawn { get; private set; }
            public double NegativeCarryDrawn { get; private set; }

            public double Spend(double need, OwnerTrustVerb verb)
            {
                if (need <= 0.0)
                    return 0.0;
                var paid = Math.Min(need, Available);
                Available -= paid;
                if (paid < need && _revolving && OwnerTrustVerbs.NegativeCarryCovers(verb))
                {
                    var draw = Math.Min(need - paid, _loop._negativeCarry);
                    _loop._negativeCarry -= draw;
                    NegativeCarryDrawn += draw;
                    paid += draw;
                }

                var covered = OwnerTrustVerbs.ReserveCovers(verb)
                              || _endOfTrust && verb is not (OwnerTrustVerb.FundReserve or OwnerTrustVerb.PayCertificate);
                if (paid < need && covered)
                {
                    var draw = Math.Min(need - paid, _loop._reserve);
                    _loop._reserve -= draw;
                    ReserveDrawn += draw;
                    paid += draw;
                }

                return paid;
            }
        }
    }
}
