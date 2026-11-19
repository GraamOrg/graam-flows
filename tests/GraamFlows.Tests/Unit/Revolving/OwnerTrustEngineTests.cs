using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Revolving;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>
///     An amortizing owner trust over an installment pool, mechanism by mechanism, against closed forms.
///     The pool is one level 0% cohort, so its present value is an annuity.
/// </summary>
public class OwnerTrustEngineTests
{
    private static readonly DateTime Statement = new(2026, 7, 31);
    private const double Rate = 0.06;
    private const double I = Rate / 12.0;
    private const double Payment = 50_000.0;
    private const int Term = 24;
    private const double Fees = 2_000.0;

    private static double Annuity(double payment, int n)
    {
        return payment * (1.0 - Math.Pow(1.0 + I, -n)) / I;
    }

    private static double Pv0 => Annuity(Payment, Term);

    /// <summary>Notes sized so the trust opens with its revolving target exactly met: notes / (1 − 10.5%) = PV.</summary>
    private static double Notes => Pv0 * (1.0 - 0.105);

    private static IReadOnlyList<RevolvingClass> Classes(double total)
    {
        return new[]
        {
            new RevolvingClass { Key = "A-1", Balance = total * 0.6, Coupon = 1.2 },
            new RevolvingClass { Key = "A-2", Balance = total * 0.2, Coupon = 2.4 },
            new RevolvingClass { Key = "B", Balance = total * 0.12, Coupon = 3.0 },
            new RevolvingClass { Key = "C", Balance = total * 0.08, Coupon = 4.0 }
        };
    }

    private static IReadOnlyList<OwnerTrustTier> Tiers => new[]
    {
        new OwnerTrustTier("A", new[] { "A-1", "A-2" }),
        new OwnerTrustTier("B", new[] { "B" }),
        new OwnerTrustTier("C", new[] { "C" })
    };

    /// <summary>The documents' order.</summary>
    private static IReadOnlyList<OwnerTrustStep> Steps => new[]
    {
        new OwnerTrustStep(OwnerTrustVerb.PayFees),
        new OwnerTrustStep(OwnerTrustVerb.PayServicing),
        new OwnerTrustStep(OwnerTrustVerb.PayInterest, "A"),
        new OwnerTrustStep(OwnerTrustVerb.PriorityPrincipal, "A"),
        new OwnerTrustStep(OwnerTrustVerb.PayInterest, "B"),
        new OwnerTrustStep(OwnerTrustVerb.PriorityPrincipal, "B"),
        new OwnerTrustStep(OwnerTrustVerb.PayInterest, "C"),
        new OwnerTrustStep(OwnerTrustVerb.PriorityPrincipal, "C"),
        new OwnerTrustStep(OwnerTrustVerb.RegularPrincipal),
        new OwnerTrustStep(OwnerTrustVerb.AcceleratedPrincipal),
        new OwnerTrustStep(OwnerTrustVerb.FundReserve),
        new OwnerTrustStep(OwnerTrustVerb.DepositAcquisition),
        new OwnerTrustStep(OwnerTrustVerb.FundNegativeCarry),
        new OwnerTrustStep(OwnerTrustVerb.PayCertificate)
    };

    private static OwnerTrustConfig Config => new()
    {
        DiscountRate = Rate,
        Tiers = Tiers,
        Steps = Steps,
        ServicingRate = 0.0075,
        MonthlyFees = Fees,
        Oc = new OwnerTrustOc
        {
            RevolvingPct = 0.105, RevolvingFailingPct = 0.135, AmortizingPct = 0.145, AmortizingFailingPct = 0.175,
            Floor = 10_000.0
        },
        RequiredReserve = 12_000.0,
        AcquisitionPrice = new InstallmentPrice { PvRate = Rate },
        AcquisitionTerms = new[] { new InstallmentTerm(Term, 1.0) }
    };

