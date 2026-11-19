using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Revolving;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>
///     A master trust of series over one installment pool, mechanism by mechanism, against closed forms.
///     The pool is one level 0% cohort, so each series' present value of it is an annuity at its own rate.
/// </summary>
public class MasterTrustEngineTests
{
    private static readonly DateTime Statement = new(2026, 7, 31);
    private const double Payment = 1_000_000.0;
    private const int Term = 30;
    private const double RateA = 0.06;
    private const double RateB = 0.09;
    private const double Oc = 0.0825;

    private static double Annuity(double rate, int n, double payment = Payment)
    {
        var i = rate / 12.0;
        return payment * (1.0 - Math.Pow(1.0 + i, -n)) / i;
    }

    private static double Round(double value, int decimals)
    {
        return (double)Math.Round((decimal)value, decimals, MidpointRounding.AwayFromZero);
    }

    private static double Invested(double notes)
    {
        return notes + Round(Oc * notes / (1.0 - Oc), 2);
    }

    private static IReadOnlyList<RevolvingClass> Classes(double total)
    {
        return new[]
        {
            new RevolvingClass { Key = "A", Balance = total * 0.85, Coupon = 4.8 },
            new RevolvingClass { Key = "B", Balance = total * 0.1, Coupon = 5.4 },
            new RevolvingClass { Key = "C", Balance = total * 0.05, Coupon = 6.0 }
        };
    }

    private static IReadOnlyList<OwnerTrustTier> Tiers => new[]
    {
        new OwnerTrustTier("A", new[] { "A" }), new OwnerTrustTier("B", new[] { "B" }),
        new OwnerTrustTier("C", new[] { "C" })
    };

    private static IReadOnlyList<OwnerTrustStep> Steps => new[]
    {
        new OwnerTrustStep(OwnerTrustVerb.PayFees), new OwnerTrustStep(OwnerTrustVerb.PayServicing),
        new OwnerTrustStep(OwnerTrustVerb.PayInterest, "A"), new OwnerTrustStep(OwnerTrustVerb.PriorityPrincipal, "A"),
        new OwnerTrustStep(OwnerTrustVerb.PayInterest, "B"), new OwnerTrustStep(OwnerTrustVerb.PriorityPrincipal, "B"),
        new OwnerTrustStep(OwnerTrustVerb.PayInterest, "C"), new OwnerTrustStep(OwnerTrustVerb.PriorityPrincipal, "C"),
        new OwnerTrustStep(OwnerTrustVerb.FundReserve), new OwnerTrustStep(OwnerTrustVerb.RegularPrincipal),
        new OwnerTrustStep(OwnerTrustVerb.PayCertificate)
    };

    private static MasterTrustSeries Series(string name, double rate, double notes, string group = "TERM") => new()
    {
        Name = name, DiscountRate = rate, OcPct = Oc, Classes = Classes(notes), Tiers = Tiers, Steps = Steps,
        ConcentrationGroup = group, RequiredReserve = notes * 0.01, MonthlyFees = 1_250.0,
        State = new MasterTrustSeriesState { Reserve = notes * 0.01 }
    };

    private static InstallmentPoolRun Pool(int periods, double writeOff = 0.0,
        IReadOnlyList<InstallmentActual>? actuals = null) => new()
    {
        StatementDate = Statement,
        Periods = periods,
        Cohorts = new[] { new InstallmentCohort { Name = "opening", Age = 6, Schedule = Level(Term) } },
        Drivers = new InstallmentDrivers
        {
            Prepayment = new InstallmentCurve(new[] { 0.0 }), WriteOff = new InstallmentCurve(new[] { writeOff })
        },
        ActualPeriods = actuals ?? Array.Empty<InstallmentActual>()
    };

    private static double[] Level(int n) => Enumerable.Repeat(Payment, n).ToArray();

