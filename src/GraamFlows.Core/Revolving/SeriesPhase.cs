namespace GraamFlows.Revolving;

public enum SeriesPhase
{
    Revolving,
    Accumulating,
    Amortizing,
    EarlyAmortization
}

/// <summary>
///     A series' phase. Moves are one-way: <c>REVOLVING → ACCUMULATING → AMORTIZING</c>, with
///     <c>EARLY_AMORTIZATION</c> reachable from the first two. A series with no accumulation period
///     may go straight from revolving to amortizing. Any other move is an error, so a bug cannot
///     quietly put a series back on a phase it has left.
/// </summary>
public sealed class SeriesPhaseMachine
{
    public SeriesPhaseMachine(SeriesPhase phase = SeriesPhase.Revolving)
    {
        Phase = phase;
    }

    public SeriesPhase Phase { get; private set; }

    public static bool CanMove(SeriesPhase from, SeriesPhase to)
    {
        return (from, to) switch
        {
            (SeriesPhase.Revolving, SeriesPhase.Accumulating) => true,
            (SeriesPhase.Revolving, SeriesPhase.Amortizing) => true,
            (SeriesPhase.Revolving, SeriesPhase.EarlyAmortization) => true,
            (SeriesPhase.Accumulating, SeriesPhase.Amortizing) => true,
            (SeriesPhase.Accumulating, SeriesPhase.EarlyAmortization) => true,
            _ => false
        };
    }

    public void MoveTo(SeriesPhase next)
    {
        if (next == Phase)
            return;

        if (!CanMove(Phase, next))
            throw new InvalidOperationException(
                $"A series cannot move from {PhaseNames.ToWire(Phase)} to {PhaseNames.ToWire(next)}: "
                + "phase moves are one-way.");

        Phase = next;
    }

    /// <summary>
    ///     Enters early amortization if the series can still get there. It returns false once the
    ///     series is amortizing or already there, where there is nothing left to accelerate.
    /// </summary>
    public bool TryEnterEarlyAmortization()
    {
        if (!CanMove(Phase, SeriesPhase.EarlyAmortization))
            return false;

        Phase = SeriesPhase.EarlyAmortization;
        return true;
    }
}

public static class PhaseNames
{
    public static string ToWire(SeriesPhase phase)
    {
        return phase switch
        {
            SeriesPhase.Revolving => "REVOLVING",
            SeriesPhase.Accumulating => "ACCUMULATING",
            SeriesPhase.Amortizing => "AMORTIZING",
            SeriesPhase.EarlyAmortization => "EARLY_AMORTIZATION",
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
        };
    }

    public static SeriesPhase Parse(string? phase)
    {
        return phase?.Trim().ToUpperInvariant() switch
        {
            "REVOLVING" => SeriesPhase.Revolving,
            "ACCUMULATING" => SeriesPhase.Accumulating,
            "AMORTIZING" => SeriesPhase.Amortizing,
            "EARLY_AMORTIZATION" => SeriesPhase.EarlyAmortization,
            _ => throw new RevolvingConfigException(
                $"Unknown series phase '{phase}'. Supported: REVOLVING, ACCUMULATING, AMORTIZING and "
                + "EARLY_AMORTIZATION.")
        };
    }
}