    private static InstallmentPoolRun Pool(int periods, double writeOff = 0.0, double prepay = 0.0,
        IReadOnlyList<InstallmentActual>? actuals = null)
    {
        return new InstallmentPoolRun
        {
            StatementDate = Statement,
            Periods = periods,
            Cohorts = new[]
            {
                new InstallmentCohort { Name = "opening", Age = 6, Schedule = InstallmentCohort.LevelSchedule(Payment * Term, Term) }
            },
            DiscountRates = new[] { Rate },
            Drivers = new InstallmentDrivers
            {
                Prepayment = new InstallmentCurve(new[] { prepay }),
                WriteOff = new InstallmentCurve(new[] { writeOff })
            },
            ActualPeriods = actuals ?? Array.Empty<InstallmentActual>()
        };
    }

    private static OwnerTrustRun Run(int periods = 1, OwnerTrustConfig? config = null, OwnerTrustState? state = null,
        double? notes = null, double writeOff = 0.0, double prepay = 0.0, IReadOnlyList<InstallmentActual>? poolActuals = null,
        IReadOnlyList<OwnerTrustActual>? actuals = null, IReadOnlyList<RevolvingClass>? classes = null)
    {
        return new OwnerTrustRun
        {
            Name = "notes",
            Pool = Pool(periods, writeOff, prepay, poolActuals),
            Trust = config ?? Config,
            Classes = classes ?? Classes(notes ?? Notes),
            State = state ?? new OwnerTrustState { Reserve = 12_000.0 },
            ActualPeriods = actuals ?? Array.Empty<OwnerTrustActual>(),
            DistributionDay = 20
        };
    }

    private static OwnerTrustResult Execute(OwnerTrustRun run)
    {
        return OwnerTrustEngine.Run(run, new ConstantRateProvider(4.0));
    }

    private static double Interest(IEnumerable<RevolvingClass> classes)
    {
        return classes.Sum(c => c.Balance * c.Coupon!.Value / 100.0 / 12.0);
    }

    [Fact]
    public void TheAdjustedPoolIsThePresentValueAtTheDiscountRateAndYsocIsTheRest()
    {
        var month = Execute(Run(state: new OwnerTrustState { Reserve = 12_000.0 })).Periods[0];

        month.BeginningAdjustedPool.Should().BeApproximately(Pv0, 1e-6);
        month.AdjustedPoolBeforeAdditions.Should().BeApproximately(Annuity(Payment, Term - 1), 1e-6);
        month.Ysoc.Should().BeApproximately(month.EndingPool - month.EndingAdjustedPool, 1e-9);
    }

    [Fact]
    public void RevolvingDepositsWhatRestoresTheTargetAndBuysItAtThePresentValue()
    {
        var run = Run();
        var month = Execute(run).Periods[0];

        // notes + target = notes / (1 − 10.5%) = PV0, so the deposit is PV0 less the pool after its payment.
        var adjustedBefore = Annuity(Payment, Term - 1);
        var deposit = Pv0 - adjustedBefore;
        deposit.Should().BeApproximately(Payment - I * Pv0, 1e-6);
        month.Phase.Should().Be(SeriesPhase.Revolving);
        month.OcTarget.Should().BeApproximately(Notes / (1.0 - 0.105) - Notes, 1e-6);
        month.AcquisitionDeposit.Should().BeApproximately(deposit, 1e-6);
        month.AcquisitionCash.Should().BeApproximately(deposit, 1e-6);
        month.AdditionsValue.Should().BeApproximately(deposit, 1e-6);
        month.EndingAdjustedPool.Should().BeApproximately(Pv0, 1e-6);
        month.Oc.Should().BeApproximately(month.OcTarget, 1e-6);
        month.PrincipalPaid.Should().Be(0.0);

        // What is left is the pool's accretion less fees, servicing and interest.
        var servicing = 0.0075 / 12.0 * Pv0;
        month.ServicingFee.Should().BeApproximately(servicing, 1e-9);
        month.InterestPaid.Should().BeApproximately(Interest(run.Classes), 1e-9);
        month.CertificatePaid.Should().BeApproximately(I * Pv0 - Fees - servicing - Interest(run.Classes), 1e-6);
    }