    /// <summary>Two series of 10m and 6m of notes at 6% and 9%, on a 30m pool.</summary>
    private static MasterTrustRun Trust(int periods = 1, double writeOff = 0.0, MasterTrustConfig? config = null,
        IReadOnlyList<MasterTrustActual>? actuals = null, IReadOnlyList<InstallmentActual>? poolActuals = null,
        double concentration = 0.0)
    {
        return new MasterTrustRun
        {
            Pool = Pool(periods, writeOff, poolActuals),
            Trust = config ?? new MasterTrustConfig
            {
                Series = new[] { Series("one", RateA, 10_000_000.0), Series("two", RateB, 6_000_000.0) },
                ServicingRate = 0.0075
            },
            State = new MasterTrustState { Concentration = new Dictionary<string, double> { ["TERM"] = concentration } },
            ActualPeriods = actuals ?? Array.Empty<MasterTrustActual>()
        };
    }

    private static MasterTrustResult Run(MasterTrustRun run)
    {
        return MasterTrustEngine.Run(run, new ConstantRateProvider(4.0));
    }

    private static MasterTrustSeriesPeriod Row(MasterTrustResult result, int period, string name)
    {
        return result.Periods[period].Series.First(s => s.Name == name);
    }

    [Fact]
    public void EachSeriesValuesTheWholePoolAtItsOwnRate()
    {
        var result = Run(Trust());
        var one = Row(result, 0, "one");
        var two = Row(result, 0, "two");

        one.PresentValueStart.Should().BeApproximately(Annuity(RateA, Term), 1e-6);
        two.PresentValueStart.Should().BeApproximately(Annuity(RateB, Term), 1e-6);
        one.PresentValueEnd.Should().BeApproximately(Annuity(RateA, Term - 1), 1e-6);
        two.PresentValueEnd.Should().BeApproximately(Annuity(RateB, Term - 1), 1e-6);
    }

    [Fact]
    public void DiscountedInvestedIsInvestedTimesTheRatioRoundedToFivePlaces()
    {
        var one = Row(Run(Trust()), 0, "one");
        var invested = Invested(10_000_000.0);
        var ratioStart = Round(30_000_000.0 / Annuity(RateA, Term), 5);
        var ratioEnd = Round(29_000_000.0 / Annuity(RateA, Term - 1), 5);

        one.InvestedStart.Should().Be(invested);
        one.TargetOcStart.Should().Be(Round(Oc * 10_000_000.0 / (1.0 - Oc), 2));
        one.DiscountRatioStart.Should().Be(ratioStart);
        one.DiscountRatioEnd.Should().Be(ratioEnd);
        one.DiscountedInvestedStart.Should().Be(Round(invested * ratioStart, 2));
        one.DiscountedInvestedEnd.Should().Be(Round(invested * ratioEnd, 2));
    }

    [Fact]
    public void TheRatioRoundsHalfAwayFromZeroAsPrinted()
    {
        MasterTrustEngine.Round(1.234565, 5).Should().Be(1.23457);
        MasterTrustEngine.Round(-1.234565, 5).Should().Be(-1.23457);
        MasterTrustEngine.Cents(0.125).Should().Be(0.13);
    }

    [Fact]
    public void TheIncrementalAmountIsTheConcentrationTimesTheRoundedSeriesShare()
    {
        var result = Run(Trust(concentration: 1_234_567.89));
        var one = Row(result, 0, "one");
        var two = Row(result, 0, "two");
        // The pool is larger than the discounted amounts, so it is the share's denominator.
        var share = Round(one.DiscountedInvestedStart / 30_000_000.0, 4);

        one.SeriesShareStart.Should().Be(share);
        one.IncrementalStart.Should().Be(Round(1_234_567.89 * share, 2));
        one.AdjustedInvestedStart.Should().Be(one.DiscountedInvestedStart + one.IncrementalStart);
        two.SeriesShareEnd.Should().Be(Round(two.DiscountedInvestedEnd / 29_000_000.0, 4));
    }

