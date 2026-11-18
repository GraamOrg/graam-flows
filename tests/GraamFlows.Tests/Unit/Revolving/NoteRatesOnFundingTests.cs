using FluentAssertions;
using GraamFlows.Revolving;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

/// <summary>
///     A note series whose portfolio yield, base rate and excess spread are the month's dollars times
///     12 over its floating numerator plus its principal funding account balances.
/// </summary>
public class NoteRatesOnFundingTests
{
    private const double Tolerance = 1e-9;

    private static RevolvingProgram Program(bool onFunding, IReadOnlyList<TriggerTest>? triggers = null) =>
        NoteProgram.Program(triggers: triggers) with
        {
            Delinked = NoteProgram.Program().Delinked! with { RatesOnNumeratorAndFunding = onFunding }
        };

    /// <summary>
    ///     An accumulating tranche whose numerator, 600, was taken when its account held 200, so the
    ///     rates divide by 600 + 200 for it, and by their amounts for the others: 1,340 in all.
    /// </summary>
    private static RevolvingSeries[] Tranches() => new[]
    {
        NoteProgram.Accumulating(NoteProgram.Tranche("A1", "A", 800.0, 4.0), 800.0, 300.0, 600.0, 200.0,
            new[] { 100.0 }, new DateTime(2026, 6, 1), new DateTime(2027, 1, 15)),
        NoteProgram.Tranche("A2", "A", 400.0, 5.0),
        NoteProgram.Tranche("B", "B", 70.0, 6.0),
        NoteProgram.Tranche("C", "C", 70.0, 7.0)
    };

    private static double Interest(RevolvingResult result, int period) =>
        result.Series.Sum(s => s.Classes.Values.Sum(rows => rows[period].InterestAccrued));

    private static double Fees(RevolvingResult result, int period) =>
        result.Series.Sum(s => s.Periods[period].FeesAccrued);

    [Fact]
    public void EveryRateIsTheMonthsDollarsTimesTwelveOverTheNumeratorAndTheFundingAccounts()
    {
        var month = NoteProgram.Month(4_000.0, 4_000.0, 1_600.0, 80.0, 8.0, 1.0, 30) with
        {
            InterestAccountEarnings = 2.0
        };
        var result = NoteProgram.Execute(Program(true), new DateTime(2026, 8, 31), new[] { month }, Tranches());
        var notes = result.Periods[0].Notes!;

        var basis = 600.0 + 200.0 + 400.0 + 70.0 + 70.0;
        notes.Numerator.Should().BeApproximately(1_140.0, Tolerance);
        notes.RateBasis.Should().BeApproximately(basis, Tolerance);

        var interest = Interest(result, 0);
        var fees = Fees(result, 0);
        interest.Should().BeGreaterThan(0.0);
        fees.Should().BeGreaterThan(0.0);
        notes.FinanceYieldPct.Should().BeApproximately((notes.FinanceAllocation + 2.0) * 1200.0 / basis, Tolerance);
        notes.NetLossPct.Should().BeApproximately(notes.InvestorDefault * 1200.0 / basis, Tolerance);
        notes.PortfolioYieldPct.Should().BeApproximately(
            (notes.FinanceAllocation + 2.0 - notes.InvestorDefault) * 1200.0 / basis, Tolerance);
        notes.CouponPct.Should().BeApproximately(interest * 1200.0 / basis, Tolerance);
        notes.BaseRatePct.Should().BeApproximately((interest + fees) * 1200.0 / basis, Tolerance);
        notes.ExcessSpreadPct.Should().BeApproximately(
            (notes.FinanceAllocation + 2.0 - notes.InvestorDefault - interest - fees) * 1200.0 / basis, Tolerance);

        // Every tranche reports the series' rates.
        result.Series.Should().AllSatisfy(s => s.Periods[0].ExcessSpreadPct.Should()
            .BeApproximately(notes.PortfolioYieldPct - notes.BaseRatePct, Tolerance));
    }

    [Fact]
    public void WithoutTheFieldTheRatesAreAsBefore()
    {
        var month = NoteProgram.Month(4_000.0, 4_000.0, 1_600.0, 80.0, 8.0, 1.0, 30);
        var result = NoteProgram.Execute(Program(false), new DateTime(2026, 8, 31), new[] { month }, Tranches());
        var notes = result.Periods[0].Notes!;

        notes.RateBasis.Should().BeNull();
        notes.FinanceYieldPct.Should().BeApproximately(notes.FinanceAllocation / 1_140.0 * 365.0 / 30.0 * 100.0,
            Tolerance);
        notes.BaseRatePct.Should().BeApproximately(notes.CouponPct + 1.5, Tolerance);
    }

    private static RevolvingResult Forecast(bool onFunding, TriggerTest? test = null) =>
        NoteProgram.Forecast(Program(onFunding, test == null ? null : new[] { test }), 4_000.0, Tranches(),
            CardProgram.Drivers(), 2);

    [Fact]
    public void TheExcessSpreadTestReadsTheRatesOnTheNumeratorAndFunding()
    {
        var onFunding = Forecast(true).Periods[0].Notes!.ExcessSpreadPct;
        var onDays = Forecast(false).Periods[0].Notes!.ExcessSpreadPct;

        // The wider basis and 12 months rather than the period's days give a lower spread. A level
        // between the two fails one measure and passes the other.
        onFunding.Should().BeLessThan(onDays - 0.1);
        var test = NoteProgram.ExcessSpreadBelowZero with { Window = 1, Level = (onFunding + onDays) / 2.0 };
        var tripped = Forecast(true, test);
        var passed = Forecast(false, test);

        var reading = tripped.Series[0].Periods[1].Triggers.Single();
        reading.Value.Should().BeApproximately(onFunding, Tolerance);
        reading.Acting.Should().BeTrue();
        tripped.Series.Should().AllSatisfy(s => s.Periods[1].Phase.Should().Be(SeriesPhase.EarlyAmortization));
        passed.Series.Should().AllSatisfy(s => s.Periods[1].Phase.Should().NotBe(SeriesPhase.EarlyAmortization));
    }
}
