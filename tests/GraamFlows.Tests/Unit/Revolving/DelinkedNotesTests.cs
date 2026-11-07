using FluentAssertions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Revolving;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>
///     De-linked notes. The first tests reproduce printed Chase Issuance Trust figures: the Monthly
///     Noteholders' Statements paying on Oct 15, 2026, Jul 15, 2026, Jun 15, 2020 and Jul 15, 2020.
/// </summary>
public class DelinkedNotesTests
{
    private const double FiveCents = 0.05;

    private static readonly DateTime Aug2026 = new(2026, 8, 31);
    private static readonly DateTime Apr2020 = new(2020, 4, 30);

    private static readonly ActualPeriod Sep2026 = NoteProgram.Month(11_836_296_022.69, 11_651_155_677.84,
        6_050_645_878.59, 235_565_989.25, 19_447_524.67, 4_492_360.11, 30);

    private static readonly ActualPeriod May2020 = NoteProgram.Month(11_840_852_443.04, 11_831_256_772.15,
        3_764_595_275.67, 188_838_246.34, 26_995_378.97, 3_896_482.33, 31);

    private static RevolvingSeries[] Sep2026Tranches() => new[]
    {
        NoteProgram.Tranche("A(2023-2)", "A", 850_000_000.0, 5.08),
        NoteProgram.Tranche("A(2024-1)", "A", 1_250_000_000.0, 4.60),
        NoteProgram.Tranche("A(2024-2)", "A", 1_100_000_000.0, 4.63),
        NoteProgram.Tranche("A(2025-1)", "A", 1_500_000_000.0, 4.16),
        NoteProgram.Tranche("A(2026-1)", "A", 1_250_000_000.0, 4.40),
        NoteProgram.Tranche("B(2025-1)", "B", 750_000_000.0, 4.91),
        NoteProgram.Tranche("C(2025-1)", "C", 750_000_000.0, 5.20)
    };

    private static RevolvingResult RunSep2026() =>
        NoteProgram.Execute(NoteProgram.Program(), Aug2026, new[] { Sep2026 }, Sep2026Tranches());

    private static RevolvingSeries Accumulator(double funding, double priorNumerator, double priorFunding) =>
        NoteProgram.Accumulating(NoteProgram.Tranche("A(2016-4)", "A", 850_000_000.0, 1.49), 850_000_000.0, funding,
            priorNumerator, priorFunding, new[] { 212_500_000.0 }, new DateTime(2020, 3, 1), new DateTime(2020, 7, 15));

    private static RevolvingSeries[] May2020Tranches(RevolvingSeries accumulator) => new[]
    {
        accumulator,
        NoteProgram.Tranche("rest of A", "A", 4_950_000_000.0, 1.21),
        NoteProgram.Tranche("B(2017-1)", "B", 750_000_000.0, 0.8),
        NoteProgram.Tranche("C(2017-1)", "C", 750_000_000.0, 1.05)
    };

    private static NotePeriod Notes(RevolvingResult result, int period = 0) => result.Periods[period].Notes!;

    private static SeriesResult Named(RevolvingResult result, string name) =>
        result.Series.Single(s => s.Name == name);

    private static TranchePeriod Row(RevolvingResult result, string name, int period = 0) =>
        Named(result, name).Periods[period].Tranche!;

    [Fact]
    public void FinanceTheFeeAndDefaultsTieOnTheNotesAsAWhole()
    {
        var notes = Notes(RunSep2026());

        notes.Numerator.Should().Be(7_450_000_000.0);
        notes.FinanceAllocation.Should().BeApproximately(148_269_916.24, FiveCents);
        notes.InvestorDefault.Should().BeApproximately(9_413_077.85, FiveCents);
        notes.FeesPaid.Should().BeApproximately(9_312_500.00, FiveCents);
        notes.InterestPaid.Should().BeApproximately(28_736_250.00, FiveCents);
        notes.PrincipalAllocation.Should().BeApproximately(3_808_396_791.46, FiveCents);
    }

    [Fact]
    public void TheEngineExcessIsThePrintedAmountLessTheAccountEarningsItWasNotGiven()
    {
        // Printed $100,924,434.44. The gap is the earnings on cash the accounts held for a day.
        Notes(RunSep2026()).ExcessDollars.Should().BeApproximately(100_808_088.39, FiveCents);
    }

    [Fact]
    public void AccountEarningsSuppliedAreFinanceAndLandInTheExcess()
    {
        var month = Sep2026 with { PrincipalAccountEarnings = 116_346.05 };
        var result = NoteProgram.Execute(NoteProgram.Program(), Aug2026, new[] { month }, Sep2026Tranches());

        Notes(result).ExcessDollars.Should().BeApproximately(100_924_434.44, FiveCents);
    }

    [Fact]
    public void PrintedRatesComeFromTheDollars()
    {
        var notes = Notes(RunSep2026());

        // The statement rounds each line, then subtracts, so 22.69 is 24.21 less 1.52.
        Math.Round(notes.FinanceYieldPct, 2).Should().Be(24.21);
        Math.Round(notes.NetLossPct, 2).Should().Be(1.52);
        Math.Round(notes.CouponPct, 2).Should().Be(4.63);
        Math.Round(notes.BaseRatePct, 2).Should().Be(6.13);
        notes.ExcessSpreadPct.Should().BeApproximately(16.57, 0.01);
    }

    [Fact]
    public void RequiredSubordinationIsAPercentOfAdjustedAndClassBHoldsForWhatClassALeaves()
    {
        var result = RunSep2026();

        Row(result, "A(2024-1)").RequiredSubordination["B"].Should().BeApproximately(101_744_125.00, FiveCents);
        Row(result, "A(2024-1)").RequiredSubordination["C"].Should().BeApproximately(101_744_125.00, FiveCents);
        result.Series.Where(s => s.Name.StartsWith("A"))
            .Sum(s => s.Periods[0].Tranche!.RequiredSubordination["C"])
            .Should().BeApproximately(484_302_035.00, FiveCents);
        Row(result, "B(2025-1)").RequiredSubordination["C"].Should().BeApproximately(504_300_801.99, 1.0);
    }

    [Fact]
    public void NothingIsUsedInAMonthThatCoversItsLosses()
    {
        var result = RunSep2026();

        result.Series.SelectMany(s => s.Periods[0].Tranche!.Usage.Values).Sum().Should().Be(0.0);
        Notes(result).UncoveredDefault.Should().Be(0.0);
    }

    [Fact]
    public void FinanceIsAllocatedOnTheCloseOfTheMonthBeforeNotTheStatementsOpeningAmount()
    {
        // Paying Jun 15, 2020. The statement opens on $6,875,000,000, and the numerator is April's
        // close: $7,087,500,000, printed as 59.86% and $113,031,648.47.
        var result = NoteProgram.Execute(NoteProgram.Program(), Apr2020, new[] { May2020 },
            May2020Tranches(Accumulator(425_000_000.0, 637_500_000.0, 212_500_000.0)));

        var notes = Notes(result);
        notes.Numerator.Should().Be(7_087_500_000.0);
        notes.FinanceAllocation.Should().BeApproximately(113_031_648.47, FiveCents);
        notes.InvestorDefault.Should().BeApproximately(13_826_152.36, FiveCents);
        notes.FeesPaid.Should().BeApproximately(8_859_375.00, FiveCents);
        Row(result, "A(2016-4)").OpeningNominalLiquidation.Should().Be(425_000_000.0);
    }

    [Fact]
    public void AFrozenNumeratorOverTheLivePoolReproducesThePrintedPrincipal()
    {
        var result = NoteProgram.Execute(NoteProgram.Program(), Apr2020, new[] { May2020 },
            May2020Tranches(Accumulator(425_000_000.0, 637_500_000.0, 212_500_000.0)));

        Notes(result).PrincipalNumerator.Should().Be(7_300_000_000.0);
        Notes(result).PrincipalAllocation.Should().BeApproximately(2_320_909_380.86, FiveCents);
    }

    [Fact]
    public void AFrozenPercentOverTheSameMonthDoesNot()
    {
        // The percent on the last revolving day: $850,000,000 over March's $13,566,953,383.69. It is
        // a test of what the two options give, not a choice between them for any program.
        var frozen = Accumulator(425_000_000.0, 637_500_000.0, 212_500_000.0) with
        {
            FrozenNumerator = null,
            FrozenPrincipalPct = 850_000_000.0 / 13_566_953_383.69
        };
        var result = NoteProgram.Execute(NoteProgram.Program(PrincipalFreeze.FrozenPercent), Apr2020, new[] { May2020 },
            May2020Tranches(frozen));

        (2_320_909_380.86 - Notes(result).PrincipalAllocation).Should().BeApproximately(34_382_551.80, FiveCents);
    }

    [Fact]
    public void TheFundingAccountBuildsOverMonthsAndPaysOnTheExpectedDate()
    {
        var month = May2020 with { Beginning = null };
        var result = NoteProgram.Execute(NoteProgram.Program(), Apr2020,
            new[] { May2020, month, month with { } },
            May2020Tranches(Accumulator(425_000_000.0, 637_500_000.0, 212_500_000.0)));

        var rows = Named(result, "A(2016-4)").Periods;
        rows[0].FundingDeposit.Should().Be(212_500_000.0);
        rows[0].FundingAccount.Should().Be(637_500_000.0);
        rows[1].FundingDeposit.Should().Be(212_500_000.0);
        rows[1].FundingAccount.Should().Be(0.0);
        Named(result, "A(2016-4)").Classes["A"][1].PrincipalPaid.Should().Be(850_000_000.0);
        Named(result, "A(2016-4)").Classes["A"][1].EndBalance.Should().Be(0.0);
    }

    [Fact]
    public void InterestStaysOnTheOutstandingAmountWhileTheNominalAmountFalls()
    {
        var result = NoteProgram.Execute(NoteProgram.Program(), Apr2020, new[] { May2020 },
            May2020Tranches(Accumulator(425_000_000.0, 637_500_000.0, 212_500_000.0)));

        Named(result, "A(2016-4)").Classes["A"][0].InterestPaid.Should().BeApproximately(1_055_416.67, FiveCents);
    }