    [Fact]
    public void TheShareDenominatorIsTheSumOfDiscountedAmountsWhenItExceedsThePool()
    {
        var config = new MasterTrustConfig
        {
            Series = new[] { Series("one", RateA, 18_000_000.0), Series("two", RateB, 12_000_000.0) }
        };
        var result = Run(Trust(config: config, concentration: 1_000_000.0));
        var one = Row(result, 0, "one");
        var two = Row(result, 0, "two");
        var sum = one.DiscountedInvestedStart + two.DiscountedInvestedStart;

        sum.Should().BeGreaterThan(30_000_000.0);
        one.SeriesShareStart.Should().Be(Round(one.DiscountedInvestedStart / sum, 4));
    }

    [Fact]
    public void WithoutStatedDatesTheOpeningHoldsEveryDayButTheLast()
    {
        var result = Run(Trust());
        var p = result.Periods[0];
        var one = Row(result, 0, "one");

        p.Fads.Select(f => f.Days).Should().Equal(30, 1);
        p.TimeWeightedPool.Should().Be(Round((30 * 30_000_000.0 + 29_000_000.0) / 31.0, 2));
        one.AllocationNumerator.Should().Be(Round((30 * one.AdjustedInvestedStart + one.AdjustedInvestedEnd) / 31.0, 2));
    }

    [Fact]
    public void TheAllocationIsTheNumeratorOverTheGreaterOfAllNumeratorsAndTheTimeWeightedPool()
    {
        var result = Run(Trust());
        var p = result.Periods[0];
        var one = Row(result, 0, "one");
        var two = Row(result, 0, "two");
        var denominator = Math.Max(one.AllocationNumerator + two.AllocationNumerator, p.TimeWeightedPool);

        p.AllocationDenominator.Should().Be(denominator);
        one.AllocationPct.Should().Be(Round(one.AllocationNumerator / denominator, 7));
        p.TransferorPct.Should().BeApproximately(1.0 - one.AllocationPct - two.AllocationPct, 1e-12);
        p.TransferorAllocation.Should().Be(Round(p.TransferorPct * p.TotalFunds, 2));
    }

    [Fact]
    public void FundsServicingAndTheAllocatedPoolBalanceAreTheSeriesShareOfTheTrust()
    {
        var result = Run(Trust());
        var p = result.Periods[0];
        var one = Row(result, 0, "one");

        p.TotalFunds.Should().BeApproximately(Payment, 1e-6);
        one.Funds.Should().Be(Round(one.AllocationPct * Payment, 2));
        one.ServicingFee.Should().Be(Round(one.AllocationPct * 0.0075 / 12.0 * 30_000_000.0, 2));
        one.AllocatedPoolBalance.Should().Be(Round(one.AllocationPct * Annuity(RateA, Term - 1), 2));
    }

    [Fact]
    public void StatedFinancingAdjustmentDatesAreWeightedByTheDaysEachHolds()
    {
        var fads = new[]
        {
            new MasterTrustFad { Date = new DateTime(2026, 8, 1), PoolBalance = 30_000_000.0 },
            new MasterTrustFad
            {
                Date = new DateTime(2026, 8, 20), PoolBalance = 31_000_000.0,
                Concentration = new Dictionary<string, double> { ["TERM"] = 500_000.0 }
            },
            new MasterTrustFad { Date = new DateTime(2026, 8, 31), PoolBalance = 29_000_000.0 }
        };
        var result = Run(Trust(actuals: new[] { new MasterTrustActual { Fads = fads } },
            poolActuals: new[] { PrintedMonth() }));
        var p = result.Periods[0];
        var one = Row(result, 0, "one");
        var two = Row(result, 0, "two");

        p.Fads.Select(f => f.Days).Should().Equal(19, 11, 1);
        p.TimeWeightedPool.Should().Be(Round((19 * 30e6 + 11 * 31e6 + 29e6) / 31.0, 2));

        // Between the month's ends the pool is valued at the closing discount ratio.
        double Disc(MasterTrustSeriesPeriod s) => Round(s.InvestedEnd * s.DiscountRatioEnd, 2);
        var share = Round(Disc(one) / Math.Max(31e6, Disc(one) + Disc(two)), 4);
        var middle = Disc(one) + Round(500_000.0 * share, 2);
        one.AllocationNumerator.Should().Be(Round(
            (19 * one.AdjustedInvestedStart + 11 * middle + one.AdjustedInvestedEnd) / 31.0, 2));
    }

