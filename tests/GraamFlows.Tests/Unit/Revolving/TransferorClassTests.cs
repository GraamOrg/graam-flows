using FluentAssertions;
using GraamFlows.Revolving;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>
///     A note series of Class A tranches whose credit enhancement is a subordinated transferor amount:
///     each tranche requires 100/74 − 1 of its own nominal liquidation amount, and the amount is the
///     sum of those requirements. It runs as a junior class S that follows its seniors.
/// </summary>
public class TransferorClassTests
{
    /// <summary>35.135135...% of each tranche's own amount.</summary>
    private const double RequiredPct = (100.0 / 74.0 - 1.0) * 100.0;

    private static double Required(double amount) => RequiredPct / 100.0 * amount;

    private static readonly DateTime Aug2026 = new(2026, 8, 31);

    private static RevolvingProgram Program(bool follows = true, IReadOnlyList<TriggerTest>? triggers = null) =>
        NoteProgram.Program(triggers: triggers) with
        {
            FinanceSteps = new WaterfallStep[]
            {
                new(WaterfallVerb.PayInterest, "A"),
                new(WaterfallVerb.CoverDefaults, "A"),
                new(WaterfallVerb.ReimburseChargeoff, "A"),
                new(WaterfallVerb.ReimburseChargeoff, "S"),
                new(WaterfallVerb.ReleaseTransferor)
            },
            PrincipalSteps = new WaterfallStep[]
            {
                new(WaterfallVerb.DepositPfa),
                new(WaterfallVerb.ReleaseTransferor)
            },
            Delinked = new DelinkedNotes { ClassOrder = new[] { "A", "S" }, TransferorClass = follows ? "S" : null }
        };

    private static RevolvingSeries ClassA(string name, double balance) =>
        NoteProgram.Tranche(name, "A", balance, 0.0) with
        {
            Fees = Array.Empty<RevolvingFee>(),
            RequiredSubordination = new Dictionary<string, double> { ["S"] = RequiredPct }
        };

    private static RevolvingSeries ClassS(double amount) =>
        NoteProgram.Tranche("S", "S", amount, 0.0) with { Fees = Array.Empty<RevolvingFee>() };

    /// <summary>A $750 tranche that deposits and pays on the first distribution, and a $1,000 one.</summary>
    private static RevolvingSeries[] TwoTranches() => new[]
    {
        NoteProgram.Accumulating(ClassA("A1", 750.0), 750.0, 0.0, 750.0, 0.0, new[] { 750.0 },
            new DateTime(2026, 8, 1), new DateTime(2026, 9, 15)),
        ClassA("A2", 1_000.0),
        ClassS(Required(1_750.0))
    };