    [Fact]
    public void FailedFloorTestsRaiseTheRevolvingTargetAndTheFloorHoldsIt()
    {
        var failing = Execute(Run(actuals: new[] { new OwnerTrustActual { FloorTestsPass = false } },
            poolActuals: new[] { Printed(Payment) })).Periods[0];
        failing.OcTargetPct.Should().Be(0.135);
        failing.OcTarget.Should().BeApproximately(Notes / (1.0 - 0.135) - Notes, 1e-6);

        var tiny = Execute(Run(notes: 1_000.0)).Periods[0];
        tiny.OcTarget.Should().Be(10_000.0, "the floor is above 10.5% of $1,000 of notes");
    }

    private static InstallmentActual Printed(double scheduled, double prepaid = 0.0, double writeOffs = 0.0)
    {
        return new InstallmentActual
        {
            ScheduledCollections = scheduled, PrepaidCollections = prepaid, WriteOffs = writeOffs
        };
    }

    [Fact]
    public void AmortizingPaysTheNotesDownToTheAdjustedPoolLessItsTarget()
    {
        var notes = Notes;
        var run = Run(state: new OwnerTrustState { Phase = SeriesPhase.Amortizing, Reserve = 12_000.0 });
        var month = Execute(run).Periods[0];

        var adjusted = Annuity(Payment, Term - 1);
        var target = 0.145 * adjusted;
        var regular = notes - (adjusted - target);
        var cash = Payment - Fees - 0.0075 / 12.0 * Pv0 - Interest(run.Classes);
        month.OcTarget.Should().BeApproximately(target, 1e-6);
        month.PriorityPrincipal.Should().Be(0.0);
        month.RegularPrincipal.Should().BeApproximately(Math.Min(regular, cash), 1e-6);
        month.AcquisitionDeposit.Should().Be(0.0);
        month.AdditionsFace.Should().Be(0.0);
    }

    [Fact]
    public void ClassAIsPaidProRataAndTheJuniorClassesWaitSequentially()
    {
        var run = Run(state: new OwnerTrustState { Phase = SeriesPhase.Amortizing, Reserve = 12_000.0 });
        var month = Execute(run).Periods[0];
        var byKey = month.Classes.ToDictionary(c => c.Key);

        byKey["A-1"].PrincipalPaid.Should().BeApproximately(3.0 * byKey["A-2"].PrincipalPaid, 1e-6);
        byKey["B"].PrincipalPaid.Should().Be(0.0);
        byKey["C"].PrincipalPaid.Should().Be(0.0);
    }

    [Fact]
    public void PriorityPrincipalPaysTheSeniorNotesDownToTheAdjustedPoolBeforeJuniorInterest()
    {
        // Notes above the adjusted pool: Class A alone is above it, so the first priority payment
        // pays A down to it before B's interest.
        var adjusted = Annuity(Payment, Term - 1);
        var classes = new[]
        {
            new RevolvingClass { Key = "A-1", Balance = adjusted + 30_000.0, Coupon = 1.2 },
            new RevolvingClass { Key = "A-2", Balance = 10_000.0, Coupon = 2.4 },
            new RevolvingClass { Key = "B", Balance = 100_000.0, Coupon = 3.0 },
            new RevolvingClass { Key = "C", Balance = 100_000.0, Coupon = 4.0 }
        };
        var run = Run(state: new OwnerTrustState { Phase = SeriesPhase.Amortizing, Reserve = 12_000.0 }, classes: classes);
        var month = Execute(run).Periods[0];

        var first = adjusted + 40_000.0 - adjusted;
        var second = 100_000.0;
        var third = 100_000.0;
        month.PriorityPrincipal.Should().BeGreaterThan(0.0);
        var available = Payment - Fees - 0.0075 / 12.0 * Pv0;
        var aInterest = Interest(classes.Take(2));
        // First priority comes straight after A's interest, so it is paid in full.
        (month.Classes[0].PrincipalPaid + month.Classes[1].PrincipalPaid).Should()
            .BeGreaterThanOrEqualTo(Math.Min(first, available - aInterest) - 1e-6);
        // Everything went to the notes: no release while they exceed the pool.
        month.CertificatePaid.Should().Be(0.0);
        (first + second + third).Should().BeGreaterThan(available);
    }