    private static InstallmentActual PrintedMonth(double scheduled = Payment, double writeOffs = 0.0,
        double prepaid = 0.0) => new()
    {
        ScheduledCollections = scheduled, PrepaidCollections = prepaid, WriteOffs = writeOffs
    };

    [Fact]
    public void ARevolvingSeriesPaysFeesServicingAndInterestAndTheRestIsClassR()
    {
        var result = Run(Trust());
        var one = Row(result, 0, "one");

        one.Fees.Should().Be(1_250.0);
        one.InterestPaid.Should().BeApproximately(
            Round(8_500_000.0 * 0.048 / 12, 2) + Round(1_000_000.0 * 0.054 / 12, 2) + Round(500_000.0 * 0.06 / 12, 2),
            1e-6);
        one.PrincipalPaid.Should().Be(0.0);
        one.ClassR.Should().BeApproximately(one.Funds - one.Fees - one.ServicingFee - one.InterestPaid, 1e-6);
        one.Reserve.Should().Be(100_000.0);
        one.Phase.Should().Be(SeriesPhase.Revolving);
    }

    [Fact]
    public void WhileRevolvingPriorityPrincipalGoesToThePrincipalFundingAccount()
    {
        // A small pool: the allocated pool balance is below the notes, tier by tier.
        var run = Trust() with
        {
            Pool = Pool(1) with
            {
                Cohorts = new[] { new InstallmentCohort { Name = "small", Age = 0, Schedule = Level(Term, 400_000.0) } }
            }
        };
        var result = Run(run);
        var one = Row(result, 0, "one");
        var shortA = Math.Max(0.0, Round(8_500_000.0 - one.AllocatedPoolBalance, 2));

        // The funds left after fees, servicing and Class A interest, and the reserve, which covers the step.
        var available = one.Funds - one.Fees - one.ServicingFee - Round(8_500_000.0 * 0.048 / 12, 2) + 100_000.0;
        one.PriorityPrincipal[0].Should().BeApproximately(Math.Min(shortA, available), 1e-6);
        one.ReserveDraw.Should().BeApproximately(Math.Min(100_000.0, Math.Max(0.0, shortA - (available - 100_000.0))), 1e-6);
        one.PfaDeposit.Should().BeApproximately(one.PriorityPrincipal.Sum(), 1e-6);
        one.Pfa.Should().BeApproximately(one.PfaDeposit, 1e-6);
        one.PrincipalPaid.Should().Be(0.0);
        one.NotesEnd.Should().Be(10_000_000.0);
    }

    private static double[] Level(int n, double payment) => Enumerable.Repeat(payment, n).ToArray();

    [Fact]
    public void WhileRevolvingRegularPrincipalDepositsTheSeriesShareOfAPoolBalanceDeficit()
    {
        var actuals = new[] { new MasterTrustActual { PoolBalanceDeficit = 200_000.0 } };
        var result = Run(Trust(actuals: actuals, poolActuals: new[] { PrintedMonth() }));
        var one = Row(result, 0, "one");

        one.RegularPrincipal.Should().Be(Round(one.AllocationPct * 200_000.0, 2));
        one.PfaDeposit.Should().Be(one.RegularPrincipal);
        result.Periods[0].TransferorPct.Should().Be(0.0);
    }

