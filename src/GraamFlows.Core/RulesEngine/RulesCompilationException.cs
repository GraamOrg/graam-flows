namespace GraamFlows.RulesEngine;

/// <summary>
/// Thrown when a deal's authored rules, triggers, or tranche coupon formulas cannot be
/// compiled because a required field is missing or malformed (e.g. a tranche with
/// couponType="Formula" but no couponFormula).
///
/// Unlike a bare <see cref="NullReferenceException"/>, this carries an actionable,
/// human-readable message naming the offending entity and field, so the caller — and the
/// agent that authored the deal — can fix the deal JSON without decoding a stack trace.
/// </summary>
public sealed class RulesCompilationException : Exception
{
    public RulesCompilationException(string message) : base(message)
    {
    }
}