    [Fact]
    public void TheServicingFeeIsAQuarterPointAnnualOnTheOpeningAdjustedPool()
    {
        var periods = Execute(Run(periods: 3)).Periods;
        foreach (var p in periods)
            p.ServicingFee.Should().BeApproximately(0.0075 / 12.0 * p.BeginningAdjustedPool, 1e-9);
        periods[1].BeginningAdjustedPool.Should().BeApproximately(periods[0].EndingAdjustedPool, 1e-9);
    }

    [Fact]
    public void TheScheduledEndOfRevolvingStartsAmortizationWithoutAcceleration()
    {
        var config = Config with { RevolvingEnd = new DateTime(2026, 10, 1) };
        var result = Execute(Run(periods: 3, config: config));

        // Payment dates: 2026-09-21, 2026-10-20, 2026-11-20.
        result.Periods.Select(p => p.Phase).Should().Equal(SeriesPhase.Revolving, SeriesPhase.Amortizing,
            SeriesPhase.Amortizing);
        result.Periods[1].AcceleratedPrincipal.Should().Be(0.0);
        result.Periods[1].Event.Should().BeNull();
        result.PhasePath.Should().ContainSingle().Which.Event.Should().BeNull();
    }

    [Fact]
    public void TheWriteOffRatioIsThreeMonthsOfWriteOffsOnTheOpeningPoolTimesFour()
    {
        // 0.3% of the schedule a month: each month's ratio is 0.3%, so the test reads 3.6%.
        var config = Config with { Tests = new OwnerTrustTests { WriteOffLimit = 0.10 } };
        var periods = Execute(Run(periods: 3, config: config, writeOff: 0.003)).Periods;

        periods[0].Tests.Single().Value.Should().BeNull("the window is not yet full");
        periods[2].Tests.Single().Value.Should().BeApproximately(3 * 0.003 * 4, 1e-12);
        periods[2].Tests.Single().Breached.Should().BeFalse();
        periods.Should().OnlyContain(p => p.Phase == SeriesPhase.Revolving);
    }

    [Fact]
    public void ATrippedWriteOffTestEndsRevolvingThatPaymentDateAndLatches()
    {
        var config = Config with { Tests = new OwnerTrustTests { WriteOffLimit = 0.10 } };
        var state = new OwnerTrustState { Reserve = 12_000.0, WriteOffHistory = new[] { 0.01, 0.01 } };
        var result = Execute(Run(periods: 3, config: config, state: state, writeOff: 0.01));

        var first = result.Periods[0];
        first.Phase.Should().Be(SeriesPhase.EarlyAmortization);
        first.Event.Should().Be("WRITE_OFF_RATIO");
        first.AcquisitionDeposit.Should().Be(0.0);
        first.PrincipalPaid.Should().BeGreaterThan(0.0);
        first.CertificatePaid.Should().Be(0.0, "accelerated principal takes everything left");
        result.Periods.Should().OnlyContain(p => p.Phase == SeriesPhase.EarlyAmortization);
        result.PhasePath.Should().ContainSingle();
    }

    [Fact]
    public void AReplayedMonthTakesThePrintedRatioAndReportsItsOwnBeside()
    {
        var config = Config with { Tests = new OwnerTrustTests { WriteOffLimit = 0.10 } };
        var state = new OwnerTrustState { Reserve = 12_000.0, WriteOffHistory = new[] { 0.0, 0.0 } };
        var month = Execute(Run(config: config, state: state, poolActuals: new[] { Printed(Payment) },
            actuals: new[] { new OwnerTrustActual { WriteOffRatio = 0.11 } })).Periods[0];

        var test = month.Tests.Single();
        test.Printed.Should().BeTrue();
        test.Value.Should().Be(0.11);
        test.Computed.Should().Be(0.0);
        month.Phase.Should().Be(SeriesPhase.EarlyAmortization);
    }