    [Fact]
    public void ARedemptionOnAPaymentDatePaysTheNotesAtParAndTheSeriesLeaves()
    {
        var config = new MasterTrustConfig
        {
            Series = new[]
            {
                Series("one", RateA, 10_000_000.0) with { RedemptionDate = new DateTime(2026, 9, 21) },
                Series("two", RateB, 6_000_000.0)
            }
        };
        var result = Run(Trust(periods: 2, config: config));
        var one = Row(result, 0, "one");

        one.RedemptionDeposit.Should().Be(10_000_000.0);
        one.ReserveDraw.Should().Be(100_000.0);
        one.RegularPrincipal.Should().Be(10_000_000.0);
        one.NotesEnd.Should().Be(0.0);
        one.InvestedEnd.Should().Be(0.0);
        one.ClassR.Should().BeApproximately(
            one.Funds + 100_000.0 - one.Fees - one.ServicingFee - one.InterestPaid, 1e-6);
        result.Periods[1].Series.Select(s => s.Name).Should().Equal("two");
        result.ClosingSeries.First(s => s.Name == "one").Ended.Should().Be("REDEEMED");
        result.PhasePath.Should().ContainSingle(m => m.Series == "one" && m.Event == "REDEEMED");
    }

    [Fact]
    public void ARedemptionBetweenPaymentDatesLeavesBeforeTheNextOne()
    {
        var config = new MasterTrustConfig
        {
            Series = new[]
            {
                Series("one", RateA, 10_000_000.0) with { RedemptionDate = new DateTime(2026, 9, 25) },
                Series("two", RateB, 6_000_000.0)
            }
        };
        var result = Run(Trust(periods: 2, config: config));

        result.Periods[0].Series.Select(s => s.Name).Should().Equal("one", "two");
        Row(result, 0, "one").PrincipalPaid.Should().Be(0.0);
        result.Periods[1].Series.Select(s => s.Name).Should().Equal("two");
        var two = Row(result, 1, "two");
        result.Periods[1].SumNumerators.Should().Be(two.AllocationNumerator);
    }

    [Fact]
    public void APoolTestTripsEverySeriesOnThePaymentDateAndFreezesTheNumerator()
    {
        var config = new MasterTrustConfig
        {
            Series = new[] { Series("one", RateA, 10_000_000.0), Series("two", RateB, 6_000_000.0) },
            Tests = new OwnerTrustTests { WriteOffLimit = 0.10 }
        };
        var run = Trust(periods: 3, writeOff: 0.02, config: config) with
        {
            State = new MasterTrustState
            {
                WriteOffHistory = new[] { 0.01, 0.01 },
                Concentration = new Dictionary<string, double> { ["TERM"] = 0.0 }
            }
        };
        var result = Run(run);

        // (1% + 1% + 2%) × 4 = 16% against 10%: the event acts on the first payment date.
        var test = result.Periods[0].Tests.First(t => t.Name == "WRITE_OFF_RATIO");
        test.Value.Should().BeApproximately(0.16, 1e-12);
        test.Breached.Should().BeTrue();
        foreach (var name in new[] { "one", "two" })
        {
            Row(result, 0, name).Phase.Should().Be(SeriesPhase.EarlyAmortization);
            Row(result, 0, name).Event.Should().Be("WRITE_OFF_RATIO");
        }

        var one = Row(result, 0, "one");
        one.RegularPrincipal.Should().BeApproximately(one.Funds - one.Fees - one.ServicingFee - one.InterestPaid, 1e-6);
        Row(result, 1, "one").AllocationNumerator.Should().Be(one.AllocationNumerator);
        Row(result, 2, "one").AllocationNumerator.Should().Be(one.AllocationNumerator);
        // The OC target is set on the notes as of the last day of revolving.
        Row(result, 1, "one").TargetOcStart.Should().Be(Round(Oc * 10_000_000.0 / (1.0 - Oc), 2));
    }

    [Fact]
    public void OnceEverySeriesAmortizesTheTrustBuysNothing()
    {
        var config = new MasterTrustConfig
        {
            Series = new[] { Series("one", RateA, 10_000_000.0) },
            Tests = new OwnerTrustTests { WriteOffLimit = 0.10, WriteOffWindow = 1, WriteOffMultiplier = 12.0 },
            Acquisition = new MasterTrustAcquisition
            {
                Price = new InstallmentPrice { PerFace = 0.9 }, Terms = new[] { new InstallmentTerm(24, 1.0) },
                HoldPoolFace = true
            }
        };
        var result = Run(Trust(periods: 3, writeOff: 0.01, config: config));

        result.Periods[0].AdditionsFace.Should().BeGreaterThan(0.0);
        Row(result, 0, "one").Phase.Should().Be(SeriesPhase.EarlyAmortization);
        result.Periods[1].AdditionsFace.Should().Be(0.0);
    }

