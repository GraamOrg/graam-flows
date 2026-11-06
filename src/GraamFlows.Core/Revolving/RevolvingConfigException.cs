namespace GraamFlows.Revolving;

/// <summary>
///     A revolving request the engine refuses by name: an unknown verb or trigger, a program that
///     does not state something it must state, or an impossible input. It is never degraded into a
///     default or a skipped step, because a run that quietly did less than the request asked for
///     cannot be told from a correct one.
/// </summary>
public sealed class RevolvingConfigException : ArgumentException
{
    public RevolvingConfigException(string message) : base(message)
    {
    }
}