    [Fact]
    public void TheDelinquencyTestAveragesTheWindowWithTheDriverInForecastMonths()
    {
        var config = Config with
        {
            Tests = new OwnerTrustTests { DelinquencyLimit = 0.02 },
            Delinquency = new[] { 0.025 }
        };
        var state = new OwnerTrustState { Reserve = 12_000.0, DelinquencyHistory = new[] { 0.01, 0.015 } };
        var month = Execute(Run(config: config, state: state)).Periods[0];

        month.Tests.Single().Value.Should().BeApproximately((0.01 + 0.015 + 0.025) / 3.0, 1e-12);
        month.Tests.Single().Breached.Should().BeFalse();

        var second = Execute(Run(periods: 2, config: config, state: state)).Periods[1];
        second.Tests.Single().Value.Should().BeApproximately((0.015 + 0.025 + 0.025) / 3.0, 1e-12);
        second.Phase.Should().Be(SeriesPhase.EarlyAmortization);
    }

    [Fact]
    public void AnOcShortfallAfterPaymentsActsFromTheNextPaymentDate()
    {
        // Write-offs the excess spread cannot replace leave the trust short of its target.
        var config = Config with { Tests = new OwnerTrustTests { OcShortfall = true } };
        var result = Execute(Run(periods: 2, config: config, writeOff: 0.02));

        result.Periods[0].Oc.Should().BeLessThan(result.Periods[0].OcTarget);
        result.Periods[0].Phase.Should().Be(SeriesPhase.Revolving);
        result.Periods[1].Phase.Should().Be(SeriesPhase.EarlyAmortization);
        result.Periods[1].Event.Should().Be("OVERCOLLATERALIZATION");
    }

    [Fact]
    public void AShortfallOnlyFromANewPercentHasItsGraceMonths()
    {
        var config = Config with { Tests = new OwnerTrustTests { OcShortfall = true } };
        var state = new OwnerTrustState { Reserve = 12_000.0, OcPctHistory = new[] { 0.105 } };
        var result = Execute(Run(periods: 2, config: config, state: state, writeOff: 0.02,
            poolActuals: new[] { Printed(Payment, writeOffs: 0.02 * Payment * Term) },
            actuals: new[] { new OwnerTrustActual { FloorTestsPass = false } }));

        result.Periods[0].OcTargetPct.Should().Be(0.135);
        result.Periods[0].Tests.Single().Breached.Should().BeFalse("the percent changed this month");
        result.Periods[1].Phase.Should().Be(SeriesPhase.Revolving);
    }

    [Fact]
    public void TheParityTestReadsTheAdjustedPoolOverTheNotesAfterPayments()
    {
        var config = Config with { Tests = new OwnerTrustTests { ParityLimit = 0.5 } };
        var month = Execute(Run(config: config)).Periods[0];
        month.Tests.Single().Value.Should().BeApproximately(month.EndingAdjustedPool / month.NoteBalance, 1e-12);

        var thin = Execute(Run(periods: 2, config: config, notes: Pv0 * 2.5)).Periods;
        thin[0].Tests.Single().Breached.Should().BeTrue();
        thin[1].Event.Should().Be("ADJUSTED_POOL_TO_NOTES");
    }

    [Fact]
    public void TheReserveCoversInterestAndIsToppedUpAfter()
    {
        // A month that collects nothing: the reserve pays fees, servicing and interest.
        var run = Run(periods: 2, poolActuals: new[] { Printed(0.0) });
        var periods = Execute(run).Periods;

        var owed = Fees + 0.0075 / 12.0 * Pv0 + Interest(run.Classes);
        periods[0].ReserveDraw.Should().BeApproximately(Math.Min(owed, 12_000.0), 1e-9);
        periods[0].Reserve.Should().BeApproximately(Math.Max(0.0, 12_000.0 - owed), 1e-9);
        periods[1].ReserveDeposit.Should().BeApproximately(12_000.0 - periods[0].Reserve, 1e-9);
        periods[1].Reserve.Should().BeApproximately(12_000.0, 1e-9);
    }