    [Fact]
    public void AnInterestShortfallActsFromTheNextPaymentDate()
    {
        var series = Series("one", RateA, 10_000_000.0) with
        {
            Classes = new[]
            {
                new RevolvingClass { Key = "A", Balance = 8_500_000.0, Coupon = 400.0 },
                new RevolvingClass { Key = "B", Balance = 1_000_000.0, Coupon = 5.4 },
                new RevolvingClass { Key = "C", Balance = 500_000.0, Coupon = 6.0 }
            },
            Tests = new MasterTrustSeriesTests { InterestShortfall = true },
            State = new MasterTrustSeriesState()
        };
        var result = Run(Trust(periods: 2, config: new MasterTrustConfig { Series = new[] { series } }));

        Row(result, 0, "one").Phase.Should().Be(SeriesPhase.Revolving);
        Row(result, 0, "one").Tests.Should().Contain(t => t.Name == "INTEREST_SHORTFALL" && t.Breached);
        Row(result, 1, "one").Phase.Should().Be(SeriesPhase.EarlyAmortization);
        Row(result, 1, "one").Event.Should().Be("INTEREST_SHORTFALL");
    }

    [Fact]
    public void APrivateSeriesTakesItsShareAndRunsNoWaterfall()
    {
        var config = new MasterTrustConfig
        {
            Series = new[]
            {
                Series("one", RateA, 10_000_000.0),
                new MasterTrustSeries { Name = "row", DiscountRate = RateB, OcPct = 0.0875, Private = true, PrivateNotes = 5e6 }
            }
        };
        var actuals = new[]
        {
            new MasterTrustActual
            {
                Series = new Dictionary<string, MasterTrustSeriesActual>
                {
                    ["row"] = new() { NoteBalance = 7e6 }
                }
            }
        };
        var result = Run(Trust(config: config, actuals: actuals, poolActuals: new[] { PrintedMonth() }));
        var row = Row(result, 0, "row");

        row.Private.Should().BeTrue();
        row.NotesStart.Should().Be(5e6);
        row.InvestedStart.Should().Be(5e6 + Round(0.0875 * 5e6 / (1 - 0.0875), 2));
        row.InvestedEnd.Should().Be(7e6 + Round(0.0875 * 7e6 / (1 - 0.0875), 2));
        row.Funds.Should().Be(Round(row.AllocationPct * Payment, 2));
        row.Classes.Should().BeEmpty();
        row.ClassR.Should().Be(0.0);
        result.Periods[0].SumNumerators.Should().Be(row.AllocationNumerator + Row(result, 0, "one").AllocationNumerator);
    }

    [Fact]
    public void ANewSeriesJoinsInItsClosingMonthOnItsClosingAmount()
    {
        var config = new MasterTrustConfig
        {
            Series = new[]
            {
                Series("one", RateA, 10_000_000.0),
                Series("new", RateB, 6_000_000.0) with
                {
                    ClosingDate = new DateTime(2026, 8, 25),
                    Classes = new[]
                    {
                        new RevolvingClass { Key = "A", Balance = 5_100_000.0, Coupon = 4.8 },
                        new RevolvingClass { Key = "B", Balance = 600_000.0, Index = MarketDataInstEnum.Sofr30Avg,
                            Margin = 0.5, Basis = AccrualBasis.Actual360 },
                        new RevolvingClass { Key = "C", Balance = 300_000.0, Coupon = 6.0 }
                    }
                }
            }
        };
        var result = Run(Trust(periods: 2, config: config));
        var fresh = Row(result, 0, "new");

        fresh.InvestedStart.Should().Be(0.0);
        fresh.AdjustedInvestedStart.Should().Be(0.0);
        fresh.AllocationNumerator.Should().Be(fresh.AdjustedInvestedEnd);
        // 30/360 from the closing to the payment day as scheduled; Actual/360 to the day it was paid.
        var a = fresh.Classes.First(c => c.Key == "A");
        a.AccrualDays.Should().Be(25);
        a.InterestAccrued.Should().Be(Round(5_100_000.0 * 0.048 * 25 / 360.0, 2));
        fresh.Classes.First(c => c.Key == "B").AccrualDays.Should().Be((new DateTime(2026, 9, 21) - new DateTime(2026, 8, 25)).Days);
        Row(result, 1, "new").InvestedStart.Should().Be(Invested(6_000_000.0));
        Row(result, 1, "new").Classes.First(c => c.Key == "A").AccrualDays.Should().Be(30);
    }

