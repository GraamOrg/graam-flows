using System.Globalization;
using GraamFlows.Api.Models;

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
/// it returns is what the user literally reads on the deal screen (harmony now
/// surfaces the engine's sentence rather than its JSON envelope), so each message
/// names the field path, the offending value, the unit convention, and the likely
/// mistake.
///
/// Returns <c>null</c> when the request is well-formed, mirroring the
/// <c>WaterfallController.ValidateRequest</c> convention.
/// </summary>
public static class AssumptionValidation
{
    private const double MinRate = 0.0;
    private const double MaxRate = 100.0;

    private const string SeverityUnits =
        "Severity is the percentage of a defaulted balance that is lost, between 0 and 100 (40 means a 40% loss severity).";

    private const string DelinquencyUnits =
        "Delinquency is the percentage of the balance that is delinquent, between 0 and 100 (5 means 5% delinquent).";

    private const string AdvancingUnits =
        "Advancing is the percentage of delinquent interest and principal the servicer advances, between 0 and 100 (100 means full advancing, 0 means none).";

    private const string AboveRangeHint =
        "A value above 100 is usually a units mistake — a PSA speed, basis points, or a fraction that was already multiplied by 100.";

    private const string BelowRangeHint =
        "A rate here is a share of a balance, so it cannot be negative.";

    /// <summary>
    /// Validate every assumption a <see cref="CalcCollateralRequest"/> carries — the
    /// deal-level scalars, their vector forms, and every per-asset override. Returns
    /// the first problem as a human-readable sentence, or <c>null</c> if all values
    /// are finite and within [0, 100].
    /// </summary>
    public static string? Validate(CalcCollateralRequest? request)
    {
        var assumptions = request?.Assumptions;
        if (assumptions == null)
            return null;

        // The declared conventions are resolved first, because they decide what the
        // rate FIELDS mean: cpr under "SMM" is a monthly hazard, not an annual CPR,
        // and a message that called it annual would send the reader the wrong way.
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

        var error =
            CheckScalar("assumptions.cpr", assumptions.Cpr, cprUnits)
            ?? CheckScalar("assumptions.cdr", assumptions.Cdr, cdrUnits)
            ?? CheckScalar("assumptions.severity", assumptions.Severity, SeverityUnits)
            ?? CheckScalar("assumptions.delinquency", assumptions.Delinquency, DelinquencyUnits)
            ?? CheckScalar("assumptions.advancing", assumptions.Advancing, AdvancingUnits)
            ?? CheckVector("assumptions.cprVector", assumptions.CprVector, cprUnits)
            ?? CheckVector("assumptions.cdrVector", assumptions.CdrVector, cdrUnits)
            ?? CheckVector("assumptions.severityVector", assumptions.SeverityVector, SeverityUnits)
            ?? CheckVector("assumptions.delinquencyVector", assumptions.DelinquencyVector, DelinquencyUnits)
            ?? CheckVector("assumptions.advancingVector", assumptions.AdvancingVector, AdvancingUnits);
        if (error != null)
            return error;

        if (request!.AssetAssumptions == null)
            return null;

        // Per-asset overrides (graam-flows#5). A loan-level model scores thousands of
        // rows, so the message has to name the asset key — "cpr is out of range" over
        // a 20,000-loan tape is not actionable.
        foreach (var (assetKey, perAsset) in request.AssetAssumptions)
        {
            if (perAsset == null)
                continue;

            var prefix = $"assetAssumptions['{assetKey}']";
            error =
                CheckOptionalScalar($"{prefix}.cpr", perAsset.Cpr, cprUnits)
                ?? CheckOptionalScalar($"{prefix}.cdr", perAsset.Cdr, cdrUnits)
                ?? CheckOptionalScalar($"{prefix}.severity", perAsset.Severity, SeverityUnits)
                ?? CheckOptionalScalar($"{prefix}.delinquency", perAsset.Delinquency, DelinquencyUnits)
                ?? CheckOptionalScalar($"{prefix}.advancing", perAsset.Advancing, AdvancingUnits)
                ?? CheckVector($"{prefix}.cprVector", perAsset.CprVector, cprUnits)
                ?? CheckVector($"{prefix}.cdrVector", perAsset.CdrVector, cdrUnits)
                ?? CheckVector($"{prefix}.severityVector", perAsset.SeverityVector, SeverityUnits)
                ?? CheckVector($"{prefix}.delinquencyVector", perAsset.DelinquencyVector, DelinquencyUnits)
                ?? CheckVector($"{prefix}.advancingVector", perAsset.AdvancingVector, AdvancingUnits);
            if (error != null)
                return error;
        }

        return null;
    }

    private static string? CheckScalar(string path, double value, string units)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return $"{path} must be a finite number; got {Format(value)}. {units}";

        if (value < MinRate || value > MaxRate)
            return $"{path} = {Format(value)} is out of range. {units} " +
                   (value > MaxRate ? AboveRangeHint : BelowRangeHint);

        return null;
    }

    private static string? CheckOptionalScalar(string path, double? value, string units)
        => value.HasValue ? CheckScalar(path, value.Value, units) : null;

    private static string? CheckVector(string path, double[]? values, string units)
    {
        if (values == null)
            return null;

        for (var i = 0; i < values.Length; i++)
        {
            var error = CheckScalar($"{path}[{i}]", values[i], units);
            if (error != null)
                return error;
        }

        return null;
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
    /// </summary>
    private static PrepaymentConvention? RecognizePrepaymentType(string? prepaymentType)
    {
        if (string.IsNullOrWhiteSpace(prepaymentType))
            return PrepaymentConvention.Cpr;
        if (string.Equals(prepaymentType, "CPR", StringComparison.OrdinalIgnoreCase))
            return PrepaymentConvention.Cpr;
        if (string.Equals(prepaymentType, "ABS", StringComparison.OrdinalIgnoreCase))
            return PrepaymentConvention.Abs;
        if (string.Equals(prepaymentType, "SMM", StringComparison.OrdinalIgnoreCase))
            return PrepaymentConvention.Smm;
        return null;
    }

    /// <summary>
    /// Recognize the declared default convention, returning <c>null</c> for a string
    /// the engine does not model. See <see cref="RecognizePrepaymentType"/> for why
    /// this is stricter than the controller's parse.
    /// </summary>
    private static DefaultConvention? RecognizeDefaultType(string? defaultType)
    {
        if (string.IsNullOrWhiteSpace(defaultType))
            return DefaultConvention.Cdr;
        if (string.Equals(defaultType, "CDR", StringComparison.OrdinalIgnoreCase))
            return DefaultConvention.Cdr;
        if (string.Equals(defaultType, "MDR", StringComparison.OrdinalIgnoreCase))
            return DefaultConvention.Mdr;
        if (string.Equals(defaultType, "ORIGMDR", StringComparison.OrdinalIgnoreCase))
            return DefaultConvention.OrigMdr;
        return null;
    }
}