    [Fact]
    public void UnpaidInterestCarriesWithInterestOnIt()
    {
        var run = Run(periods: 2, state: new OwnerTrustState(), poolActuals: new[] { Printed(0.0) },
            config: Config with { RequiredReserve = 0.0 });
        var periods = Execute(run).Periods;

        var c = periods[0].Classes.Single(k => k.Key == "C");
        c.InterestPaid.Should().Be(0.0);
        c.InterestShortfall.Should().BeApproximately(c.InterestAccrued, 1e-9);
        var next = periods[1].Classes.Single(k => k.Key == "C");
        next.InterestDue.Should().BeApproximately(c.InterestShortfall * (1.0 + 0.04 / 12.0) + next.InterestAccrued, 1e-9);
        next.InterestShortfall.Should().Be(0.0);
    }

    [Fact]
    public void AFloatingClassAccruesItsIndexPlusMarginOnActualDays()
    {
        var classes = Classes(Notes).Select(c => c.Key == "A-2"
            ? c with { Coupon = null, Index = MarketDataInstEnum.Libor1M, Margin = 0.5, Basis = AccrualBasis.Actual360 }
            : c).ToList();
        var month = Execute(Run(classes: classes)).Periods[0];

        var a2 = month.Classes.Single(c => c.Key == "A-2");
        a2.CouponPct.Should().Be(4.5);
        a2.AccrualDays.Should().Be(32, "2026-08-20 to 2026-09-21");
        a2.InterestAccrued.Should().BeApproximately(Notes * 0.2 * 0.045 * 32 / 360.0, 1e-9);
    }

    [Fact]
    public void AnAcquisitionAccountIsCarriedNegativelyAndReleasedWhenAmortizationStarts()
    {
        var notes = Notes;
        var classes = Classes(notes);
        var weighted = classes.Sum(c => c.Balance * c.Coupon!.Value) / notes;

        // No acquisition price: the deposit stays in the account and the negative carry account covers it.
        var revolving = Execute(Run(config: Config with { AcquisitionPrice = null, AcquisitionTerms = Array.Empty<InstallmentTerm>() }))
            .Periods[0];
        revolving.AcquisitionAccount.Should().BeApproximately(revolving.AcquisitionDeposit, 1e-9);
        revolving.NegativeCarryRequired.Should().BeApproximately(revolving.AcquisitionAccount * weighted / 100.0 / 12.0, 1e-9);
        revolving.NegativeCarry.Should().BeApproximately(revolving.NegativeCarryRequired, 1e-9);
        revolving.Oc.Should().BeApproximately(revolving.EndingAdjustedPool + revolving.AcquisitionAccount - notes, 1e-6);

        var state = new OwnerTrustState
        {
            Phase = SeriesPhase.Amortizing, Reserve = 12_000.0, AcquisitionAccount = 40_000.0, NegativeCarry = 500.0
        };
        var amortizing = Execute(Run(state: state)).Periods[0];
        amortizing.AcquisitionRelease.Should().Be(40_000.0);
        amortizing.NegativeCarryRelease.Should().Be(500.0);
        amortizing.AcquisitionAccount.Should().Be(0.0);
        amortizing.PrincipalPaid.Should().BeGreaterThan(Execute(Run(state: state with { AcquisitionAccount = 0.0, NegativeCarry = 0.0 }))
            .Periods[0].PrincipalPaid);
    }

    private static OwnerTrustConfig WithCleanUp(int delay = 0, CleanUpPriceBasis basis = CleanUpPriceBasis.PoolFace,
        double price = 1.0)
    {
        return Config with
        {
            CleanUp = new OwnerTrustCleanUp
            {
                PoolFactor = 0.10, ClosingPoolBalance = Payment * Term * 10.0, DelayMonths = delay, PriceBasis = basis,
                Price = price
            }
        };
    }