    [Fact]
    public void PrintedPresentValuesAndNumeratorsAreUsedAndTheRunsOwnAreReported()
    {
        var actuals = new[]
        {
            new MasterTrustActual
            {
                Series = new Dictionary<string, MasterTrustSeriesActual>
                {
                    ["one"] = new()
                    {
                        PresentValueStart = 27_000_000.0, PresentValueEnd = 26_000_000.0, AllocationNumerator = 11e6
                    }
                }
            }
        };
        var result = Run(Trust(actuals: actuals, poolActuals: new[] { PrintedMonth() }));
        var one = Row(result, 0, "one");

        one.DiscountRatioStart.Should().Be(Round(30e6 / 27e6, 5));
        one.DiscountRatioEnd.Should().Be(Round(29e6 / 26e6, 5));
        one.ComputedPresentValueEnd.Should().BeApproximately(Annuity(RateA, Term - 1), 1e-6);
        one.AllocationNumerator.Should().Be(11e6);
        one.ComputedAllocationNumerator.Should().Be(
            Round((30 * one.AdjustedInvestedStart + one.AdjustedInvestedEnd) / 31.0, 2));
        one.AllocatedPoolBalance.Should().Be(Round(one.AllocationPct * 26e6, 2));
        Row(result, 0, "two").ComputedPresentValueEnd.Should().BeNull();
    }

    [Fact]
    public void APrintedRateAndAPrintedCouponHoldForLaterMonths()
    {
        var actuals = new[]
        {
            new MasterTrustActual
            {
                Series = new Dictionary<string, MasterTrustSeriesActual>
                {
                    ["one"] = new() { DiscountRate = 0.07, Coupons = new Dictionary<string, double> { ["B"] = 4.5 } }
                }
            },
            new MasterTrustActual()
        };
        var result = Run(Trust(periods: 3, actuals: actuals, poolActuals: new[] { PrintedMonth(), PrintedMonth() }));

        for (var t = 0; t < 3; t++)
        {
            Row(result, t, "one").DiscountRate.Should().Be(0.07);
            Row(result, t, "one").Classes.First(c => c.Key == "B").CouponPct.Should().Be(4.5);
        }

        Row(result, 0, "one").PresentValueStart.Should().BeApproximately(Annuity(0.07, Term), 1e-6);
    }

    [Fact]
    public void HoldingThePoolFaceBuysBackToTheOpeningFacePayingCashUpToWhatTheCertificatesWereLeft()
    {
        var config = new MasterTrustConfig
        {
            Series = new[] { Series("one", RateA, 10_000_000.0) },
            Acquisition = new MasterTrustAcquisition
            {
                Price = new InstallmentPrice { PerFace = 0.9 }, Terms = new[] { new InstallmentTerm(24, 1.0) },
                HoldPoolFace = true
            }
        };
        var run = Trust(periods: 2, config: config) with
        {
            State = new MasterTrustState { CertificateCash = 300_000.0 }
        };
        var result = Run(run);
        var p = result.Periods[0];

        p.AdditionsFace.Should().BeApproximately(Payment, 1e-6);
        p.EndingPool.Should().BeApproximately(30_000_000.0, 1e-6);
        p.AdditionsCash.Should().BeApproximately(300_000.0, 1e-6);
        p.CertificateFundedFace.Should().BeApproximately(Payment - 300_000.0 / 0.9, 1e-6);
        // The next month buys with what this payment date left the certificates.
        var next = result.Periods[1];
        next.AdditionsCash.Should().BeApproximately(Math.Min(p.CertificateCash, 0.9 * next.AdditionsFace), 1e-6);
        next.EndingPool.Should().BeApproximately(30_000_000.0, 1e-6);
    }

