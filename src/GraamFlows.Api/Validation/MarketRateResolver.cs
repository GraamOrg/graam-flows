using GraamFlows.Api.Models;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Api.Validation;

/// <summary>
///     The single place a caller's <c>marketRates</c> dictionary becomes typed curves
///     (graam-flows#102).
///
///     Both endpoints used to do this inline, each with its own <c>Enum.TryParse</c> and its own
///     silent <c>continue</c>, and both ended in the same shape: an entry the caller supplied was
///     dropped without a word and the run priced a floating instrument on an index nobody gave it
///     — flat 5% on one path, 0.0 on the other, HTTP 200 on both. Resolving in one place means
///     one vocabulary, one error message, and one answer to "what did the run actually price on".
///
///     What this refuses, and why refusing beats resolving:
///     <list type="bullet">
///         <item>
///             an unrecognized name. <c>SOFR_3M</c> is a natural spelling of the most common index
///             in the market and is not a member of <see cref="MarketDataInstEnum" />.
///         </item>
///         <item>
///             a name given as an ordinal. <c>Enum.TryParse</c> accepts <c>"5"</c> and answers
///             <c>Swap2Y</c>, and accepts <c>"999"</c> and answers a cast that is not a member at
///             all. A caller writing a number is naming a different index than the one they meant.
///         </item>
///         <item>
///             <see cref="MarketDataInstEnum.None" />, which means "this instrument has no index".
///             It is not a curve a caller can supply, and letting it through would make the
///             <c>default</c> in <see cref="Objects.DataObjects.MarketData.SetValueForIndex" />
///             fire on a request rather than on a programming mistake.
///         </item>
///         <item>
///             a curve with no usable <c>[monthOffset, rate]</c> point. A supplied-but-empty curve
///             resolves to the provider's fallback exactly like a curve that was never supplied.
///         </item>
///     </list>
///
///     Rejecting an entry is not the same as rejecting the request: see
///     <see cref="MarketRateResolution.Error" /> for which mismatches are fatal and which are
///     reported in the response.
/// </summary>
public static class MarketRateResolver
{
    /// <summary>
    ///     The flat rate a request that supplies no curves at all prices every index on. It is the
    ///     legacy behaviour and is deliberately kept: a fixed-rate request ignores the provider
    ///     entirely, and changing it would break every such caller. What changes in #102 is that
    ///     the response now says the run used it — see <see cref="MarketRateResolutionDto" />.
    /// </summary>
    public const double AssumedFlatRate = 5.0;

    private static readonly string[] LegalIndexNamesArray = Enum.GetValues<MarketDataInstEnum>()
        .Where(inst => inst != MarketDataInstEnum.None)
        .Select(inst => inst.ToString())
        .ToArray();

    /// <summary>The index names a caller may use, for an error message that names them.</summary>
    public static string LegalIndexNames => string.Join(", ", LegalIndexNamesArray);

    public static MarketRateResolution Resolve(Dictionary<string, List<double[]>>? marketRates)
    {
        var curves = new Dictionary<MarketDataInstEnum, List<double[]>>();
        var unrecognized = new List<string>();
        var withoutPoints = new List<string>();

        if (marketRates != null)
            foreach (var (instName, points) in marketRates)
            {
                if (!TryParseIndex(instName, out var inst))
                {
                    unrecognized.Add(Describe(instName));
                    continue;
                }

                // A point is usable only if it carries both a month offset and a rate. The
                // waterfall path used to skip a short row here and the collateral path skipped it
                // one layer down in CurveRateProvider; either way a curve of nothing but short
                // rows was indistinguishable from a curve that was never sent.
                var usable = (points ?? new List<double[]>())
                    .Where(p => p is { Length: >= 2 })
                    .ToList();
                if (usable.Count == 0)
                {
                    withoutPoints.Add(Describe(instName));
                    continue;
                }

                curves[inst] = usable;
            }

        return new MarketRateResolution(
            marketRates is { Count: > 0 }, curves, unrecognized, withoutPoints);
    }

    public static bool TryParseIndex(string? name, out MarketDataInstEnum inst)
    {
        inst = MarketDataInstEnum.None;

        var value = name?.Trim();
        if (string.IsNullOrEmpty(value))
            return false;

        // Refuse an ordinal before TryParse can resolve one — see the class remarks.
        if (value.All(c => char.IsDigit(c) || c is '-' or '+'))
            return false;

        if (!Enum.TryParse(value, true, out inst) || !Enum.IsDefined(inst) ||
            inst == MarketDataInstEnum.None)
        {
            inst = MarketDataInstEnum.None;
            return false;
        }

        return true;
    }