    [Fact]
    public void TheCleanUpPaysTheNotesAtParWithInterestAndTheTrustEnds()
    {
        var state = new OwnerTrustState { Phase = SeriesPhase.Amortizing, Reserve = 12_000.0 };
        var result = Execute(Run(periods: 6, config: WithCleanUp(), state: state));

        result.Ended.Should().Be("CLEAN_UP");
        result.Periods.Should().HaveCount(1, "the pool is at 10% of the closing pool from the first month");
        var month = result.Periods[0];
        month.CleanUpDeposit.Should().BeApproximately(month.PoolBeforeAdditions, 1e-6);
        month.NoteBalance.Should().Be(0.0);
        month.Classes.Should().OnlyContain(c => c.InterestShortfall == 0.0 && c.EndBalance == 0.0);
        month.ReserveRelease.Should().Be(12_000.0, "the reserve is released on its own line");
        var notes = month.Classes.Sum(c => c.BeginBalance);
        month.CertificatePaid.Should().BeApproximately(
            month.AvailableFunds - Fees - month.ServicingFee - month.InterestPaid - notes, 1e-6);
        result.Closing.Should().BeNull();
        result.Disclosures.Should().Contain(d => d.Contains("CLEAN_UP"));
    }

    [Fact]
    public void ACleanUpWaitsItsStatedMonthsAfterTheFirstEligibleDate()
    {
        var state = new OwnerTrustState { Phase = SeriesPhase.Amortizing, Reserve = 12_000.0 };
        var result = Execute(Run(periods: 6, config: WithCleanUp(delay: 2), state: state));

        result.Periods.Should().HaveCount(3);
        result.Periods.Take(2).Should().OnlyContain(p => p.CleanUpDeposit == 0.0);
        result.Periods[2].CleanUpDeposit.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void ACleanUpTheTrustCannotPayIsNotExercised()
    {
        var state = new OwnerTrustState { Phase = SeriesPhase.Amortizing, Reserve = 12_000.0 };
        var result = Execute(Run(periods: 1, config: WithCleanUp(price: 0.01), state: state));

        result.Ended.Should().BeNull();
        result.Periods[0].CleanUpDeposit.Should().Be(0.0);
        result.Disclosures.Should().Contain(d => d.Contains("not exercised"));
    }

    [Fact]
    public void APrintedCleanUpDepositIsTakenAsPrinted()
    {
        var state = new OwnerTrustState { Phase = SeriesPhase.Amortizing, Reserve = 12_000.0 };
        var month = Execute(Run(state: state, poolActuals: new[] { Printed(Payment) },
            actuals: new[] { new OwnerTrustActual { CleanUpDeposit = 1_500_000.0 } })).Periods[0];

        month.CleanUpDeposit.Should().Be(1_500_000.0);
        month.NoteBalance.Should().Be(0.0);
    }

    [Fact]
    public void APrintedAdjustedPoolRunsTheWaterfallAndTheRunsOwnValueGoesBeside()
    {
        var state = new OwnerTrustState { Phase = SeriesPhase.Amortizing, Reserve = 12_000.0 };
        var printed = 1_000_000.0;
        var month = Execute(Run(state: state, poolActuals: new[] { Printed(Payment) },
            actuals: new[] { new OwnerTrustActual { AdjustedPool = printed } })).Periods[0];

        month.EndingAdjustedPool.Should().Be(printed);
        month.ComputedAdjustedPool.Should().BeApproximately(Annuity(Payment, Term - 1), 1e-6);
        month.OcTarget.Should().BeApproximately(0.145 * printed, 1e-6);
    }

    [Fact]
    public void ACutRunMatchesOneLongRun()
    {
        var config = Config with { RevolvingEnd = new DateTime(2026, 11, 1) };
        var whole = Execute(Run(periods: 6, config: config, writeOff: 0.002, prepay: 0.01));
        var first = Execute(Run(periods: 3, config: config, writeOff: 0.002, prepay: 0.01));

        var rest = new OwnerTrustRun
        {
            Name = "notes",
            Pool = Pool(3, 0.002, 0.01) with { StatementDate = new DateTime(2026, 10, 31), Cohorts = first.ClosingPool },
            Trust = config,
            Classes = first.ClosingClasses.Select(c => Classes(Notes).Single(k => k.Key == c.Key) with
            {
                Balance = c.Balance, InterestShortfall = c.InterestShortfall
            }).ToList(),
            State = first.Closing!,
            DistributionDay = 20
        };
        var second = Execute(rest);

        for (var k = 0; k < 3; k++)
        {
            second.Periods[k].NoteBalance.Should().BeApproximately(whole.Periods[k + 3].NoteBalance, 1e-6);
            second.Periods[k].CertificatePaid.Should().BeApproximately(whole.Periods[k + 3].CertificatePaid, 1e-6);
            second.Periods[k].EndingAdjustedPool.Should().BeApproximately(whole.Periods[k + 3].EndingAdjustedPool, 1e-6);
            second.Periods[k].Phase.Should().Be(whole.Periods[k + 3].Phase);
        }
    }

    [Fact]
    public void TheFederalReserveCalendarRollsAJuneteenthPaymentDate()
    {
        var settlement = RevolvingCalendar.Build(new DateTime(2022, 4, 30), 20, 1);
        var reserve = RevolvingCalendar.Build(new DateTime(2022, 4, 30), 20, 1,
            new GraamFlows.Util.Calender.Calendars.UnitedStates(
                GraamFlows.Util.Calender.Calendars.UnitedStates.Market.FederalReserve));

        settlement[0].DistributionDate.Should().Be(new DateTime(2022, 6, 20));
        reserve[0].DistributionDate.Should().Be(new DateTime(2022, 6, 21));
    }

    [Fact]
    public void ABoughtTermKeepsItsStatedAgeForTheCurves()
    {
        var run = Pool(2, 0.0, 0.0) with
        {
            Drivers = new InstallmentDrivers
            {
                Prepayment = new InstallmentCurve(new[] { 0.0 }),
                WriteOff = new InstallmentCurve(new[] { 0.0 }),
                Reinvest = new InstallmentReinvest
                {
                    Share = 1.0, Price = new InstallmentPrice { PerFace = 0.9 },
                    Terms = new[] { new InstallmentTerm(20, 0.5, 4), new InstallmentTerm(36, 0.5) }
                }
            }
        };
        var closing = InstallmentPoolLedger.Run(run).Closing;

        // Bought at the close of the first month, each has run one month by the close of the second.
        closing.Single(c => c.Name == "reinvest-0-20").Age.Should().Be(5);
        closing.Single(c => c.Name == "reinvest-0-36").Age.Should().Be(1);
    }

    [Theory]
    [InlineData("tier")]
    [InlineData("step")]
    [InlineData("last")]
    [InlineData("reinvest")]
    [InlineData("purchases")]
    [InlineData("early")]
    public void ARunTheEngineCannotReadIsRefusedByName(string what)
    {
        var run = Run();
        run = what switch
        {
            "tier" => run with { Trust = Config with { Tiers = Tiers.Take(2).ToList() } },
            "step" => run with
            {
                Trust = Config with { Steps = Steps.Prepend(new OwnerTrustStep(OwnerTrustVerb.PayInterest, "Z")).ToList() }
            },
            "last" => run with { Trust = Config with { Steps = Steps.Take(Steps.Count - 1).ToList() } },
            "reinvest" => run with
            {
                Pool = run.Pool with
                {
                    Drivers = run.Pool.Drivers! with
                    {
                        Reinvest = new InstallmentReinvest
                        {
                            Share = 1.0, Price = new InstallmentPrice { PerFace = 0.9 },
                            Terms = new[] { new InstallmentTerm(24, 1.0) }
                        }
                    }
                }
            },
            "purchases" => run with
            {
                Pool = run.Pool with
                {
                    Purchases = new[]
                    {
                        new InstallmentPurchase
                        {
                            Cohort = new InstallmentCohort { Age = 0, Schedule = new[] { 1.0 } }, Cash = 1.0, Period = 0
                        }
                    }
                }
            },
            "early" => run with { State = new OwnerTrustState { Phase = SeriesPhase.EarlyAmortization } },
            _ => run
        };

        var act = () => Execute(run);
        var expected = what switch
        {
            "tier" => "*Class 'C' sits in 0 tiers*",
            "step" => "*tier 'Z'*",
            "last" => "*PAY_CERTIFICATE*",
            "reinvest" => "*pool.drivers.reinvest*",
            "purchases" => "*pool.purchases*",
            "early" => "*amortization event*",
            _ => "*"
        };
        act.Should().Throw<RevolvingConfigException>().WithMessage(expected);
    }
}