    private static RevolvingResult Forecast(RevolvingProgram program, int periods) =>
        NoteProgram.Forecast(program, 4_000.0, TwoTranches(), CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), periods);

    private static SeriesResult Named(RevolvingResult result, string name) =>
        result.Series.Single(s => s.Name == name);

    private static NotePeriod Notes(RevolvingResult result, int period = 0) => result.Periods[period].Notes!;

    [Fact]
    public void AfterTheFirstTranchePaysTheAmountIsTheRemainingTranchesRequirement()
    {
        var result = Forecast(Program(), 3);

        Named(result, "A1").Classes["A"][0].PrincipalPaid.Should().BeApproximately(750.0, 1e-9);
        foreach (var period in new[] { 0, 1, 2 })
            Named(result, "S").Periods[period].InvestedAmount.Should().BeApproximately(Required(1_000.0), 1e-9);
        Notes(result).TransferorClassReleased.Should().BeApproximately(Required(750.0), 1e-9);
        Notes(result, 1).TransferorClassReleased.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void WhatItReleasesGoesToTheTransferorAndNoCashMoves()
    {
        var follows = Forecast(Program(), 1);
        var stated = Forecast(Program(follows: false), 1);

        // The same month's cash, to the cent, and the transferor's interest in the pool is larger by
        // what the class gave back.
        var on = follows.Periods[0];
        var off = stated.Periods[0];
        on.TransferorFinance.Should().BeApproximately(off.TransferorFinance, 1e-9);
        on.TransferorPrincipal.Should().BeApproximately(off.TransferorPrincipal, 1e-9);
        on.CashOut.Should().BeApproximately(off.CashOut, 1e-9);
        (on.TransferorAmount - off.TransferorAmount).Should().BeApproximately(Required(750.0), 1e-9);
        Named(follows, "S").Classes["S"][0].PrincipalPaid.Should().Be(0.0);

        // Without the field the class keeps its stated amount and reports no release.
        Named(stated, "S").Periods[0].InvestedAmount.Should().BeApproximately(Required(1_750.0), 1e-9);
        off.Notes!.TransferorClassReleased.Should().BeNull();
    }

    [Fact]
    public void ItRisesAndDrawsFromTheTransferorWhenATrancheJoins()
    {
        // A $500 tranche closes in the first monthly period, so the amount rises by its requirement.
        var joining = ClassA("A3", 500.0) with
        {
            ClosingDate = new DateTime(2026, 8, 20),
            FirstPayDate = new DateTime(2026, 9, 15)
        };
        var result = NoteProgram.Forecast(Program(), 4_000.0,
            new[] { ClassA("A2", 1_000.0), joining, ClassS(Required(1_000.0)) },
            CardProgram.Drivers(0.4, 0.4, 0.05, 0.0, 0.0), 1);

        Named(result, "S").Periods[0].InvestedAmount.Should().BeApproximately(Required(1_500.0), 1e-9);
        Notes(result).TransferorClassReleased.Should().BeApproximately(-Required(500.0), 1e-9);
    }

    [Fact]
    public void ItStopsFollowingOnceEarlyAmortizationStarts()
    {
        // The test always fails, so every tranche enters early amortization in the second month and
        // the remaining tranche starts to deposit. The amount holds where it stood.
        var trip = NoteProgram.ExcessSpreadBelowZero with { Window = 1, Level = 100.0 };
        var result = Forecast(Program(triggers: new[] { trip }), 2);

        Named(result, "S").Periods[1].Phase.Should().Be(SeriesPhase.EarlyAmortization);
        Named(result, "A2").Periods[1].AdjustedInvested.Should().BeLessThan(1_000.0 - 1.0);
        Named(result, "S").Periods[1].InvestedAmount.Should().BeApproximately(Required(1_000.0), 1e-9);
        Named(result, "S").Classes["S"][1].PrincipalPaid.Should().Be(0.0);
        Notes(result, 1).TransferorClassReleased.Should().Be(0.0);
    }

    [Fact]
    public void LossesStillWriteTheClassDownFirstAndTheWritedownSurvivesTheReset()
    {
        // Pool 2,000, notes 1,400, no finance: the notes' 140 of a 200 default is uncovered. Class A's
        // 100 moves to S and S bears its own 40, so A is whole. S then resets from 400 to what A
        // requires, and its 140 written down stays written down.
        var month = NoteProgram.Month(2_000.0, 2_000.0, 500.0, 0.0, 200.0, 0.0, 30);
        var result = NoteProgram.Execute(Program(), Aug2026, new[] { month },
            new[] { ClassA("A", 1_000.0), ClassS(400.0) });

        Notes(result).UncoveredDefault.Should().BeApproximately(140.0, 1e-9);
        Named(result, "A").Periods[0].InvestedAmount.Should().BeApproximately(1_000.0, 1e-9);
        Named(result, "A").Classes["A"][0].CumWritedown.Should().Be(0.0);
        Named(result, "S").Classes["S"][0].CumWritedown.Should().BeApproximately(140.0, 1e-9);
        Named(result, "S").Periods[0].InvestedAmount.Should().BeApproximately(Required(1_000.0) - 140.0, 1e-9);
        Notes(result).TransferorClassReleased.Should().BeApproximately(400.0 - Required(1_000.0), 1e-9);
    }

    [Fact]
    public void ATransferorClassThatIsNotTheLastClassIsRefused()
    {
        var program = NoteProgram.Program() with
        {
            Delinked = NoteProgram.Program().Delinked! with { TransferorClass = "B" }
        };
        var act = () => NoteProgram.Execute(program, Aug2026,
            new[] { NoteProgram.Month(2_000.0, 2_000.0, 500.0, 100.0, 0.0, 0.0, 30) },
            new[]
            {
                NoteProgram.Tranche("A", "A", 860.0, 0.0), NoteProgram.Tranche("B", "B", 70.0, 0.0),
                NoteProgram.Tranche("C", "C", 70.0, 0.0)
            });

        act.Should().Throw<RevolvingConfigException>().WithMessage("*transferorClass 'B' must be the last class*");
    }

    [Fact]
    public void TwoTranchesOfTheTransferorClassAreRefused()
    {
        var act = () => NoteProgram.Execute(Program(), Aug2026,
            new[] { NoteProgram.Month(2_000.0, 2_000.0, 500.0, 100.0, 0.0, 0.0, 30) },
            new[] { ClassA("A", 1_000.0), ClassS(200.0), ClassS(200.0) with { Name = "S2" } });

        act.Should().Throw<RevolvingConfigException>().WithMessage("*transferorClass 'S' must be one tranche*2 are*");
    }
}