    [Fact]
    public void BuyingWithCashSpendsItAllAtThePrice()
    {
        var config = new MasterTrustConfig
        {
            Series = new[] { Series("one", RateA, 10_000_000.0) },
            Acquisition = new MasterTrustAcquisition
            {
                Price = new InstallmentPrice { PerFace = 0.8 }, Terms = new[] { new InstallmentTerm(12, 1.0) }
            }
        };
        var result = Run(Trust(periods: 1, config: config) with { State = new MasterTrustState { CertificateCash = 400_000.0 } });

        result.Periods[0].AdditionsFace.Should().BeApproximately(500_000.0, 1e-6);
        result.Periods[0].CertificateFundedFace.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void Days360FollowsTheUsConvention()
    {
        MasterTrustEngine.Days360(new DateTime(2026, 9, 22), new DateTime(2026, 10, 20)).Should().Be(28);
        MasterTrustEngine.Days360(new DateTime(2025, 11, 25), new DateTime(2025, 12, 20)).Should().Be(25);
        MasterTrustEngine.Days360(new DateTime(2025, 3, 31), new DateTime(2025, 4, 20)).Should().Be(20);
        MasterTrustEngine.Days360(new DateTime(2026, 1, 30), new DateTime(2026, 3, 31)).Should().Be(60);
    }

    [Theory]
    [InlineData("verb")]
    [InlineData("privateWithClasses")]
    [InlineData("duplicate")]
    [InlineData("closing")]
    [InlineData("unknownActual")]
    [InlineData("rounding")]
    [InlineData("fadOrder")]
    [InlineData("lastStep")]
    public void RefusesByName(string what)
    {
        var one = Series("one", RateA, 10_000_000.0);
        var run = Trust();
        run = what switch
        {
            "verb" => run with
            {
                Trust = run.Trust with
                {
                    Series = new[] { one with { Steps = Steps.Prepend(new OwnerTrustStep(OwnerTrustVerb.DepositAcquisition)).ToList() } }
                }
            },
            "privateWithClasses" => run with { Trust = run.Trust with { Series = new[] { one with { Private = true } } } },
            "duplicate" => run with { Trust = run.Trust with { Series = new[] { one, one } } },
            "closing" => run with { Trust = run.Trust with { Series = new[] { one with { ClosingDate = Statement } } } },
            "unknownActual" => run with
            {
                Pool = Pool(1, actuals: new[] { PrintedMonth() }),
                ActualPeriods = new[]
                {
                    new MasterTrustActual
                    {
                        Series = new Dictionary<string, MasterTrustSeriesActual> { ["nobody"] = new() }
                    }
                }
            },
            "rounding" => run with { Trust = run.Trust with { Rounding = new MasterTrustRounding { Allocation = 20 } } },
            "fadOrder" => run with
            {
                Pool = Pool(1, actuals: new[] { PrintedMonth() }),
                ActualPeriods = new[]
                {
                    new MasterTrustActual
                    {
                        Fads = new[]
                        {
                            new MasterTrustFad { Date = new DateTime(2026, 8, 31), PoolBalance = 1.0 },
                            new MasterTrustFad { Date = new DateTime(2026, 8, 1), PoolBalance = 1.0 }
                        }
                    }
                }
            },
            "lastStep" => run with
            {
                Trust = run.Trust with { Series = new[] { one with { Steps = Steps.Take(Steps.Count - 1).ToList() } } }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(what))
        };

        var act = () => Run(run);
        act.Should().Throw<RevolvingConfigException>();
    }
}