    [Fact]
    public void ARequiredAmountStandsOnTheAdjustedAmountAndEndsWhenTheTrancheIsPaid()
    {
        var result = NoteProgram.Execute(NoteProgram.Program(), Apr2020,
            new[] { May2020, May2020 with { Beginning = null } },
            May2020Tranches(Accumulator(425_000_000.0, 637_500_000.0, 212_500_000.0)));

        Row(result, "A(2016-4)").RequiredSubordination["B"].Should().BeApproximately(34_593_002.50, FiveCents);
        Row(result, "A(2016-4)", 1).RequiredSubordination["B"].Should().Be(0.0);
    }

    [Fact]
    public void SegregatedFinanceCoversInterestOnWhatTheAccountHeldTheMonthBefore()
    {
        // Paying Jun 15, 2020: $212,500,000 at 1.49%, from the transferor's finance.
        var result = NoteProgram.Execute(NoteProgram.Program(segregated: true), Apr2020, new[] { May2020 },
            May2020Tranches(Accumulator(425_000_000.0, 637_500_000.0, 212_500_000.0)));

        Notes(result).SegregatedFinance.Should().BeApproximately(263_854.17, FiveCents);
    }

    [Fact]
    public void ATrancheCountsFromItsIssueMonthAndOwesInterestFromItsFirstPaymentMonth()
    {
        // Class A(2026-1) closed May 28, 2026 and first paid Jul 15: $0.00 in June, 47 days in July.
        var tranche = NoteProgram.Tranche("A(2026-1)", "A", 1_250_000_000.0, 4.40) with
        {
            ClosingDate = new DateTime(2026, 5, 28),
            FirstPayDate = new DateTime(2026, 7, 15)
        };
        var month = NoteProgram.Month(11_869_600_000.0, 11_975_400_000.0, 6_000_000_000.0, 235_000_000.0,
            20_000_000.0, 4_000_000.0, 31);
        var result = NoteProgram.Execute(NoteProgram.Program(), new DateTime(2026, 4, 30),
            new[] { month, month with { Beginning = null, Ending = null } },
            new[] { tranche, NoteProgram.Tranche("rest", "A", 7_350_000_000.0, 4.7) });

        Notes(result).Numerator.Should().Be(8_600_000_000.0);
        var rows = Named(result, "A(2026-1)").Classes["A"];
        rows[0].InterestPaid.Should().Be(0.0);
        rows[1].InterestPaid.Should().BeApproximately(7_180_555.56, FiveCents);
    }

    [Fact]
    public void AClosingOnTheThirtyFirstCountsFromTheThirtieth()
    {
        // Class A(2024-1) closed Jan 31, 2024 and first paid Feb 15: printed $2,395,833.34, 15 days.
        var tranche = NoteProgram.Tranche("A(2024-1)", "A", 1_250_000_000.0, 4.60) with
        {
            ClosingDate = new DateTime(2024, 1, 31),
            FirstPayDate = new DateTime(2024, 2, 15)
        };
        var month = NoteProgram.Month(9_036_900_000.0, 9_250_400_000.0, 3_000_000_000.0, 180_000_000.0,
            15_000_000.0, 3_000_000.0, 31);
        var result = NoteProgram.Execute(NoteProgram.Program(), new DateTime(2023, 12, 31), new[] { month },
            new[] { tranche, NoteProgram.Tranche("rest", "A", 4_500_000_000.0, 4.7) });

        Named(result, "A(2024-1)").Classes["A"][0].InterestPaid.Should().BeApproximately(2_395_833.33, FiveCents);
    }

    [Fact]
    public void RecoveriesInPrincipalLeaveTheInvestorsTheNetDefault()
    {
        var result = RunSep2026();

        result.Periods[0].Pool.DefaultAmount.Should().BeApproximately(14_955_164.56, FiveCents);
    }

    [Fact]
    public void AnAverageBalanceStandsInForTheOpeningPoolInAMonthAccountsWereAdded()
    {
        var month = Sep2026 with { AllocationBalance = 12_000_000_000.0 };
        var result = NoteProgram.Execute(NoteProgram.Program(), Aug2026, new[] { month }, Sep2026Tranches());

        Named(result, "A(2024-1)").Periods[0].FloatingPct.Should()
            .BeApproximately(1_250_000_000.0 / 12_000_000_000.0 * 100.0, 1e-9);
    }

    private static RevolvingSeries[] Stack(double a = 860.0, double b = 70.0, double c = 70.0) => new[]
    {
        NoteProgram.Tranche("A", "A", a, 0.0) with { Fees = Array.Empty<RevolvingFee>() },
        NoteProgram.Tranche("B", "B", b, 0.0) with { Fees = Array.Empty<RevolvingFee>() },
        NoteProgram.Tranche("C", "C", c, 0.0) with { Fees = Array.Empty<RevolvingFee>() }
    };

    private static ActualPeriod ShortMonth(double finance, double gross, double principal = 500.0)
    {
        return NoteProgram.Month(2_000.0, 2_000.0, principal, finance, gross, 0.0, 30) with { };
    }

    private static RevolvingResult RunShort(ActualPeriod month, params RevolvingSeries[] tranches)
    {
        return NoteProgram.Execute(NoteProgram.Program(), Aug2026, new[] { month },
            tranches.Length == 0 ? Stack() : tranches);
    }

    [Fact]
    public void AnUncoveredDefaultFallsOnTheLowestClassTheSeniorsMayUse()
    {
        // Pool 2,000, notes 1,000, so half the default is the notes': 100 uncovered, no finance.
        var result = RunShort(ShortMonth(0.0, 200.0));

        Notes(result).UncoveredDefault.Should().BeApproximately(100.0, 1e-9);
        Named(result, "A").Periods[0].InvestedAmount.Should().BeApproximately(860.0, 1e-6);
        var c = Named(result, "C").Periods[0].InvestedAmount;
        var b = Named(result, "B").Periods[0].InvestedAmount;
        (70.0 - c + 70.0 - b).Should().BeApproximately(100.0, 1e-6);
        c.Should().BeLessThan(b);
    }

    [Fact]
    public void AChargeOffBeyondTheRequiredAmountsStaysOnTheTrancheItFellOn()
    {
        // 750 uncovered. Class C goes, and what Class A has unused of B caps what else can move, so
        // the rest stays on A while B keeps what A could no longer use.
        var result = RunShort(ShortMonth(0.0, 1_500.0));

        Named(result, "C").Periods[0].InvestedAmount.Should().BeApproximately(0.0, 1e-6);
        Named(result, "A").Periods[0].InvestedAmount.Should().BeLessThan(860.0);
        var row = Row(result, "A");
        row.Usage["B"].Should().BeApproximately(row.RequiredSubordination["B"], 1e-3);
        row.Usage["C"].Should().BeLessThanOrEqualTo(row.RequiredSubordination["C"] + 1e-6);
    }

    [Fact]
    public void UsageRecordsWhatASeniorTrancheSpentAndNeverPassesItsRequiredAmount()
    {
        var result = RunShort(ShortMonth(0.0, 200.0));

        var row = Row(result, "A");
        row.Usage["C"].Should().BeGreaterThan(0.0);
        row.Usage["C"].Should().BeLessThanOrEqualTo(row.RequiredSubordination["C"]);
        row.UsageThisMonth["C"].Should().Be(row.Usage["C"]);
    }

    [Fact]
    public void PrincipalPaysSeniorInterestAndAJuniorClassBearsIt()
    {
        var tranches = new[]
        {
            NoteProgram.Tranche("A", "A", 860.0, 12.0),
            NoteProgram.Tranche("B", "B", 70.0, 0.0),
            NoteProgram.Tranche("C", "C", 70.0, 0.0)
        };
        tranches = tranches.Select(t => t with { Fees = Array.Empty<RevolvingFee>() }).ToArray();
        var result = RunShort(ShortMonth(0.0, 0.0, 1_000.0), tranches);

        Named(result, "A").Classes["A"][0].InterestPaid.Should().BeApproximately(8.6, 1e-6);
        Named(result, "C").Periods[0].InvestedAmount.Should().BeApproximately(70.0 - 8.6, 1e-6);
        Named(result, "A").Periods[0].InvestedAmount.Should().BeApproximately(860.0, 1e-6);
        Notes(result).PrincipalReallocated.Should().BeApproximately(8.6, 1e-6);
    }

    [Fact]
    public void PrincipalMovedToSeniorsStopsAtWhatTheJuniorClassesWereAllocated()
    {
        var tranches = new[]
        {
            NoteProgram.Tranche("A", "A", 860.0, 12.0),
            NoteProgram.Tranche("B", "B", 70.0, 0.0),
            NoteProgram.Tranche("C", "C", 70.0, 0.0)
        }.Select(t => t with { Fees = Array.Empty<RevolvingFee>() }).ToArray();

        // Notes are half the pool, and the juniors 14% of the notes: 7 of 100 principal.
        var result = RunShort(ShortMonth(0.0, 0.0, 100.0), tranches);

        Notes(result).PrincipalReallocated.Should().BeApproximately(7.0, 1e-6);
        Named(result, "A").Classes["A"][0].InterestPaid.Should().BeApproximately(7.0, 1e-6);
    }

    [Fact]
    public void AnUnpaidTrancheGoesToEarlyAmortizationAloneAndTheOthersStayRevolving()
    {
        // B matures while it is all of A's Class B subordination, so no deposit may be made.
        var expected = new DateTime(2026, 9, 15);
        var b = NoteProgram.Tranche("B", "B", 70.0, 0.0) with
        {
            Phase = SeriesPhase.Accumulating,
            FrozenNumerator = 70.0,
            AccumulationStart = new DateTime(2026, 8, 1),
            ExpectedDate = expected,
            ControlledAmounts = new[] { 70.0 }
        };
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0,
            new[] { NoteProgram.Tranche("A", "A", 860.0, 0.0), b, NoteProgram.Tranche("C", "C", 70.0, 0.0) },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 2);