    private static string Describe(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "(blank)" : name;
}

/// <summary>
///     What a caller's <c>marketRates</c> resolved to, and whether the request can proceed.
/// </summary>
public sealed class MarketRateResolution
{
    internal MarketRateResolution(
        bool callerSupplied,
        Dictionary<MarketDataInstEnum, List<double[]>> curves,
        List<string> unrecognizedIndices,
        List<string> indicesWithoutPoints)
    {
        CallerSupplied = callerSupplied;
        Curves = curves;
        UnrecognizedIndices = unrecognizedIndices;
        IndicesWithoutPoints = indicesWithoutPoints;
    }

    /// <summary>The request carried a non-empty <c>marketRates</c>, whatever it resolved to.</summary>
    public bool CallerSupplied { get; }

    public IReadOnlyDictionary<MarketDataInstEnum, List<double[]>> Curves { get; }

    /// <summary>Names supplied that are not an index this engine knows.</summary>
    public IReadOnlyList<string> UnrecognizedIndices { get; }

    /// <summary>Names supplied that are a known index but carried no usable point.</summary>
    public IReadOnlyList<string> IndicesWithoutPoints { get; }

    /// <summary>
    ///     True when no curve is available and the run will price every index on
    ///     <see cref="MarketRateResolver.AssumedFlatRate" />. Only reachable for a request that
    ///     supplied nothing: a request that supplied curves and landed none is an
    ///     <see cref="Error" />.
    /// </summary>
    public bool UsesAssumedRate => Curves.Count == 0;

    /// <summary>
    ///     Non-null when the request must be rejected.
    ///
    ///     The line is drawn at "the caller supplied curves and NOT ONE of them landed". That
    ///     request has no reading under which the flat 5% is what they asked for — the documented
    ///     justification for the fallback is a curve-less request, and this one is not curve-less.
    ///
    ///     A PARTIAL mismatch is deliberately not fatal; it is reported in the response instead.
    ///     Rejecting it would take down a live caller that sends a recognized index alongside an
    ///     unrecognized one today, and the failure it guards against is narrower: it only bites if
    ///     an instrument actually references the index that went missing. Naming the dropped entry
    ///     in the response is what the old code never did.
    /// </summary>
    public string? Error
    {
        get
        {
            if (!CallerSupplied || Curves.Count > 0)
                return null;

            var parts = new List<string>();
            if (UnrecognizedIndices.Count > 0)
                parts.Add($"unrecognized index name(s): {string.Join(", ", UnrecognizedIndices)}");
            if (IndicesWithoutPoints.Count > 0)
                parts.Add(
                    "index name(s) with no usable [monthOffset, rate] point: " +
                    string.Join(", ", IndicesWithoutPoints));

            return
                "marketRates was supplied but no entry resolved to a curve, so the run would have " +
                $"priced every floating index on an assumed flat {MarketRateResolver.AssumedFlatRate}% " +
                "that the request never supplied. " +
                (parts.Count > 0 ? string.Join("; ", parts) + ". " : "") +
                $"Legal index names are: {MarketRateResolver.LegalIndexNames}.";
        }
    }

    /// <summary>
    ///     What the response says about the index the run priced on. Populated on every successful
    ///     run, including the ordinary one where everything resolved — an assumed index has to be
    ///     distinguishable from a supplied one by reading the response, not by knowing what was
    ///     sent.
    /// </summary>
    public MarketRateResolutionDto Describe()
    {
        var dto = new MarketRateResolutionDto
        {
            IndexSource = UsesAssumedRate ? MarketRateSource.Assumed : MarketRateSource.Supplied,
            AssumedRate = UsesAssumedRate ? MarketRateResolver.AssumedFlatRate : null,
            ResolvedIndices = Curves.Keys.Select(inst => inst.ToString()).OrderBy(n => n, StringComparer.Ordinal).ToList(),
            UnrecognizedIndices = UnrecognizedIndices.ToList(),
            IndicesWithoutPoints = IndicesWithoutPoints.ToList(),
        };

        var warnings = new List<string>();
        if (UsesAssumedRate)
            warnings.Add(
                $"No market rate curves were supplied: every floating index resolved to an assumed " +
                $"flat {MarketRateResolver.AssumedFlatRate}%, which is not a rate this request gave. " +
                "A fixed-rate instrument ignores the index and is unaffected; a floating one is " +
                "priced on an assumption.");
        if (UnrecognizedIndices.Count > 0)
            warnings.Add(
                $"Ignored unrecognized index name(s): {string.Join(", ", UnrecognizedIndices)}. " +
                "An instrument referencing one of them resolves to no curve. Legal index names " +
                $"are: {MarketRateResolver.LegalIndexNames}.");
        if (IndicesWithoutPoints.Count > 0)
            warnings.Add(
                "Ignored index name(s) with no usable [monthOffset, rate] point: " +
                $"{string.Join(", ", IndicesWithoutPoints)}.");

        dto.Warnings = warnings.Count > 0 ? warnings : null;
        return dto;
    }
}
