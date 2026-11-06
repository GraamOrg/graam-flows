using FluentAssertions;
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
    public void FinanceReimbursesADeficitSeniorClassFirstAndGivesUsageBack()
    {
        var charged = RunShort(ShortMonth(0.0, 200.0));
        var usedA = Row(charged, "A").Usage["C"];
        var deficitC = 70.0 - Named(charged, "C").Periods[0].InvestedAmount;

        var tranches = Stack().Select(t => t.Name switch
        {
            "A" => t with { Usage = Row(charged, "A").Usage },
            "B" => t with
            {
                Usage = Row(charged, "B").Usage,
                Classes = new[] { t.Classes[0] with { WrittenDown = 70.0 - Named(charged, "B").Periods[0].InvestedAmount } }
            },
            _ => t with { Classes = new[] { t.Classes[0] with { WrittenDown = deficitC } } }
        }).ToArray();
        var result = RunShort(ShortMonth(1_000.0, 0.0), tranches);

        Named(result, "C").Periods[0].InvestedAmount.Should().BeApproximately(70.0, 1e-6);
        Row(result, "A").Usage["C"].Should().BeLessThan(usedA);
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
    public void TheExcessSpreadTestReadsTheSeriesAndActsOnEveryTranche()
    {
        var history = new Dictionary<TriggerMetric, IReadOnlyList<double>>
        {
            [TriggerMetric.ExcessSpread] = new[] { -1.0, -1.0, -1.0 }
        };
        var tranches = Stack().Select(t => t with { History = history, FrozenNumerator = null }).ToArray();
        var result = NoteProgram.Forecast(
            NoteProgram.Program(triggers: new[] { NoteProgram.ExcessSpreadBelowZero }), 2_000.0, tranches,
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);

        result.Series.Should().AllSatisfy(s => s.Periods[0].Phase.Should().Be(SeriesPhase.EarlyAmortization));
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
    public void ADeferredDepositShortfallCarriesOverOnATranche()
    {
        var tranche = NoteProgram.Accumulating(NoteProgram.Tranche("A", "A", 860.0, 0.0), 860.0, 0.0, 860.0, 0.0,
            new[] { 500.0 }, new DateTime(2026, 8, 1), new DateTime(2027, 8, 15));
        var result = NoteProgram.Forecast(NoteProgram.Program(), 2_000.0,
            new[] { tranche, NoteProgram.Tranche("B", "B", 70.0, 0.0), NoteProgram.Tranche("C", "C", 70.0, 0.0) },
            CardProgram.Drivers(0.1, 0.1, 0.05, 0.0, 0.0), 2);

        var rows = Named(result, "A").Periods;
        rows[0].FundingDeposit.Should().BeApproximately(100.0, 1e-6);
        rows[0].FundingDeficit.Should().BeApproximately(400.0, 1e-6);
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
