using System.Globalization;
using GraamFlows.Api.Models;
using GraamFlows.Assumptions;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.Util;

namespace GraamFlows.Api.Validation;

/// <summary>
/// Boundary validation for the assumption block of a <see cref="CalcCollateralRequest"/>.
///
/// Context (graam-harmony #4476): <c>/api/calccollateral</c> accepted any double for
/// cpr/cdr/severity/delinquency/advancing. A user who typed <c>1000</c> into the
/// Prepays field on the deal Scenarios screen sent <c>cpr = 1000</c>; the engine
/// de-annualized it as <c>1 - Pow(1 - 1000/100, 1/12)</c> = <c>Pow(-9, 1/12)</c> = NaN,
/// the amortizer's <c>Math.Clamp(smm, 0, 1)</c> could not clamp it (every comparison
/// against NaN is false), and the endpoint returned <b>200 OK</b> with NaN in every
/// row. The mistake only surfaced one service call later, when
/// <c>WaterfallController.ValidateRequest</c> rejected the cashflows with
/// "Collateral cashflow at index 0 has a non-finite UnscheduledPrincipal value" —
/// an error that names the symptom, comes from the wrong endpoint, and never
/// mentions the input the user actually got wrong.
///
/// This validator rejects the bad input where it enters the system, and the message
/// it returns is what the user literally reads on the deal screen, so each message
/// names the field path, the offending value, the unit convention, and the likely
/// mistake.
///
/// Two properties are load-bearing and easy to break:
///
/// <list type="bullet">
/// <item><b>It validates only what the engine will actually read.</b> The three input
/// shapes are a priority ladder in <c>CalcCollateralController.CreateAssumptions</c> —
/// typed vectors beat PolyPaths strings beat scalars — and <see cref="Validate"/>
/// mirrors that ladder exactly. Naming a shadowed field would point the user at a
/// value the projection never touched. This is not hypothetical: harmony's client
/// sends a synthetic mean scalar alongside every vector it posts, so the shadowed
/// scalar is a number nobody typed.</item>
/// <item><b>It covers every shape.</b> An unvalidated shape is worse after this
/// change than before it, because <c>MathUtil.AnnualPercentToMonthlyHazard</c> now
/// saturates instead of minting NaN — so an out-of-range value that slips past the
/// validator produces a plausible full-prepay projection and a 200 OK, where it used
/// to produce NaN that <c>/api/waterfall</c> would at least catch.</item>
/// </list>
///
/// Returns <c>null</c> when the request is well-formed, mirroring the
/// <c>WaterfallController.ValidateRequest</c> convention.
/// </summary>
public static class AssumptionValidation
{
    /// <summary>Rates that are a share of a balance cannot exceed 100%.</summary>
    private const double RateMax = 100.0;

    /// <summary>
    /// Severity legitimately exceeds 100: liquidation costs can exceed the defaulted
    /// balance, and the engine models that correctly and monotonically
    /// (<c>recovery = defaulted - defaulted * sev</c> simply goes negative — measured
    /// at sev=110 and sev=150, both finite, at 110% and 150% of defaults). Rejecting
    /// it would 400 a valid input and tell an analyst their deliberate 110 was a
    /// mistake. The cap is a generous units-mistake heuristic, not a modelling limit.
    /// </summary>
    private const double SeverityMax = 200.0;

    /// <summary>How many problems to spell out before summarizing the rest.</summary>
    private const int MaxReportedErrors = 6;

    /// <summary>
    /// How many problems to look for before giving up. A loan-level tape can carry
    /// tens of thousands of per-asset overrides; there is no value in walking all of
    /// them once the request is already known to be bad.
    /// </summary>
    private const int MaxCollectedErrors = 25;

    private const string SeverityUnits =
        "Severity is the percentage of a defaulted balance that is lost (40 means a 40% loss severity). "
        + "Values above 100 are allowed — liquidation costs can exceed the balance — but it must be between 0 and 200.";

    private const string DelinquencyUnits =
        "Delinquency is the percentage of the balance that is delinquent, between 0 and 100 (5 means 5% delinquent).";

    // Deliberately states the bound and nothing else. The DTO documents this field as
    // a percent, but CfCore builds it with `divisor: 1.0` where severity and
    // delinquency both use 100.0, so the amortizer consumes it as a FRACTION:
    // measured on a 1,000,000 6% pool at 10% delinquency, advancing=1 gives full
    // advancing (unadvanced interest 0) while advancing=100 — the DTO's own default —
    // yields 54,500 of interest against 5,000 actually due. Until that divisor is
    // fixed (out of scope here, it moves numbers for every DQ>0 run) this message must
    // not tell the reader what 100 means, because both candidate answers are wrong:
    // "100 = full advancing" describes the documented intent the engine does not
    // honour, and "1 = full advancing" would enshrine the bug as the convention.
    private const string AdvancingUnits =
        "Advancing is the servicer's advancing rate on delinquent interest and principal, between 0 and 100.";

