using FluentAssertions;
using GraamFlows.Revolving;
using Xunit;

namespace GraamFlows.Tests.Unit.Revolving;

public class SeriesPhaseTests
{
    [Theory]
    [InlineData(SeriesPhase.Accumulating, SeriesPhase.Revolving)]
    [InlineData(SeriesPhase.Amortizing, SeriesPhase.Revolving)]
    [InlineData(SeriesPhase.Amortizing, SeriesPhase.Accumulating)]
    [InlineData(SeriesPhase.EarlyAmortization, SeriesPhase.Revolving)]
    [InlineData(SeriesPhase.EarlyAmortization, SeriesPhase.Accumulating)]
    [InlineData(SeriesPhase.EarlyAmortization, SeriesPhase.Amortizing)]
    [InlineData(SeriesPhase.Amortizing, SeriesPhase.EarlyAmortization)]
    public void APhaseNeverMovesBackward(SeriesPhase from, SeriesPhase to)
    {
        var machine = new SeriesPhaseMachine(from);

        var move = () => machine.MoveTo(to);

        move.Should().Throw<InvalidOperationException>().WithMessage("*one-way*");
        machine.Phase.Should().Be(from);
    }

    [Theory]
    [InlineData(SeriesPhase.Revolving, SeriesPhase.Accumulating)]
    [InlineData(SeriesPhase.Revolving, SeriesPhase.EarlyAmortization)]
    [InlineData(SeriesPhase.Accumulating, SeriesPhase.Amortizing)]
    [InlineData(SeriesPhase.Accumulating, SeriesPhase.EarlyAmortization)]
    public void TheForwardMovesAreLegal(SeriesPhase from, SeriesPhase to)
    {
        var machine = new SeriesPhaseMachine(from);

        machine.MoveTo(to);

        machine.Phase.Should().Be(to);
    }

    [Theory]
    [InlineData(SeriesPhase.Revolving, true)]
    [InlineData(SeriesPhase.Accumulating, true)]
    [InlineData(SeriesPhase.Amortizing, false)]
    [InlineData(SeriesPhase.EarlyAmortization, false)]
    public void EarlyAmortizationIsReachableFromTheFirstTwoPhases(SeriesPhase from, bool moves)
    {
        var machine = new SeriesPhaseMachine(from);

        machine.TryEnterEarlyAmortization().Should().Be(moves);
        machine.Phase.Should().Be(moves ? SeriesPhase.EarlyAmortization : from);
    }

    [Theory]
    [InlineData("REVOLVING", SeriesPhase.Revolving)]
    [InlineData("early_amortization", SeriesPhase.EarlyAmortization)]
    public void WireNamesParse(string name, SeriesPhase phase)
    {
        PhaseNames.Parse(name).Should().Be(phase);
    }

    [Fact]
    public void AnUnknownPhaseIsRefusedByName()
    {
        var refuse = () => PhaseNames.Parse("WINDING_DOWN");

        refuse.Should().Throw<RevolvingConfigException>().WithMessage("*WINDING_DOWN*");
    }

    [Theory]
    [InlineData(SeriesPhase.Revolving)]
    [InlineData(SeriesPhase.Accumulating)]
    [InlineData(SeriesPhase.Amortizing)]
    [InlineData(SeriesPhase.EarlyAmortization)]
    public void WireNamesRoundTrip(SeriesPhase phase)
    {
        PhaseNames.Parse(PhaseNames.ToWire(phase)).Should().Be(phase);
    }
}
