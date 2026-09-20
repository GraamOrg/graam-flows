namespace GraamFlows.Api.Models;

/// <summary>Where the index a run priced on came from.</summary>
public enum MarketRateSource
{
    /// <summary>
    ///     No curve was available, so every index resolved to a flat rate this engine assumed
    ///     rather than one the request supplied.
    /// </summary>
    Assumed,

    /// <summary>Every index the run resolved came from a curve in the request.</summary>
    Supplied
}

/// <summary>
///     What the run priced its floating indices on (graam-flows#102).
///
///     The three fallbacks #102 is about were all invisible: the response of a run priced on an
///     assumed 5% — or on a 0.0 index, or on a curve the request sent under a name this engine
///     does not know — was byte-identical to the response of a correct one, so a wrong number
///     survived review by looking ordinary. Two of those three are now errors. The third is
///     load-bearing for fixed-rate callers and stays, which makes disclosing it the whole fix:
///     this object is present on every successful response so that "the index was assumed" is
///     readable from the answer itself.
///
///     Additive: it is a new key on the response, and no existing field changes.
/// </summary>
public class MarketRateResolutionDto
{
    /// <summary>
    ///     <see cref="MarketRateSource.Assumed" /> means no curve was in play and
    ///     <see cref="AssumedRate" /> was used for every index. A consumer that cares whether a
    ///     number is real needs to read exactly this field.
    /// </summary>
    public MarketRateSource IndexSource { get; set; }

    /// <summary>The flat rate every index resolved to, when and only when it was assumed.</summary>
    public double? AssumedRate { get; set; }

    /// <summary>The indices resolved from the request's curves, by their engine names.</summary>
    public List<string> ResolvedIndices { get; set; } = new();

    /// <summary>
    ///     Names the request supplied that are not an index this engine knows, and so were not
    ///     used. Non-empty here means at least one other name DID resolve — a request where none
    ///     resolved is rejected rather than answered.
    /// </summary>
    public List<string> UnrecognizedIndices { get; set; } = new();

    /// <summary>
    ///     Names the request supplied for a known index but with no usable
    ///     <c>[monthOffset, rate]</c> point, and so were not used.
    /// </summary>
    public List<string> IndicesWithoutPoints { get; set; } = new();

    /// <summary>
    ///     Plain-language statements of anything above that makes a number in this response rest
    ///     on something the caller did not supply. Null when nothing does.
    /// </summary>
    public List<string>? Warnings { get; set; }
}