    /// <summary>
    /// Validate every assumption the engine will actually read, and report all of the
    /// problems found rather than only the first. The Scenarios screen submits several
    /// boxes at once, so first-error-wins would cost the user one round trip per bad
    /// field.
    /// </summary>
    public static string? Validate(CalcCollateralRequest? request)
    {
        var assumptions = request?.Assumptions;
        if (assumptions == null)
            return null;

        // The declared conventions are resolved first and returned on alone, because
        // they decide what the rate FIELDS mean: cpr under "SMM" is a monthly hazard,
        // not an annual CPR, and a message that called it annual would send the reader
        // the wrong way. There is no useful way to word the rate errors until these
        // are known, so this one check does short-circuit.
        var prepaymentType = RecognizePrepaymentType(assumptions.PrepaymentType);
        if (prepaymentType == null)
            return $"assumptions.prepaymentType = \"{assumptions.PrepaymentType}\" is not a recognized prepayment " +
                   "convention, so the engine cannot tell what assumptions.cpr means. Use \"CPR\" (an annual " +
                   "percentage of the current balance), \"ABS\" (a percentage of the original balance per month), " +
                   "or \"SMM\" (a direct monthly hazard). Omit the field to get CPR.";

        var defaultType = RecognizeDefaultType(assumptions.DefaultType);
        if (defaultType == null)
            return $"assumptions.defaultType = \"{assumptions.DefaultType}\" is not a recognized default " +
                   "convention, so the engine cannot tell what assumptions.cdr means. Use \"CDR\" (an annual " +
                   "percentage of the current balance), \"MDR\" (a direct monthly hazard), or \"ORIGMDR\" (a " +
                   "direct monthly hazard on the original balance). Omit the field to get CDR.";

        var cprUnits = CprUnits(prepaymentType.Value);
        var cdrUnits = CdrUnits(defaultType.Value);

        var fields = new[]
        {
            new RateField("cpr", assumptions.Cpr, assumptions.CprVector, assumptions.CprVectorStr, cprUnits, RateMax),
            new RateField("cdr", assumptions.Cdr, assumptions.CdrVector, assumptions.CdrVectorStr, cdrUnits, RateMax),
            new RateField("severity", assumptions.Severity, assumptions.SeverityVector, assumptions.SeverityVectorStr, SeverityUnits, SeverityMax),
            new RateField("delinquency", assumptions.Delinquency, assumptions.DelinquencyVector, assumptions.DelinquencyVectorStr, DelinquencyUnits, RateMax),
            new RateField("advancing", assumptions.Advancing, assumptions.AdvancingVector, assumptions.AdvancingVectorStr, AdvancingUnits, RateMax),
        };

        // Mirror CreateAssumptions' priority ladder. Priority 1 fires when ANY typed
        // vector is present (and then the PolyPaths strings are ignored wholesale);
        // priority 2 fires when any PolyPaths string is present; otherwise scalars.
        // Within the winning tier, a field without its own vector still falls back to
        // its scalar.
        var hasArrays = fields.Any(f => f.Vector != null);
        var hasVectorStrs = !hasArrays && fields.Any(f => !string.IsNullOrEmpty(f.VectorStr));

        var errors = new List<string>();
        var anchorAbsT = DateUtil.CalcAbsT(request!.ProjectionDate);
        var horizon = ProjectionHorizon(request);

        foreach (var field in fields)
        {
            var path = $"assumptions.{field.Name}";
            if (hasArrays && field.Vector != null)
                CheckVector(errors, $"{path}Vector", field.Vector, field);
            else if (hasVectorStrs && !string.IsNullOrEmpty(field.VectorStr))
                CheckVectorString(errors, $"{path}VectorStr", field.VectorStr!, anchorAbsT, horizon, field);
            else
                Add(errors, Describe(path, field.Scalar, field));
        }

        // Per-asset overrides (graam-flows#5). A loan-level model scores thousands of
        // rows, so the message has to name the asset key — "cpr is out of range" over
        // a 20,000-loan tape is not actionable. BuildAssetAssumptions resolves each
        // rate as vector > scalar > deal-level, and treats an EMPTY array as absent,
        // so the shadowing rule here differs subtly from the deal-level one above.
        if (request.AssetAssumptions != null)
            foreach (var (assetKey, perAsset) in request.AssetAssumptions)
            {
                if (perAsset == null || errors.Count >= MaxCollectedErrors)
                    break;

                var prefix = $"assetAssumptions['{assetKey}']";
                CheckPerAsset(errors, $"{prefix}.cpr", perAsset.CprVector, perAsset.Cpr, fields[0]);
                CheckPerAsset(errors, $"{prefix}.cdr", perAsset.CdrVector, perAsset.Cdr, fields[1]);
                CheckPerAsset(errors, $"{prefix}.severity", perAsset.SeverityVector, perAsset.Severity, fields[2]);
                CheckPerAsset(errors, $"{prefix}.delinquency", perAsset.DelinquencyVector, perAsset.Delinquency, fields[3]);
                CheckPerAsset(errors, $"{prefix}.advancing", perAsset.AdvancingVector, perAsset.Advancing, fields[4]);
            }

        return Summarize(errors);
    }