        // 8.13953% of 860 is 69.99996, so a few thousandths of a cent are free.
        Named(result, "B").Periods[0].FundingDeposit.Should().BeLessThan(0.001);
        Named(result, "B").Periods[0].FundingDeficit.Should().BeApproximately(70.0, 0.001);
        Named(result, "B").PhasePath.Should().Contain(m => m.Phase == SeriesPhase.EarlyAmortization);
        Named(result, "A").Periods[1].Phase.Should().Be(SeriesPhase.Revolving);
        Named(result, "C").Periods[1].Phase.Should().Be(SeriesPhase.Revolving);
    }

    [Fact]
    public void AJuniorTrancheCanBePaidOnceTheSeniorsNoLongerNeedIt()
    {
        // A second Class B stands behind A, so the first may be paid.
        var expected = new DateTime(2026, 9, 15);
        var b = NoteProgram.Tranche("B1", "B", 70.0, 0.0) with
        {
            Phase = SeriesPhase.Accumulating,
            FrozenNumerator = 70.0,
            AccumulationStart = new DateTime(2026, 8, 1),
            ExpectedDate = expected,
            ControlledAmounts = new[] { 70.0 }
        };
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0,
            new[]
            {
                NoteProgram.Tranche("A", "A", 860.0, 0.0), b, NoteProgram.Tranche("B2", "B", 70.0, 0.0),
                NoteProgram.Tranche("C", "C", 70.0, 0.0)
            },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 2);

        Named(result, "B1").Classes["B"][0].PrincipalPaid.Should().Be(70.0);
    }

    [Fact]
    public void ATrancheInEarlyAmortizationTargetsItsWholeAmount()
    {
        var tranche = NoteProgram.Tranche("A", "A", 860.0, 0.0) with
        {
            Phase = SeriesPhase.EarlyAmortization,
            FrozenNumerator = 860.0
        };
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0,
            new[] { tranche, NoteProgram.Tranche("B", "B", 70.0, 0.0), NoteProgram.Tranche("C", "C", 70.0, 0.0) },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);

        // Principal is pooled: the notes' half of 800, and the tranche targets all of its 860.
        Named(result, "A").Classes["A"][0].PrincipalPaid.Should().BeApproximately(400.0, 1e-6);
    }

    [Fact]
    public void ATrancheFundedBeforeItsExpectedDatePaysOnThatDate()
    {
        // $800 fills on Sep 15, a month early, so Oct 15 has no deposit to make.
        var tranche = NoteProgram.Accumulating(NoteProgram.Tranche("A", "A", 800.0, 0.0), 800.0, 700.0, 800.0, 0.0,
            new[] { 100.0 }, new DateTime(2026, 8, 1), new DateTime(2026, 10, 15));
        var result = NoteProgram.Forecast(NoteProgram.Program(), 4_000.0,
            new[] { tranche, NoteProgram.Tranche("B", "B", 70.0, 0.0), NoteProgram.Tranche("C", "C", 70.0, 0.0) },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 3);

        var rows = Named(result, "A").Classes["A"];
        rows[0].PrincipalPaid.Should().Be(0.0);
        rows[1].PrincipalPaid.Should().Be(800.0);
        Named(result, "A").PhasePath.Should().NotContain(m => m.Phase == SeriesPhase.EarlyAmortization);
        Named(result, "A").PhasePath.Should().Contain(m => m.Phase == SeriesPhase.Amortizing);
    }

    [Fact]
    public void AnAmortizingTrancheWithABalanceTargetsItsWholeAmount()
    {
        var tranche = NoteProgram.Tranche("A", "A", 860.0, 0.0) with
        {
            Phase = SeriesPhase.Amortizing,
            FrozenNumerator = 860.0
        };
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0,
            new[] { tranche, NoteProgram.Tranche("B", "B", 70.0, 0.0), NoteProgram.Tranche("C", "C", 70.0, 0.0) },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);

        // Principal is pooled: the notes' half of 800, as in early amortization.
        Named(result, "A").Classes["A"][0].PrincipalPaid.Should().BeApproximately(400.0, 1e-6);
    }

    [Fact]
    public void AnAmortizingTranchePaysWhatItsAccountHoldsAndThenWhatFinanceReimbursed()
    {
        // $100 outstanding, $10 written down, $90 in the account. Finance reimburses the $10, which
        // leaves $10 still to pay once the account has paid.
        var tranche = NoteProgram.Tranche("A", "A", 100.0, 0.0) with
        {
            Phase = SeriesPhase.Amortizing,
            FrozenNumerator = 100.0,
            FundingAccount = 90.0,
            PriorNumerator = 100.0,
            PriorFunding = 0.0,
            Classes = new[] { new RevolvingClass { Key = "A", Balance = 100.0, Coupon = 0.0, WrittenDown = 10.0 } }
        };
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0,
            new[] { tranche, NoteProgram.Tranche("B", "B", 70.0, 0.0), NoteProgram.Tranche("C", "C", 70.0, 0.0) },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 2);

        var rows = Named(result, "A").Classes["A"];
        rows.Sum(r => r.PrincipalPaid).Should().BeApproximately(100.0, 1e-6);
        rows[^1].EndBalance.Should().BeApproximately(0.0, 1e-6);
        rows[^1].CumWritedown.Should().Be(0.0);
    }

    private static RevolvingSeries Reduced(string key, double left, double balance = 70.0) =>
        NoteProgram.Tranche(key, key, balance, 0.0) with
        {
            Fees = Array.Empty<RevolvingFee>(),
            PriorNumerator = balance,
            Classes = new[] { new RevolvingClass { Key = key, Balance = balance, Coupon = 0.0, WrittenDown = balance - left } }
        };

    [Fact]
    public void PrincipalMovesOnlyAsFarAsTheJuniorClassesCanBearIt()
    {
        // A is short $8.60 and Class B and Class C have $1 each left. "No Available Principal
        // Collections will be reallocated ... if the reallocation would result in the reduction of
        // the Nominal Liquidation Amount of those senior notes."
        var senior = NoteProgram.Tranche("A", "A", 860.0, 12.0) with { Fees = Array.Empty<RevolvingFee>() };
        var result = RunShort(ShortMonth(0.0, 0.0, 1_000.0), senior, Reduced("B", 1.0), Reduced("C", 1.0));

        Notes(result).PrincipalReallocated.Should().BeApproximately(2.0, 1e-6);
        Named(result, "A").Classes["A"][0].InterestPaid.Should().BeApproximately(2.0, 1e-6);
        Named(result, "A").Classes["A"][0].InterestDueUnpaid.Should().BeApproximately(6.6, 1e-6);
        Named(result, "B").Periods[0].InvestedAmount.Should().BeApproximately(0.0, 1e-6);
        Named(result, "C").Periods[0].InvestedAmount.Should().BeApproximately(0.0, 1e-6);
        Named(result, "A").Periods[0].InvestedAmount.Should().BeApproximately(860.0, 1e-6);
    }

    [Fact]
    public void AChargeStopsAtWhatATrancheHasLeftAndTheRestIsReported()
    {
        // C's numerator is its $70 a month ago, but it has $1 left. Its $7 charge cannot take it
        // below zero, and nothing lies below it.
        var result = RunShort(ShortMonth(0.0, 200.0), NoteProgram.Tranche("A", "A", 860.0, 0.0) with
        {
            Fees = Array.Empty<RevolvingFee>(),
            RequiredSubordination = new Dictionary<string, double> { ["B"] = 0.0, ["C"] = 0.0 }
        }, NoteProgram.Tranche("B", "B", 70.0, 0.0) with
        {
            Fees = Array.Empty<RevolvingFee>(),
            RequiredSubordination = new Dictionary<string, double> { ["C"] = 0.0 }
        }, Reduced("C", 1.0));

        Named(result, "C").Periods[0].InvestedAmount.Should().Be(0.0);
        Notes(result).UncoveredDefault.Should().BeApproximately(100.0, 1e-6);
        Notes(result).UnchargedDefault.Should().BeApproximately(6.0, 1e-6);
    }

    [Fact]
    public void ATrancheWithNothingLeftStillTakesItsShareOfTheDefault()
    {
        // C has no amount left, but $70 of the month's $1,000 numerator: $7 of the $100 default. With
        // no finance and no class below it, that $7 is reported, not lost.
        var result = RunShort(ShortMonth(0.0, 200.0), Stack()[0], Stack()[1], Reduced("C", 0.0));

        var notes = Notes(result);
        notes.InvestorDefault.Should().BeApproximately(100.0, 1e-6);
        notes.UncoveredDefault.Should().BeApproximately(100.0, 1e-6);
        var charged = Named(result, "A").Classes["A"][0].Writedown + Named(result, "B").Classes["B"][0].Writedown
                                                                    + Named(result, "C").Classes["C"][0].Writedown;
        (charged + notes.UnchargedDefault).Should().BeApproximately(100.0, 1e-6);
    }

    private static RevolvingResult PaidInTheFirstMonth(double reserve, int periods)
    {
        var paying = NoteProgram.Accumulating(NoteProgram.Tranche("A1", "A", 800.0, 0.0), 800.0, 700.0, 800.0, 0.0,
            new[] { 100.0 }, new DateTime(2026, 8, 1), new DateTime(2026, 9, 15)) with
        {
            Reserve = reserve,
            RequiredReserve = reserve
        };
        return NoteProgram.Forecast(NoteProgram.Program(), 4_000.0,
            new[]
            {
                paying, NoteProgram.Tranche("A2", "A", 60.0, 0.0), NoteProgram.Tranche("B", "B", 70.0, 0.0),
                NoteProgram.Tranche("C", "C", 70.0, 0.0)
            },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), periods);
    }

    [Fact]
    public void ARetiredTranchesReserveIsReleasedIntoFinance()
    {
        var withReserve = PaidInTheFirstMonth(5.0, 3);
        var without = PaidInTheFirstMonth(0.0, 3);

        var rows = Named(withReserve, "A1").Periods;
        rows[0].Reserve.Should().Be(5.0);
        rows[1].Reserve.Should().Be(0.0);
        rows[1].ReserveRelease.Should().Be(5.0);
        (Notes(withReserve, 1).ExcessDollars - Notes(without, 1).ExcessDollars).Should().BeApproximately(5.0, 1e-6);
    }

    [Fact]
    public void ARetiredTrancheOwesNoFeeAndHasNoNumerator()
    {
        var rows = Named(PaidInTheFirstMonth(0.0, 3), "A1").Periods;

        rows[0].FeesAccrued.Should().BeGreaterThan(0.0);
        rows[1].FeesAccrued.Should().Be(0.0);
        rows[2].FeesAccrued.Should().Be(0.0);
        rows[1].Tranche!.Numerator.Should().Be(0.0);
    }

    private static RevolvingSeries Bare(string name, string key, double balance, double coupon = 0.0) =>
        NoteProgram.Tranche(name, key, balance, coupon) with { Fees = Array.Empty<RevolvingFee>() };

    [Fact]
    public void FinanceReimbursesTheSeniorClassFirstAndWhatIsLeftGoesDown()
    {
        // Each class is $10 down, the pool is all notes, and finance is $15: A gets all of its $10,
        // B the $5 left, and C nothing. Reimbursing B gives back A's usage of B, in proportion.
        RevolvingSeries Down(string key, double balance) => Bare(key, key, balance) with
        {
            Classes = new[] { new RevolvingClass { Key = key, Balance = balance, Coupon = 0.0, WrittenDown = 10.0 } }
        };
        var a = Down("A", 860.0) with { Usage = new Dictionary<string, double> { ["B"] = 8.0, ["C"] = 2.0 } };
        var month = NoteProgram.Month(970.0, 970.0, 0.0, 15.0, 0.0, 0.0, 30);
        var result = RunShort(month, a, Down("B", 70.0), Down("C", 70.0));

        Named(result, "A").Classes["A"][0].CumWritedown.Should().BeApproximately(0.0, 1e-9);
        Named(result, "B").Classes["B"][0].CumWritedown.Should().BeApproximately(5.0, 1e-9);
        Named(result, "C").Classes["C"][0].CumWritedown.Should().BeApproximately(10.0, 1e-9);
        Row(result, "A").Usage["B"].Should().BeApproximately(4.0, 1e-9);
        Row(result, "A").Usage["C"].Should().BeApproximately(2.0, 1e-9);
    }

    private static RevolvingResult SpreadBelowZero(params RevolvingSeries[] tranches) =>
        NoteProgram.Forecast(
            NoteProgram.Program(triggers: new[] { NoteProgram.ExcessSpreadBelowZero with { Window = 1 } }), 2_000.0,
            tranches, CardProgram.Drivers(0.4, 0.4, 0.001, 0.0, 0.0), 2);

    [Fact]
    public void TheExcessSpreadTestReadsTheSeriesNotTheTranche()
    {
        // On its own Class A has no coupon and no fee, so its spread is positive. The series' is
        // negative: about 1.2% of yield against 4.2% of coupon and 1.5% of fee.
        var result = SpreadBelowZero(Bare("A", "A", 860.0),
            NoteProgram.Tranche("B", "B", 70.0, 30.0), NoteProgram.Tranche("C", "C", 70.0, 30.0));

        Notes(result).ExcessSpreadPct.Should().BeApproximately(0.1 * 365.0 / 31.0 - 5.7, 1e-9);
        Named(result, "A").Periods[0].Phase.Should().Be(SeriesPhase.Revolving);
        Named(result, "A").Periods[0].BaseRatePct.Should().Be(Notes(result).BaseRatePct);
        result.Series.Should().AllSatisfy(s => s.Periods[1].Phase.Should().Be(SeriesPhase.EarlyAmortization));
    }

    [Fact]
    public void AHistorySeededOnOneTrancheActsOnEveryTranche()
    {
        var history = new Dictionary<TriggerMetric, IReadOnlyList<double>>
        {
            [TriggerMetric.ExcessSpread] = new[] { -1.0, -1.0, -1.0 }
        };
        var result = NoteProgram.Forecast(NoteProgram.Program(triggers: new[] { NoteProgram.ExcessSpreadBelowZero }),
            2_000.0,
            new[] { Bare("A1", "A", 430.0) with { History = history }, Bare("A2", "A", 430.0), Stack()[1], Stack()[2] },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);

        result.Series.Should().AllSatisfy(s => s.Periods[0].Phase.Should().Be(SeriesPhase.EarlyAmortization));
    }

    [Fact]
    public void TranchesSeededWithDifferentHistoryAreRefused()
    {
        var one = new Dictionary<TriggerMetric, IReadOnlyList<double>> { [TriggerMetric.ExcessSpread] = new[] { 1.0 } };
        var two = new Dictionary<TriggerMetric, IReadOnlyList<double>> { [TriggerMetric.ExcessSpread] = new[] { 2.0 } };

        var act = () => RunShort(ShortMonth(100.0, 0.0), Stack()[0] with { History = one }, Stack()[1] with { History = two },
            Stack()[2]);

        act.Should().Throw<RevolvingConfigException>().WithMessage("*different history*");
    }

    [Fact]
    public void AFloaterAwaitingItsFirstPayCountsItsRateInTheWeightedCoupon()
    {
        // It owes nothing yet, and half the notes pay 4.0 + 2.0.
        var floater = Bare("A1", "A", 500.0) with
        {
            ClosingDate = new DateTime(2026, 8, 20),
            FirstPayDate = new DateTime(2026, 10, 15),
            Classes = new[]
            {
                new RevolvingClass
                {
                    Key = "A", Balance = 500.0, Index = GraamFlows.Objects.TypeEnum.MarketDataInstEnum.Sofr30Avg,
                    Margin = 2.0, Basis = AccrualBasis.Actual360
                }
            }
        };
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0,
            new[] { floater, Bare("A2", "A", 360.0, 3.0), Bare("B", "B", 70.0, 3.0), Bare("C", "C", 70.0, 3.0) },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);

        Named(result, "A1").Classes["A"][0].InterestAccrued.Should().Be(0.0);
        Notes(result).CouponPct.Should().BeApproximately(4.5, 1e-9);
    }

    private static RevolvingResult Segregating(RevolvingSeries accumulator, ActualPeriod month, DateTime? statement = null) =>
        NoteProgram.Execute(NoteProgram.Program(segregated: true), statement ?? Aug2026, new[] { month },
            new[] { accumulator, Bare("B", "B", 70.0), Bare("C", "C", 70.0) });

    private static RevolvingSeries Holding(double coupon = 3.0, AccrualBasis basis = AccrualBasis.Thirty360) =>
        NoteProgram.Accumulating(NoteProgram.Tranche("A", "A", 850.0, coupon), 850.0, 425.0, 637.5, 425.0,
            new[] { 212.5 }, new DateTime(2026, 7, 1), new DateTime(2026, 12, 15)) with
        {
            Fees = Array.Empty<RevolvingFee>(),
            Classes = new[] { new RevolvingClass { Key = "A", Balance = 850.0, Coupon = coupon, Basis = basis } }
        };

    [Fact]
    public void SegregatedFinanceNetsWhatTheFundingAccountsWereSaidToEarn()
    {
        // $425 at 3% for a month is $1.0625.
        var month = NoteProgram.Month(2_000.0, 2_000.0, 500.0, 200.0, 0.0, 0.0, 30);

        Notes(Segregating(Holding(), month)).SegregatedFinance.Should().BeApproximately(1.0625, 1e-9);
        Notes(Segregating(Holding(), month with { PrincipalAccountEarnings = 1.0 })).SegregatedFinance
            .Should().BeApproximately(0.0625, 1e-9);
        Notes(Segregating(Holding(), month with { InterestAccountEarnings = 1.0 })).SegregatedFinance
            .Should().BeApproximately(1.0625, 1e-9);
    }

    [Fact]
    public void SegregatedFinanceAccruesOnTheTranchesOwnBasis()
    {
        // Aug 17 to Sep 15 is 29 days, not a twelfth of a year.
        var month = NoteProgram.Month(2_000.0, 2_000.0, 500.0, 200.0, 0.0, 0.0, 30);
        var result = Segregating(Holding(basis: AccrualBasis.Actual360), month, new DateTime(2026, 7, 31));

        var days = result.Periods[0].Dates.AccrualDays;
        days.Should().Be(29);
        Notes(result).SegregatedFinance.Should().BeApproximately(425.0 * 0.03 * days / 360.0, 1e-9);
    }

    [Fact]
    public void SegregatedFinanceStopsAtWhatTheTransferorWasAllocated()
    {
        // The pool is $10 over the notes, so 1% of the finance is the transferor's: $1 of $100.
        var month = NoteProgram.Month(990.0 + 10.0, 1_000.0, 0.0, 100.0, 0.0, 0.0, 30) with { AllocationBalance = 1_000.0 };
        var tranche = NoteProgram.Accumulating(Bare("A", "A", 850.0, 12.0), 850.0, 0.0, 850.0, 400.0, new[] { 10.0 },
            new DateTime(2026, 7, 1), new DateTime(2026, 12, 15));
        var result = NoteProgram.Execute(NoteProgram.Program(segregated: true), Aug2026, new[] { month },
            new[] { tranche, Bare("B", "B", 70.0), Bare("C", "C", 70.0) });

        Notes(result).SegregatedFinance.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void AFourClassStackReproducesAPublishedRequiredAmountExample()
    {
        // A prospectus's worked example for a four-class trust: $100mm of Class A over $10mm of
        // Class B, $10mm of Class C and $11.148mm of Class D. It prints Class B's claim of C as
        // $9,112,426 ($8,860,760 + $251,667), and Class D's encumbered amount as the three claims of
        // $10,759,494, $305,595 and $82,452.
        var program = NoteProgram.Program() with
        {
            Delinked = new DelinkedNotes { ClassOrder = new[] { "A", "B", "C", "D" } },
            FinanceSteps = new WaterfallStep[]
            {
                new(WaterfallVerb.PayInterest, "A"), new(WaterfallVerb.PayInterest, "B"),
                new(WaterfallVerb.PayInterest, "C"), new(WaterfallVerb.PayInterest, "D"),
                new(WaterfallVerb.ReleaseTransferor)
            },
            PrincipalSteps = new WaterfallStep[] { new(WaterfallVerb.DepositPfa), new(WaterfallVerb.ReleaseTransferor) }
        };
        var tranches = new[]
        {
            Bare("A", "A", 100_000_000.0) with
            {
                RequiredSubordination = new Dictionary<string, double>
                    { ["B"] = 6.96202532, ["C"] = 8.86075950, ["D"] = 10.75949368 }
            },
            Bare("B", "B", 10_000_000.0) with
            {
                RequiredSubordination = new Dictionary<string, double> { ["C"] = 8.28402367, ["D"] = 10.05917160 }
            },
            Bare("C", "C", 10_000_000.0) with
            {
                RequiredSubordination = new Dictionary<string, double> { ["D"] = 9.28961749 }
            },
            Bare("D", "D", 11_148_000.0)
        };
        var result = NoteProgram.Forecast(program, 400_000_000.0, tranches, CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);

        Row(result, "A").RequiredSubordination["D"].Should().BeApproximately(10_759_494.0, 1.0);
        Row(result, "B").RequiredSubordination["C"].Should().BeApproximately(9_112_426.0, 1.0);
        Row(result, "B").RequiredSubordination["D"].Should().BeApproximately(10_759_494.0 + 305_595.0, 1.0);
        Row(result, "C").RequiredSubordination["D"].Should().BeApproximately(10_759_494.0 + 305_595.0 + 82_452.0, 1.0);
    }

    [Fact]
    public void ADeferredDepositShortfallCarriesIntoTheNextDepositAndIsCaughtUp()
    {
        var tranche = NoteProgram.Accumulating(NoteProgram.Tranche("A", "A", 860.0, 0.0), 860.0, 0.0, 860.0, 0.0,
            new[] { 200.0 }, new DateTime(2026, 8, 1), new DateTime(2027, 8, 15));
        var drivers = CardProgram.Drivers(0.1, 0.1, 0.05, 0.0, 0.0) with
        {
            PaymentRate = new PoolDriver(new[] { 0.1, 0.1, 0.4 })
        };
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0,
            new[] { tranche, NoteProgram.Tranche("B", "B", 70.0, 0.0), NoteProgram.Tranche("C", "C", 70.0, 0.0) },
            drivers, 3);

        // The notes get half of each month's principal. $200 a month is wanted, $100 comes, and the
        // third month's $400 clears the month's $200 and the $200 behind it.
        var rows = Named(result, "A").Periods;
        rows[0].FundingDeposit.Should().BeApproximately(100.0, 1e-6);
        rows[0].FundingDeficit.Should().BeApproximately(100.0, 1e-6);
        rows[1].FundingDeposit.Should().BeApproximately(100.0, 1e-6);
        rows[1].FundingDeficit.Should().BeApproximately(200.0, 1e-6);
        rows[2].FundingDeposit.Should().BeApproximately(400.0, 1e-6);
        rows[2].FundingDeficit.Should().BeApproximately(0.0, 1e-6);
    }

    private static RevolvingSeries[] AccumulatorAndStack() =>
        May2020Tranches(Accumulator(425_000_000.0, 637_500_000.0, 212_500_000.0));

    private static ActualPeriod[] May2020Months(int count) =>
        Enumerable.Range(0, count).Select(i => i == 0 ? May2020 : May2020 with { Beginning = null }).ToArray();

    [Fact]
    public void TheNumeratorRunsAMonthBehindInEveryMonthOfARun()
    {
        // The account opens on $425mm and takes $212.5mm a month. So the first month's numerator is
        // the stated $637.5mm, and each month after it is the opening amount of the month before.
        var slow = NoteProgram.Accumulating(NoteProgram.Tranche("A(2016-4)", "A", 850_000_000.0, 1.49), 850_000_000.0,
            212_500_000.0, 850_000_000.0, 0.0, new[] { 212_500_000.0 }, new DateTime(2020, 3, 1), new DateTime(2020, 9, 15));
        var result = NoteProgram.Execute(NoteProgram.Program(), Apr2020, May2020Months(3), May2020Tranches(slow));

        var rows = Named(result, "A(2016-4)").Periods;
        rows[0].Tranche!.Numerator.Should().Be(850_000_000.0);
        rows[1].Tranche!.Numerator.Should().Be(637_500_000.0);
        rows[2].Tranche!.Numerator.Should().Be(425_000_000.0);
        rows[2].Tranche!.OpeningNominalLiquidation.Should().Be(212_500_000.0);
    }

    [Fact]
    public void SegregatedFinanceReadsTheAccountAMonthBehindInEveryMonthOfARun()
    {
        var slow = NoteProgram.Accumulating(NoteProgram.Tranche("A(2016-4)", "A", 850_000_000.0, 1.49), 850_000_000.0,
            212_500_000.0, 850_000_000.0, 0.0, new[] { 212_500_000.0 }, new DateTime(2020, 3, 1), new DateTime(2020, 9, 15));
        var result = NoteProgram.Execute(NoteProgram.Program(segregated: true), Apr2020, May2020Months(3),
            May2020Tranches(slow));

        // The month before held nothing, and from then on what each month opened on.
        Notes(result, 0).SegregatedFinance.Should().Be(0.0);
        Notes(result, 1).SegregatedFinance.Should().BeApproximately(212_500_000.0 * 0.0149 / 12.0, 0.01);
        Notes(result, 2).SegregatedFinance.Should().BeApproximately(425_000_000.0 * 0.0149 / 12.0, 0.01);
    }

    [Fact]
    public void OneLongRunMatchesMonthlyRunsCarriedOnTheEnginesClose()
    {
        // A replay cuts every month, so a run's own state only proves itself against this.
        var slow = NoteProgram.Accumulating(NoteProgram.Tranche("A(2016-4)", "A", 850_000_000.0, 1.49), 850_000_000.0,
            212_500_000.0, 850_000_000.0, 0.0, new[] { 212_500_000.0 }, new DateTime(2020, 3, 1), new DateTime(2020, 9, 15));
        var run = new RevolvingRun
        {
            Program = NoteProgram.Program(segregated: true),
            StatementDate = Apr2020,
            Periods = 4,
            PoolBalance = May2020.Beginning!.Value,
            ActualPeriods = May2020Months(4),
            Series = May2020Tranches(slow),
            FundingEarningsRate = new[] { 0.0 }
        };
        var whole = CardProgram.Execute(run);

        CardProgram.RequireSameMonths(whole, CardProgram.MonthByMonth(run, whole));
    }

    private static readonly TriggerTest ReserveOnThinSpread = new()
    {
        Name = "RESERVE_ON_THIN_SPREAD",
        Metric = TriggerMetric.ExcessSpread,
        Window = 1,
        Comparison = TriggerComparison.Below,
        Level = 2.0,
        Action = TriggerAction.FundReserve,
        CurePeriods = 2,
        ReserveAmount = 5.0
    };

    private static RevolvingRun StressedNotes(int lag)
    {
        // Finance cannot pay the fee, charge-offs fall on the classes and are reimbursed, a test trips
        // and acts at its lag, a reserve test cures, and a tranche closes and awaits its first pay.
        var maturing = NoteProgram.Accumulating(NoteProgram.Tranche("A1", "A", 300.0, 4.0), 300.0, 100.0, 300.0, 50.0,
            new[] { 20.0, 40.0, 60.0, 80.0 }, new DateTime(2026, 7, 1), new DateTime(2026, 12, 15));
        var waiting = NoteProgram.Tranche("A2", "A", 300.0, 5.0) with
        {
            ClosingDate = new DateTime(2026, 7, 20),
            FirstPayDate = new DateTime(2026, 11, 15)
        };
        var joining = NoteProgram.Tranche("A3", "A", 260.0, 5.0) with
        {
            ClosingDate = new DateTime(2026, 10, 20),
            FirstPayDate = new DateTime(2026, 12, 15)
        };
        var drivers = CardProgram.Drivers(0.4, 0.4, 0.0, 0.01, 0.0) with
        {
            Yield = new PoolDriver(new[] { 0.0, 0.0, 0.1, 0.1, 0.1, 0.0, 0.1 })
        };
        return new RevolvingRun
        {
            Program = NoteProgram.Program(triggers: new[] { NoteProgram.ExcessSpreadBelowZero with { Window = 2 }, ReserveOnThinSpread },
                actionLag: lag, segregated: true),
            StatementDate = new DateTime(2026, 7, 31),
            Periods = 9,
            PoolBalance = 2_000.0,
            Drivers = drivers,
            Series = new[] { maturing, waiting, joining, NoteProgram.Tranche("B", "B", 70.0, 5.0), NoteProgram.Tranche("C", "C", 70.0, 5.0) },
            FundingEarningsRate = new[] { 1.0 }
        };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OneLongRunMatchesMonthlyRunsUnderStress(int lag)
    {
        var run = StressedNotes(lag);
        var whole = CardProgram.Execute(run);

        var months = CardProgram.MonthByMonth(run, whole);

        CardProgram.RequireSameMonths(whole, months);
        months.Should().Contain(m => m.Series.Any(s => s.Closing.FeesDue.Values.Sum() > 0.1));
        months.Should().Contain(m => m.Series.Any(s => s.Closing.Classes[0].InterestShortfall > 0.1));
        months.Should().Contain(m => m.Series.Any(s => s.Closing.Classes[0].WrittenDown > 1.0));
        months.Should().Contain(m => m.Series.Any(s => s.Closing.Usage.Values.Sum() > 1.0));
        months.Should().Contain(m => m.Series.Any(s => s.Closing.TriggerStates.Count > 0));
        whole.Series.Should().Contain(s => s.PhasePath.Any(p => p.Phase == SeriesPhase.EarlyAmortization));
    }

    [Fact]
    public void PrincipalPaysAFeeFinanceCouldNotAndAJuniorClassBearsIt()
    {
        // 1.5% a year on $860 and $70, with no finance and no interest due. No step covers C's own fee.
        var tranches = new[]
        {
            NoteProgram.Tranche("A", "A", 860.0, 0.0), NoteProgram.Tranche("B", "B", 70.0, 0.0),
            NoteProgram.Tranche("C", "C", 70.0, 0.0)
        };
        var result = RunShort(ShortMonth(0.0, 0.0, 1_000.0), tranches);

        Named(result, "A").Periods[0].FeesPaid.Should().BeApproximately(1.075, 1e-9);
        Named(result, "B").Periods[0].FeesPaid.Should().BeApproximately(0.0875, 1e-9);
        Notes(result).PrincipalReallocated.Should().BeApproximately(1.1625, 1e-9);
        Named(result, "C").Periods[0].InvestedAmount.Should().BeApproximately(70.0 - 1.1625, 1e-9);
        Named(result, "B").Periods[0].InvestedAmount.Should().BeApproximately(70.0, 1e-9);
    }

    [Fact]
    public void AClassShortOfInterestIsShortTogether()
    {
        var a1 = Bare("A1", "A", 860.0, 12.0);
        var a2 = Bare("A2", "A", 430.0, 12.0);
        var month = NoteProgram.Month(2_860.0, 2_860.0, 0.0, 6.0, 0.0, 0.0, 30);
        var result = RunShort(month, a1, a2, Stack()[1], Stack()[2]);

        // The notes' $3 of finance pays $8.60 and $4.30 two to one.
        var paid1 = Named(result, "A1").Classes["A"][0].InterestPaid;
        var paid2 = Named(result, "A2").Classes["A"][0].InterestPaid;
        (paid1 + paid2).Should().BeApproximately(3.0, 1e-9);
        paid1.Should().BeApproximately(2.0 * paid2, 1e-9);
    }

    [Fact]
    public void TranchesOfAClassShareAShortDepositByTarget()
    {
        RevolvingSeries Depositing(string name, double balance, double controlled) =>
            NoteProgram.Accumulating(Bare(name, "A", balance), balance, 0.0, balance, 0.0, new[] { controlled },
                new DateTime(2026, 8, 1), new DateTime(2027, 8, 15));

        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0,
            new[] { Depositing("A1", 430.0, 300.0), Depositing("A2", 430.0, 100.0), Stack()[1], Stack()[2] },
            CardProgram.Drivers(0.2, 0.2, 0.05, 0.0, 0.0), 1);

        // The notes' $200 of principal against targets of $300 and $100.
        Named(result, "A1").Periods[0].FundingDeposit.Should().BeApproximately(150.0, 1e-6);
        Named(result, "A2").Periods[0].FundingDeposit.Should().BeApproximately(50.0, 1e-6);
    }

    [Fact]
    public void AMiddleClassPassesWhatItTookFromAboveDownToTheClassBelowIt()
    {
        // $100 uncovered on numerators of 860, 70 and 70. A may use $43 of B and none of C, and B's
        // own $7 spends $4.30 of that, so A moves $38.70 to B and keeps $47.30. B holds up 50% of
        // the $27 of itself A does not claim: $13.50 of C. C's own $7 spends $1.35 of that, and B's
        // own $7 goes to C, which leaves $5.15 of the $38.70 for B to pass down.
        var result = RunShort(ShortMonth(0.0, 200.0),
            Bare("A", "A", 860.0) with { RequiredSubordination = new Dictionary<string, double> { ["B"] = 5.0, ["C"] = 0.0 } },
            Bare("B", "B", 70.0) with { RequiredSubordination = new Dictionary<string, double> { ["C"] = 50.0 } },
            Bare("C", "C", 70.0));

        Named(result, "A").Periods[0].InvestedAmount.Should().BeApproximately(812.7, 1e-9);
        Named(result, "B").Periods[0].InvestedAmount.Should().BeApproximately(36.45, 1e-9);
        Named(result, "C").Periods[0].InvestedAmount.Should().BeApproximately(50.85, 1e-9);
    }

    [Fact]
    public void ChargesFallOnTheMonthsNumeratorNotTheCurrentAmount()
    {
        // Equal numerators and unequal amounts, and nothing moves down: $43 each.
        var a1 = Bare("A1", "A", 500.0) with
        {
            PriorNumerator = 430.0,
            RequiredSubordination = new Dictionary<string, double> { ["B"] = 0.0, ["C"] = 0.0 }
        };
        var a2 = Bare("A2", "A", 360.0) with
        {
            PriorNumerator = 430.0,
            RequiredSubordination = new Dictionary<string, double> { ["B"] = 0.0, ["C"] = 0.0 }
        };
        var result = RunShort(ShortMonth(0.0, 200.0), a1, a2, Stack()[1], Stack()[2]);

        Named(result, "A1").Periods[0].InvestedAmount.Should().BeApproximately(500.0 - 43.0, 1e-9);
        Named(result, "A2").Periods[0].InvestedAmount.Should().BeApproximately(360.0 - 43.0, 1e-9);
    }

    private static RevolvingSeries Maturing(string name, string key, double balance) =>
        NoteProgram.Accumulating(Bare(name, key, balance), balance, 0.0, balance, 0.0, new[] { balance },
            new DateTime(2026, 8, 1), new DateTime(2027, 8, 15));

    [Fact]
    public void AJuniorDepositStopsAtWhatLeavesTheSeniorClaimCovered()
    {
        // 10% of $860 is a claim of $86 on $120 of Class B, so $34 of Class B may go.
        var a = Bare("A", "A", 860.0) with
        {
            RequiredSubordination = new Dictionary<string, double> { ["B"] = 10.0, ["C"] = 0.0 }
        };
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0,
            new[] { a, Maturing("B1", "B", 60.0), Bare("B2", "B", 60.0), Stack()[2] },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);

        Named(result, "B1").Periods[0].FundingDeposit.Should().BeApproximately(34.0, 1e-6);
    }

    [Fact]
    public void AJuniorDepositIsMeasuredAfterTheSeniorDepositsOfTheMonth()
    {
        // Class A deposits $430 first, which halves its claim on Class B to $43, so all $60 may go.
        var a = NoteProgram.Accumulating(Bare("A", "A", 860.0), 860.0, 0.0, 860.0, 0.0, new[] { 430.0 },
            new DateTime(2026, 8, 1), new DateTime(2027, 8, 15)) with
        {
            RequiredSubordination = new Dictionary<string, double> { ["B"] = 10.0, ["C"] = 0.0 }
        };
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0,
            new[] { a, Maturing("B1", "B", 60.0), Bare("B2", "B", 60.0), Stack()[2] },
            CardProgram.Drivers(0.8, 0.8, 0.05, 0.0, 0.0), 1);

        Named(result, "B1").Periods[0].FundingDeposit.Should().BeApproximately(60.0, 1e-6);
    }

    [Fact]
    public void AClassCDepositReadsTheLargerOfTheTwoClaimsNotTheirSum()
    {
        // Class B's figure already holds Class A's claim, so adding them would count it twice. With
        // Class A claiming $40 and Class B $40.50, $39.50 of the $80 of Class C may go.
        var a = Bare("A", "A", 800.0) with
        {
            RequiredSubordination = new Dictionary<string, double> { ["B"] = 0.0, ["C"] = 5.0 }
        };
        var b = Bare("B", "B", 100.0) with { RequiredSubordination = new Dictionary<string, double> { ["C"] = 0.5 } };
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0,
            new[] { a, b, Maturing("C1", "C", 40.0), Bare("C2", "C", 40.0) },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);

        Named(result, "C1").Periods[0].FundingDeposit.Should().BeApproximately(39.5, 1e-6);
    }

    [Fact]
    public void AReallocationForClassBCountsClassBsOwnPrincipalInItsCap()
    {
        // "Principal Collections ... allocable to the Class B notes and Class C notes ... minus" what
        // the steps before it moved, so the class being paid counts. B and C are each allocated $3.50
        // of the month's principal, and B is owed $5.25: more than C's own share.
        var a = Bare("A", "A", 860.0) with
        {
            RequiredSubordination = new Dictionary<string, double> { ["B"] = 0.0, ["C"] = 0.0 }
        };
        var b = Bare("B", "B", 70.0, 90.0) with { RequiredSubordination = new Dictionary<string, double> { ["C"] = 100.0 } };
        var result = RunShort(ShortMonth(0.0, 0.0, 100.0), a, b, Bare("C", "C", 70.0));

        Notes(result).PrincipalReallocated.Should().BeApproximately(5.25, 1e-9);
        Named(result, "B").Classes["B"][0].InterestPaid.Should().BeApproximately(5.25, 1e-9);
    }

    [Fact]
    public void FinanceYieldUsesTheDaysOfThePeriodsOwnYear()
    {
        // February 2024: 5% of the numerator for the month, on a 366-day year.
        var month = NoteProgram.Month(2_000.0, 2_000.0, 500.0, 100.0, 0.0, 0.0, 29);
        var result = NoteProgram.Execute(NoteProgram.Program(), new DateTime(2024, 1, 31), new[] { month }, Stack());

        Notes(result).FinanceYieldPct.Should().BeApproximately(5.0 * 366.0 / 29.0, 1e-9);
        Named(result, "A").Periods[0].PortfolioYieldPct.Should().Be(Notes(result).PortfolioYieldPct);
    }

    private static RevolvingResult PaidWithInterestOwed(params ActualPeriod[] months)
    {
        // A1 is fully funded and pays in the first month, with no finance to pay its $8 of interest.
        var paying = NoteProgram.Accumulating(NoteProgram.Tranche("A1", "A", 800.0, 12.0), 800.0, 800.0, 0.0, 800.0,
            new[] { 100.0 }, new DateTime(2026, 8, 1), new DateTime(2026, 10, 15));
        return NoteProgram.Execute(NoteProgram.Program(), Aug2026, months,
            new[] { paying, Bare("A2", "A", 60.0), Stack()[1], Stack()[2] });
    }

    private static ActualPeriod Finance(double finance, double? begin = 2_000.0) =>
        NoteProgram.Month(begin ?? 2_000.0, 2_000.0, 100.0, finance, 0.0, 0.0, 30) with { Beginning = begin };

    [Fact]
    public void ATranchePaidOffStillOwedInterestIsPaidBeforeTheTransferor()
    {
        var result = PaidWithInterestOwed(Finance(0.0), Finance(1_000.0, null));

        var rows = Named(result, "A1").Classes["A"];
        rows[0].PrincipalPaid.Should().Be(800.0);
        rows[0].InterestDueUnpaid.Should().Be(8.0);
        rows[1].InterestPaid.Should().Be(8.0);
        rows[1].InterestDueUnpaid.Should().Be(0.0);
        Named(result, "A1").Closing.Classes[0].InterestShortfall.Should().Be(0.0);
        Named(result, "A1").Retired.Should().BeTrue();
    }

    [Fact]
    public void ATranchePaidOffAndOwedTakesNoAllocation()
    {
        // Its numerator and frozen numerator are both gone once it is paid.
        var result = PaidWithInterestOwed(Finance(0.0), Finance(1_000.0, null));

        Named(result, "A1").Periods[1].FinanceAllocation.Should().Be(0.0);
        Named(result, "A1").Periods[1].PrincipalAllocation.Should().Be(0.0);
        Named(result, "A1").Periods[1].Tranche!.Numerator.Should().Be(0.0);
        Notes(result, 1).Numerator.Should().Be(200.0);
        Notes(result, 1).PrincipalNumerator.Should().Be(200.0);
    }

    [Fact]
    public void ATranchePaidOffStillOwedIsNotRetiredAndCarriesAcrossACut()
    {
        var first = PaidWithInterestOwed(Finance(0.0));

        Named(first, "A1").Retired.Should().BeFalse();
        Named(first, "A1").Closing.Classes[0].InterestShortfall.Should().Be(8.0);
    }

    [Fact]
    public void ATranchePaidOffStillOwedAFeeIsPaidBeforeTheTransferor()
    {
        var paying = NoteProgram.Accumulating(NoteProgram.Tranche("A1", "A", 800.0, 0.0), 800.0, 800.0, 800.0, 0.0,
            new[] { 100.0 }, new DateTime(2026, 8, 1), new DateTime(2026, 10, 15));
        var noPrincipal = NoteProgram.Month(2_000.0, 2_000.0, 0.0, 0.0, 0.0, 0.0, 30);
        var result = NoteProgram.Execute(NoteProgram.Program(), Aug2026, new[] { noPrincipal, Finance(1_000.0, null) },
            new[] { paying, Bare("A2", "A", 60.0), Stack()[1], Stack()[2] });

        var rows = Named(result, "A1").Periods;
        rows[0].FeesPaid.Should().Be(0.0);
        rows[1].FeesPaid.Should().BeApproximately(rows[0].FeesAccrued, 1e-9);
        Named(result, "A1").Retired.Should().BeTrue();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-5.0)]
    public void NoteFiguresThatAreNotAmountsAreRefusedByName(double bad)
    {
        void Refuses(string field, ActualPeriod month, params RevolvingSeries[] tranches)
        {
            var act = () => RunShort(month, tranches);
            act.Should().Throw<RevolvingConfigException>().WithMessage($"*{field}*");
        }

        var month = ShortMonth(100.0, 0.0);
        var a = Stack()[0];
        var (b, c) = (Stack()[1], Stack()[2]);
        Refuses("interestAccountEarnings", month with { InterestAccountEarnings = bad }, a, b, c);
        Refuses("principalAccountEarnings", month with { PrincipalAccountEarnings = bad }, a, b, c);
        Refuses("allocationBalance", month with { AllocationBalance = bad }, a, b, c);
        Refuses("priorNumerator", month, a with { PriorNumerator = bad }, b, c);
        Refuses("priorFunding", month, a with { PriorFunding = bad }, b, c);
        Refuses("usage of class 'C'", month, a with { Usage = new Dictionary<string, double> { ["C"] = bad } }, b, c);
        Refuses("requiredSubordination of class 'C'", month,
            a with { RequiredSubordination = new Dictionary<string, double> { ["B"] = 8.0, ["C"] = bad } }, b, c);
    }

    [Fact]
    public void ARequiredPercentOverAHundredIsRefused()
    {
        var a = Stack()[0] with { RequiredSubordination = new Dictionary<string, double> { ["B"] = 8.0, ["C"] = 150.0 } };

        Refused(senior: a).Should().Throw<RevolvingConfigException>().WithMessage("*requiredSubordination*");
    }

    [Fact]
    public void UsageOfAClassThatIsNotJuniorIsRefused()
    {
        var a = Stack()[0] with { Usage = new Dictionary<string, double> { ["Z"] = 5.0 } };

        Refused(senior: a).Should().Throw<RevolvingConfigException>().WithMessage("*usage of class 'Z'*not junior*");
    }

    [Fact]
    public void SharingFlagsOnATrancheAreRefusedForWhatTheyAre()
    {
        var a = Stack()[0] with { Group = "", ExcessSharing = true };

        Refused(senior: a).Should().Throw<RevolvingConfigException>().WithMessage("*shares through its steps*");
    }

    [Fact]
    public void ANoteProgramWithNoDepositStepIsRefused()
    {
        var program = NoteProgram.Program() with
        {
            PrincipalSteps = new WaterfallStep[] { new(WaterfallVerb.SharePrincipal), new(WaterfallVerb.ReleaseTransferor) }
        };

        Refused(program).Should().Throw<RevolvingConfigException>().WithMessage("*DEPOSIT_PFA*");
    }

    [Fact]
    public void WhenPaidOnANoteStepIsRefused()
    {
        var limited = new WaterfallStep(WaterfallVerb.DepositPfa, WhenPaid: new[] { "A" });
        var program = NoteProgram.Program() with { PrincipalSteps = NoteProgram.PrincipalSteps.Prepend(limited).ToList() };

        Refused(program).Should().Throw<RevolvingConfigException>().WithMessage("*whenPaid*");
    }

    [Fact]
    public void AStackMonthStatingAccountEarningsIsRefused()
    {
        var month = NoteProgram.Month(1_000.0, 1_000.0, 100.0, 100.0, 0.0, 0.0, 30) with { InterestAccountEarnings = 0.005 };
        var run = CardProgram.Run(CardProgram.Program(), 1_000.0, new[] { CardProgram.Series("S", 500.0, 50.0, 50.0) }, null, 1)
            with { ActualPeriods = new[] { month } };

        ((Action)(() => CardProgram.Execute(run))).Should().Throw<RevolvingConfigException>().WithMessage("*account earnings*");
    }

    [Fact]
    public void ARateOnADustNumeratorIsNoRate()
    {
        // A numerator of a billionth of a dollar would otherwise print a yield of 10^16%.
        var dust = Stack()[0] with { PriorNumerator = 1e-9 };
        var result = NoteProgram.Execute(NoteProgram.Program(), Aug2026,
            new[] { NoteProgram.Month(1e9, 1e9, 1e6, 1e7, 1e5, 0.0, 30) with { InterestAccountEarnings = 500.0 } },
            new[] { dust with { RequiredSubordination = new Dictionary<string, double> { ["B"] = 0.0, ["C"] = 0.0 } }, Reduced("B", 0.0, 1.0) with { PriorNumerator = 0.0 }, Reduced("C", 0.0, 1.0) with { PriorNumerator = 0.0 } });

        Notes(result).FinanceYieldPct.Should().Be(0.0);
        Notes(result).NetLossPct.Should().Be(0.0);
    }

    [Fact]
    public void ATrancheThatClosedBeforeTheStatementMustStateTheMonthBeforeOrItIsDisclosed()
    {
        var old = Stack()[2] with { ClosingDate = new DateTime(2023, 3, 1), FirstPayDate = new DateTime(2023, 4, 15) };
        var result = RunShort(ShortMonth(100.0, 0.0), Stack()[0] with { PriorNumerator = 860.0, PriorFunding = 0.0 },
            Stack()[1] with { PriorNumerator = 70.0, PriorFunding = 0.0 }, old);

        result.Disclosures.Should().Contain(d => d.Contains("'C' states no priorNumerator"));
    }

    [Fact]
    public void ATrancheNotYetIssuedTakesNothing()
    {
        var late = Bare("A2", "A", 430.0) with
        {
            ClosingDate = new DateTime(2026, 11, 20),
            FirstPayDate = new DateTime(2026, 12, 15)
        };
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0,
            new[] { Bare("A1", "A", 430.0), late, Stack()[1], Stack()[2] }, CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 5);

        var rows = Named(result, "A2").Periods;
        rows.Take(3).Should().AllSatisfy(p =>
        {
            p.FinanceAllocation.Should().Be(0.0);
            p.Tranche!.Numerator.Should().Be(0.0);
        });
        rows[3].FinanceAllocation.Should().BeGreaterThan(0.0);
        Notes(result, 0).Numerator.Should().Be(570.0);
    }

    [Fact]
    public void ATrancheNotYetIssuedBearsNoCharge()
    {
        // Class C closes in November, so there is no Class C yet to take August's $100 charge.
        var late = Stack()[2] with
        {
            ClosingDate = new DateTime(2026, 11, 20),
            FirstPayDate = new DateTime(2026, 12, 15)
        };
        var drivers = CardProgram.Drivers(0.4, 0.4, 0.0, 0.1, 0.0);
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0, new[] { Stack()[0], Stack()[1], late }, drivers, 1);

        Named(result, "C").Periods[0].InvestedAmount.Should().Be(70.0);
        Named(result, "C").Classes["C"][0].Writedown.Should().Be(0.0);
        Named(result, "B").Classes["B"][0].Writedown.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void AMonthWithNoNumeratorAddsNoHistory()
    {
        RevolvingSeries Late(RevolvingSeries t) => t with
        {
            ClosingDate = new DateTime(2026, 11, 20),
            FirstPayDate = new DateTime(2026, 12, 15)
        };
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0, Stack().Select(Late).ToArray(),
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 2);

        result.Series.Should().AllSatisfy(s => s.Closing.History.Should().BeEmpty());
    }

    [Fact]
    public void TheWeightedCouponReadsWhatWasOutstandingAsTheMonthOpened()
    {
        // A1 pays in this month, and its 2% still counts: half the notes at 2%, half at 6%.
        var paying = NoteProgram.Accumulating(Bare("A1", "A", 430.0, 2.0), 430.0, 430.0, 430.0, 0.0, new[] { 10.0 },
            new DateTime(2026, 8, 1), new DateTime(2026, 10, 15));
        var result = RunShort(ShortMonth(100.0, 0.0), paying, Bare("A2", "A", 430.0, 6.0), Bare("B", "B", 70.0, 4.0),
            Bare("C", "C", 70.0, 4.0));

        Named(result, "A1").Classes["A"][0].PrincipalPaid.Should().Be(430.0);
        Notes(result).CouponPct.Should().BeApproximately((430.0 * 2.0 + 430.0 * 6.0 + 140.0 * 4.0) / 1_000.0, 1e-9);
    }

    [Fact]
    public void ATranchePaidWhileHoldingAReserveIsNotRetiredUntilItIsReleased()
    {
        var first = PaidInTheFirstMonth(5.0, 1);
        var second = PaidInTheFirstMonth(5.0, 2);

        Named(first, "A1").Retired.Should().BeFalse();
        Named(second, "A1").Retired.Should().BeTrue();
    }

    [Fact]
    public void ATranchePaidAtTheMonthsOpeningStillBearsItsDefault()
    {
        // Early amortization pays A0 out of its account as the month opens. It has no coupon and no
        // fee, so nothing else is owed, and its $43 of the month's default must still be charged.
        var paid = Bare("A0", "A", 430.0) with
        {
            Phase = SeriesPhase.EarlyAmortization,
            FrozenNumerator = 430.0,
            FundingAccount = 430.0,
            PriorNumerator = 430.0,
            PriorFunding = 0.0,
            RequiredSubordination = new Dictionary<string, double> { ["B"] = 0.0, ["C"] = 0.0 }
        };
        var result = RunShort(ShortMonth(0.0, 200.0), paid, Bare("A1", "A", 430.0), Stack()[1], Stack()[2]);

        var notes = Notes(result);
        notes.UncoveredDefault.Should().BeApproximately(100.0, 1e-9);
        var charged = result.Series.SelectMany(s => s.Classes.Values).Sum(c => c[0].Writedown);
        (charged + notes.UnchargedDefault).Should().BeApproximately(100.0, 1e-9);
        notes.UnchargedDefault.Should().BeApproximately(43.0, 1e-9);
        Named(result, "A0").Closing.Classes[0].DefaultDue.Should().Be(0.0);
    }

    private static RevolvingResult LastTrancheOwed(params RevolvingSeries[] others)
    {
        // Paid from its account as the month opens, and owed more than any month allocates it.
        var paid = Bare("A0", "A", 1_000.0, 5.0) with
        {
            Classes = new[] { new RevolvingClass { Key = "A", Balance = 1_000.0, Coupon = 5.0, InterestShortfall = 500.0 } },
            Phase = SeriesPhase.EarlyAmortization,
            FrozenNumerator = 1_000.0,
            FundingAccount = 1_000.0,
            PriorNumerator = 1_000.0,
            PriorFunding = 0.0,
            RequiredSubordination = new Dictionary<string, double> { ["B"] = 0.0, ["C"] = 0.0 }
        };
        return NoteProgram.Forecast(NoteProgram.Program(), 5_000.0, others.Prepend(paid).ToArray(),
            CardProgram.Drivers(0.4, 0.4, 0.0, 0.0, 0.0), 6);
    }

    [Fact]
    public void TheLastTrancheOwedWithNothingLeftIsStrandedAndDisclosed()
    {
        var result = LastTrancheOwed();

        Named(result, "A0").Retired.Should().BeFalse();
        Named(result, "A0").Stranded.Should().BeTrue();
        result.Disclosures.Should().Contain(d => d.Contains("'A0' closes still owed") && d.Contains("504.17 of interest"));
    }

    [Fact]
    public void ATrancheOwedWhileAnotherStillHasAnAmountIsNotStranded()
    {
        var result = LastTrancheOwed(Bare("A1", "A", 1_000.0), Stack()[1], Stack()[2]);

        Named(result, "A0").Stranded.Should().BeFalse();
    }

    [Fact]
    public void ATrancheWrittenOffWithNothingLeftInTheProgramIsStranded()
    {
        var gone = Reduced("C", 0.0) with { PriorNumerator = 0.0, PriorFunding = 0.0 };
        var goneB = Reduced("B", 0.0) with { PriorNumerator = 0.0, PriorFunding = 0.0 };
        var goneA = Reduced("A", 0.0) with
        {
            PriorNumerator = 0.0,
            PriorFunding = 0.0,
            RequiredSubordination = new Dictionary<string, double> { ["B"] = 0.0, ["C"] = 0.0 }
        };
        var result = RunShort(ShortMonth(100.0, 0.0), goneA, goneB, gone);

        result.Series.Should().AllSatisfy(s => s.Stranded.Should().BeTrue());
    }

    [Fact]
    public void ATrancheOfTwoClassesIsRefused()
    {
        var two = NoteProgram.Tranche("A", "A", 860.0) with
        {
            Classes = new[]
            {
                new RevolvingClass { Key = "A", Balance = 860.0 },
                new RevolvingClass { Key = "B", Balance = 70.0 }
            },
            WritedownOrder = new[] { "A", "B" }
        };

        var act = () => RunShort(ShortMonth(0.0, 0.0), two);

        act.Should().Throw<RevolvingConfigException>().WithMessage("*must have one class*");
    }

    [Fact]
    public void ATrancheOfAClassTheOrderDoesNotNameIsRefused()
    {
        var act = () => RunShort(ShortMonth(0.0, 0.0), NoteProgram.Tranche("D", "D", 70.0));

        act.Should().Throw<RevolvingConfigException>().WithMessage("*classOrder does not name*");
    }

    [Fact]
    public void RequiringSubordinationOfASeniorClassIsRefused()
    {
        var wrong = NoteProgram.Tranche("B", "B", 70.0) with
        {
            RequiredSubordination = new Dictionary<string, double> { ["A"] = 5.0 }
        };

        var act = () => RunShort(ShortMonth(0.0, 0.0), wrong);

        act.Should().Throw<RevolvingConfigException>().WithMessage("*not junior*");
    }

    [Fact]
    public void ReallocatingPrincipalInTheFinanceStepsOfANoteSeriesIsRefused()
    {
        var program = NoteProgram.Program() with
        {
            FinanceSteps = new[]
            {
                new WaterfallStep(WaterfallVerb.ReallocatePrincipal, "A", From: new[] { "C" }),
                new WaterfallStep(WaterfallVerb.ReleaseTransferor)
            }
        };

        var act = () => NoteProgram.Execute(program, Aug2026, new[] { Sep2026 }, Sep2026Tranches());

        act.Should().Throw<RevolvingConfigException>().WithMessage("*finance steps*");
    }

    private static Action Refused(RevolvingProgram? program = null, RevolvingSeries? senior = null)
    {
        return () => NoteProgram.Execute(program ?? NoteProgram.Program(), Aug2026, new[] { ShortMonth(100.0, 0.0) },
            new[] { senior ?? Stack()[0], Stack()[1], Stack()[2] });
    }

    [Fact]
    public void AStepNamingAClassTheOrderDoesNotNameIsRefused()
    {
        var unknown = new WaterfallStep(WaterfallVerb.PayInterest, "AA");
        var program = NoteProgram.Program() with { FinanceSteps = NoteProgram.FinanceSteps.Prepend(unknown).ToList() };

        Refused(program).Should().Throw<RevolvingConfigException>().WithMessage("*PayInterest names class 'AA'*");
    }

    [Fact]
    public void AReallocationFromAClassTheOrderDoesNotNameIsRefused()
    {
        var unknown = new WaterfallStep(WaterfallVerb.ReallocatePrincipal, "A", From: new[] { "Z" });
        var program = NoteProgram.Program() with { PrincipalSteps = NoteProgram.PrincipalSteps.Prepend(unknown).ToList() };

        Refused(program).Should().Throw<RevolvingConfigException>().WithMessage("*names class 'Z'*");
    }

    [Fact]
    public void AReallocationFromAClassThatIsNotJuniorIsRefused()
    {
        var upward = new WaterfallStep(WaterfallVerb.ReallocatePrincipal, "B", From: new[] { "A" });
        var program = NoteProgram.Program() with { PrincipalSteps = NoteProgram.PrincipalSteps.Prepend(upward).ToList() };

        Refused(program).Should().Throw<RevolvingConfigException>().WithMessage("*not junior*");
    }

    [Fact]
    public void PhasesOnANoteStepAreRefused()
    {
        var limited = new WaterfallStep(WaterfallVerb.PayInterest, "A", Phases: new[] { SeriesPhase.EarlyAmortization });
        var program = NoteProgram.Program() with { FinanceSteps = NoteProgram.FinanceSteps.Prepend(limited).ToList() };

        Refused(program).Should().Throw<RevolvingConfigException>().WithMessage("*states phases*");
    }

    [Fact]
    public void PayClassOnANoteSeriesIsRefused()
    {
        var pay = new WaterfallStep(WaterfallVerb.PayClass, "A");
        var program = NoteProgram.Program() with { PrincipalSteps = NoteProgram.PrincipalSteps.Prepend(pay).ToList() };

        Refused(program).Should().Throw<RevolvingConfigException>().WithMessage("*PAY_CLASS*funding account*");
    }

    [Fact]
    public void GroupStepsOnANoteSeriesAreRefused()
    {
        var program = NoteProgram.Program() with
        {
            GroupSteps = new[] { new WaterfallStep(WaterfallVerb.ReallocateGroupFinance) }
        };

        Refused(program).Should().Throw<RevolvingConfigException>().WithMessage("*group steps*");
    }

    [Fact]
    public void FinanceStepsWithNoLastReleaseAreRefused()
    {
        var program = NoteProgram.Program() with { FinanceSteps = NoteProgram.FinanceSteps.Take(4).ToList() };

        Refused(program).Should().Throw<RevolvingConfigException>().WithMessage("*finance steps must end*");
    }

    [Fact]
    public void PrincipalStepsWithNoLastReleaseAreRefused()
    {
        var program = NoteProgram.Program() with { PrincipalSteps = NoteProgram.PrincipalSteps.Take(5).ToList() };

        Refused(program).Should().Throw<RevolvingConfigException>().WithMessage("*principal steps must end*");
    }

    [Fact]
    public void ATrancheStatingNoRequiredAmountOfAClassBelowIsRefused()
    {
        var bare = Stack()[0] with { RequiredSubordination = new Dictionary<string, double> { ["B"] = 8.0 } };

        Refused(senior: bare).Should().Throw<RevolvingConfigException>().WithMessage("*no requiredSubordination of class 'C'*");
    }

    [Fact]
    public void ATrancheWhoseFundedClassesAreEmptyIsRefused()
    {
        var bare = Stack()[0] with { FundedClasses = Array.Empty<string>() };

        Refused(senior: bare).Should().Throw<RevolvingConfigException>().WithMessage("*fundedClasses*");
    }

    [Fact]
    public void AFirstPayDateWithNoClosingDateIsRefusedOnATranche()
    {
        var bare = Stack()[0] with { FirstPayDate = new DateTime(2026, 11, 15) };

        Refused(senior: bare).Should().Throw<RevolvingConfigException>().WithMessage("*firstPayDate but no closingDate*");
    }

    [Fact]
    public void ATrancheHoldingAFundingAccountMustStateTheMonthBefore()
    {
        var bare = Stack()[0] with { FundingAccount = 100.0, PriorNumerator = 860.0 };

        Refused(senior: bare).Should().Throw<RevolvingConfigException>().WithMessage("*priorNumerator and priorFunding*");
    }

    [Fact]
    public void ATrancheStatingNoMonthBeforeIsDisclosedAndANewTrancheIsNot()
    {
        var joining = Stack()[0] with
        {
            Name = "joining",
            ClosingDate = new DateTime(2026, 9, 5),
            FirstPayDate = new DateTime(2026, 10, 15)
        };
        var stated = Stack()[1] with { PriorNumerator = 70.0, PriorFunding = 0.0 };
        var result = RunShort(ShortMonth(100.0, 0.0), joining, stated, Stack()[2]);

        result.Disclosures.Should().Contain(d => d.Contains("'C' states no priorNumerator"));
        result.Disclosures.Should().NotContain(d => d.Contains("'joining' states no prior") || d.Contains("'B' states no prior"));
    }

    [Fact]
    public void AnUnknownCoversIsRefusedByName()
    {
        var act = () => ReallocationCoversParser.Parse("TAXES");

        act.Should().Throw<RevolvingConfigException>().WithMessage("*TAXES*");
    }

    [Fact]
    public void ANoteSeriesRunDisclosesThatItsAccountsEarnedNothing()
    {
        RunSep2026().Disclosures.Should().Contain(d => d.Contains("account earnings"));
    }
}