    private static void CheckPerAsset(
        List<string> errors, string path, double[]? vector, double? scalar, RateField field)
    {
        if (vector is { Length: > 0 })
            CheckVector(errors, $"{path}Vector", vector, field);
        else if (scalar.HasValue)
            Add(errors, Describe(path, scalar.Value, field));
    }

    private static void CheckVector(List<string> errors, string path, double[] values, RateField field)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var error = Describe($"{path}[{i}]", values[i], field);
            if (error != null)
            {
                // One report per field: a bad model run can put every element out of
                // range, and 360 copies of the same sentence helps nobody.
                Add(errors, error);
                return;
            }
        }
    }

    /// <summary>
    /// Validate a PolyPaths vector string ("1000", "1.0R12,6.0", "202301,1.0R12,6.0")
    /// by parsing it exactly the way <c>DealLevelAssumptions.CreateConstAssumptions</c>
    /// does and range-checking the values it actually yields. This shape was the one
    /// hole in the guard: it wins over the scalars whenever it is present, so a
    /// <c>cprVectorStr</c> of "1000" used to sail through and — now that the
    /// de-annualization saturates — produce a 200 OK full-prepay projection with no
    /// error anywhere.
    /// </summary>
    private static void CheckVectorString(
        List<string> errors, string path, string vectorStr, int anchorAbsT, int horizon, RateField field)
    {
        IAnchorableVector parsed;
        try
        {
            parsed = PolyPathsVectorLanguageParser.parseAnchorableVector(vectorStr, 0, null, anchorAbsT);
        }
        catch (Exception ex)
        {
            Add(errors, $"{path} = \"{vectorStr}\" is not a valid vector: {ex.Message} " +
                        "Expected a PolyPaths vector — a plain number (\"6.0\"), a ramp (\"1.0R12,6.0\"), " +
                        "or an anchored ramp (\"202301,1.0R12,6.0\").");
            return;
        }

        for (var period = 0; period < horizon; period++)
        {
            var error = Describe($"{path} (period {period})", parsed.ValueAt(period, anchorAbsT + period), field);
            if (error != null)
            {
                Add(errors, error);
                return;
            }
        }
    }

    /// <summary>
    /// Describe what is wrong with one value, or <c>null</c> if it is acceptable.
    /// </summary>
    private static string? Describe(string path, double value, RateField field)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return $"{path} must be a finite number; got {Format(value)}. {field.Units}";

        if (value < 0.0)
            return $"{path} = {Format(value)} is out of range. {field.Units} " +
                   "A rate here is a share of a balance, so it cannot be negative.";

        if (value > field.Max)
            return $"{path} = {Format(value)} is out of range. {field.Units} " +
                   $"A value above {Format(field.Max)} is usually a units mistake — a PSA speed, basis points, " +
                   "or a fraction that was already multiplied by 100.";

        return null;
    }

    private static void Add(List<string> errors, string? error)
    {
        if (error != null && errors.Count < MaxCollectedErrors)
            errors.Add(error);
    }

    private static string? Summarize(List<string> errors)
    {
        if (errors.Count == 0)
            return null;
        if (errors.Count == 1)
            return errors[0];

        var shown = errors.Take(MaxReportedErrors);
        var hidden = errors.Count - Math.Min(errors.Count, MaxReportedErrors);
        var truncated = errors.Count >= MaxCollectedErrors;

        var suffix = truncated
            ? " (More values may also be out of range; fix these first.)"
            : hidden > 0
                ? $" (And {hidden} more.)"
                : string.Empty;

        return $"{errors.Count} assumption values are invalid. " + string.Join(" ", shown) + suffix;
    }

    /// <summary>
    /// How many periods of a PolyPaths vector to range-check. The engine sizes the
    /// projection off the assets' terms, so a ramp that only goes bad after every loan
    /// has amortized away is not reachable; a year of headroom over the longest term
    /// covers the reachable span, floored and capped so a malformed tape cannot make
    /// this loop expensive.
    /// </summary>
    private static int ProjectionHorizon(CalcCollateralRequest request)
    {
        var maxTerm = 0;
        if (request.Assets != null)
            foreach (var asset in request.Assets)
                if (asset != null && asset.OriginalAmortizationTerm > maxTerm)
                    maxTerm = asset.OriginalAmortizationTerm;

        return Math.Clamp(maxTerm + 12, 360, 1200);
    }

    /// <summary>
    /// What <c>cpr</c> means under each prepayment convention. The field name is the
    /// same in all three cases, so naming the convention is the only way the reader
    /// can tell whether 6 is an annual speed or a monthly hazard.
    /// </summary>
    private static string CprUnits(PrepaymentConvention convention) => convention switch
    {
        PrepaymentConvention.Smm =>
            "With prepaymentType \"SMM\", cpr is a MONTHLY prepayment hazard in percent, between 0 and 100 " +
            "(0.5 means 0.5% of the balance prepays this month) — not an annual CPR.",
        PrepaymentConvention.Abs =>
            "With prepaymentType \"ABS\", cpr is a percentage of the ORIGINAL balance prepaying each month, " +
            "between 0 and 100 (1.5 means 1.5% of the original balance per month).",
        _ =>
            "CPR is an annual percentage between 0 and 100 (6 means 6% CPR).",
    };

    /// <summary>
    /// What <c>cdr</c> means under each default convention — same reasoning as
    /// <see cref="CprUnits"/>.
    /// </summary>
    private static string CdrUnits(DefaultConvention convention) => convention switch
    {
        DefaultConvention.Mdr =>
            "With defaultType \"MDR\", cdr is a MONTHLY default hazard in percent, between 0 and 100 " +
            "(0.05 means 0.05% of the balance defaults this month) — not an annual CDR.",
        DefaultConvention.OrigMdr =>
            "With defaultType \"ORIGMDR\", cdr is a MONTHLY default rate on the ORIGINAL balance, in percent, " +
            "between 0 and 100 (0.05 means 0.05% of the original balance defaults each month) — not an annual CDR.",
        _ =>
            "CDR is an annual percentage between 0 and 100 (0.5 means 0.5% CDR).",
    };

    /// <summary>
    /// Round-trip formatting so the message quotes the value the caller actually sent
    /// (1000, not 1000.0) and renders NaN/Infinity by name.
    /// </summary>
    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private readonly record struct RateField(
        string Name, double Scalar, double[]? Vector, string? VectorStr, string Units, double Max);

    private enum PrepaymentConvention { Cpr, Abs, Smm }

    private enum DefaultConvention { Cdr, Mdr, OrigMdr }

    /// <summary>
    /// Recognize the declared prepayment convention, returning <c>null</c> for a
    /// string the engine does not model.
    ///
    /// This is deliberately stricter than
    /// <c>CalcCollateralController.ParsePrepaymentType</c>, which falls back to CPR for
    /// ANY unrecognized string — so "PSA" or "PercentCPR" used to be modelled silently
    /// as CPR. A census of both consumer repos (graam-harmony, graam-web) for
    /// graam-harmony #4476 found every value actually sent is CPR, ABS or SMM, or the
    /// field omitted, so rejecting the rest breaks no live caller and turns a silent
    /// mis-model into an answerable error.
    ///
    /// Surrounding whitespace is trimmed HERE and in ParsePrepaymentType together —
    /// trimming in only one of the two would be worse than trimming in neither,
    /// because " SMM " would then pass validation and still be modelled as CPR.
    /// </summary>
    private static PrepaymentConvention? RecognizePrepaymentType(string? prepaymentType)
    {
        var value = prepaymentType?.Trim();
        if (string.IsNullOrEmpty(value))
            return PrepaymentConvention.Cpr;
        if (string.Equals(value, "CPR", StringComparison.OrdinalIgnoreCase))
            return PrepaymentConvention.Cpr;
        if (string.Equals(value, "ABS", StringComparison.OrdinalIgnoreCase))
            return PrepaymentConvention.Abs;
        if (string.Equals(value, "SMM", StringComparison.OrdinalIgnoreCase))
            return PrepaymentConvention.Smm;
        return null;
    }

    /// <summary>
    /// Recognize the declared default convention, returning <c>null</c> for a string
    /// the engine does not model. See <see cref="RecognizePrepaymentType"/> for why
    /// this is stricter than the controller's parse and why the trim is paired.
    /// </summary>
    private static DefaultConvention? RecognizeDefaultType(string? defaultType)
    {
        var value = defaultType?.Trim();
        if (string.IsNullOrEmpty(value))
            return DefaultConvention.Cdr;
        if (string.Equals(value, "CDR", StringComparison.OrdinalIgnoreCase))
            return DefaultConvention.Cdr;
        if (string.Equals(value, "MDR", StringComparison.OrdinalIgnoreCase))
            return DefaultConvention.Mdr;
        if (string.Equals(value, "ORIGMDR", StringComparison.OrdinalIgnoreCase))
            return DefaultConvention.OrigMdr;
        return null;
    }
}
