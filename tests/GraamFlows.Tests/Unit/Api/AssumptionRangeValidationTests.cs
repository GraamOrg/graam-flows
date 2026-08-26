using System.Globalization;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
/// Boundary validation for <c>/api/calccollateral</c> assumptions (graam-harmony #4476).
///
/// Context: a user typed <c>1000</c> into the Prepays (CPR) field on the deal
/// Scenarios screen. The endpoint accepted it with no validation of any kind;
/// <c>CfCore.BuildAssumptionArray</c> de-annualized it as
/// <c>1 - Pow(1 - 1000/100, 1/12)</c> = <c>Pow(-9, 1/12)</c> = NaN; the amortizer's
/// <c>Math.Clamp(smm, 0, 1)</c> did not clamp it (every comparison against NaN is
/// false); and because <c>Program.cs</c> enables
/// <c>JsonNumberHandling.AllowNamedFloatingPointLiterals</c>, the endpoint returned
/// <b>200 OK with NaN in every row</b>. The mistake only surfaced one service call
/// later, when <c>/api/waterfall</c> rejected the cashflows for a non-finite
/// UnscheduledPrincipal — an error naming the symptom, from the wrong endpoint,
/// that never mentioned the input the user got wrong.
///
/// These tests pin that the request is now rejected where it enters the system, and
/// — because harmony surfaces the engine's sentence verbatim to the user — that each
/// message names the field path, the offending value and the bound, not merely that
/// something was invalid. They also guard that the legal range is untouched: 0 and
/// 100 are valid, and an ordinary request still succeeds.
/// </summary>
public class AssumptionRangeValidationTests
{
    private static AssetDto Pool() => new()
    {
        AssetName = "Pool_Aggregate",
        AssetId = "Pool_Aggregate",
        InterestRateType = "FRM",
        OriginalDate = new DateTime(2024, 1, 1),
        OriginalBalance = 100_000_000.0,
        CurrentBalance = 100_000_000.0,
        OriginalInterestRate = 6.0,
        CurrentInterestRate = 6.0,
        OriginalAmortizationTerm = 360,
        ServiceFee = 0.0,
        GroupNum = "1",
        IsIO = false,
    };

    private static CalcCollateralRequest Request(AssumptionsDto assumptions) => new()
    {
        Assets = new List<AssetDto> { Pool() },
        ProjectionDate = new DateTime(2025, 1, 1),
        Assumptions = assumptions,
    };

    private static AssumptionsDto Sane() => new()
    {
        Cpr = 6.0, Cdr = 0.5, Severity = 40.0, Delinquency = 0.0, Advancing = 0.0,
    };

    private static ActionResult<CalcCollateralResponse> Calculate(CalcCollateralRequest request)
        => new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(request);

    /// <summary>
    /// Pull the message out of the <c>{ error = "..." }</c> payload. That payload is an
    /// anonymous type (its shape is parsed by a downstream Python client, so it must
    /// stay exactly one <c>error</c> key), so it is reached by reflection.
    /// </summary>
    private static string RejectionMessage(ActionResult<CalcCollateralResponse> result, string because)
    {
        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>(because).Subject;
        var payload = bad.Value.Should().NotBeNull().And.Subject;
        var errorProperty = payload!.GetType().GetProperty("error");
        errorProperty.Should().NotBeNull("the rejection payload must keep its single `error` key — a downstream Python client parses it");
        return errorProperty!.GetValue(payload)!.ToString()!;
    }

    [Fact]
    public void Calculate_CprAboveOneHundred_RejectsNamingFieldValueAndBound()
    {
        var assumptions = Sane();
        assumptions.Cpr = 1000;

        var message = RejectionMessage(
            Calculate(Request(assumptions)),
            because: "before this fix the identical request returned 200 OK with NaN in every cashflow row, "
                     + "and the user only saw an error two service calls later from /api/waterfall");

        message.Should().Contain("assumptions.cpr",
            because: "the message must name the field the user actually got wrong, not the downstream symptom");
        message.Should().Contain("1000",
            because: "the message must quote the offending value back so the user recognizes what they typed");
        message.Should().Contain("100",
            because: "the message must state the bound that was violated");
        message.Should().Contain("annual",
            because: "the message must state the unit convention — the whole mistake is a units mistake");
        message.Should().NotContain("NaN",
            because: "the user never produced a NaN; they typed 1000, and that is what the message must be about");
    }

    [Theory]
    [InlineData("cdr")]
    [InlineData("severity")]
    [InlineData("delinquency")]
    [InlineData("advancing")]
    public void Calculate_ScalarAboveOneHundred_RejectsNamingThatField(string field)
    {
        var assumptions = Sane();
        switch (field)
        {
            case "cdr": assumptions.Cdr = 250; break;
            case "severity": assumptions.Severity = 250; break;
            case "delinquency": assumptions.Delinquency = 250; break;
            case "advancing": assumptions.Advancing = 250; break;
        }

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: $"{field} is a percentage and 250 is outside [0, 100]");

        message.Should().Contain($"assumptions.{field}",
            because: "each rate field must be named individually so the user knows which input to change");
        message.Should().Contain("250", because: "the message must quote the offending value");
        message.Should().Contain("100", because: "the message must state the bound");
    }

    [Fact]
    public void Calculate_NegativeScalar_RejectsNamingFieldAndValue()
    {
        var assumptions = Sane();
        assumptions.Severity = -5;

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "a share of a balance cannot be negative");

        message.Should().Contain("assumptions.severity").And.Contain("-5");
        message.Should().Contain("negative",
            because: "the hint for a negative rate must differ from the units hint for an above-100 rate");
    }

    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "Infinity")]
    public void Calculate_NonFiniteScalar_RejectsAsNonFinite(double value, string rendered)
    {
        var assumptions = Sane();
        assumptions.Cpr = value;

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "a non-finite assumption is exactly what used to poison every emitted period");

        message.Should().Contain("assumptions.cpr").And.Contain("finite").And.Contain(rendered,
            because: "the message must say which field was non-finite and what it was");
    }

    [Fact]
    public void Calculate_VectorElementOutOfRange_RejectsNamingTheIndex()
    {
        var assumptions = Sane();
        assumptions.CdrVector = new[] { 0.5, 0.6, 0.7, 4000.0, 0.8 };

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "a per-period vector is validated element by element");

        message.Should().Contain("assumptions.cdrVector[3]",
            because: "over a 360-element vector the message is only actionable if it names the offending index");
        message.Should().Contain("4000", because: "the message must quote the offending value");
    }

    [Fact]
    public void Calculate_NonFiniteVectorElement_RejectsNamingTheIndex()
    {
        var assumptions = Sane();
        assumptions.CprVector = new[] { 6.0, double.NaN };

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "a NaN in a model-scored vector is the other route to non-finite collateral");

        message.Should().Contain("assumptions.cprVector[1]").And.Contain("finite").And.Contain("NaN");
    }

    [Fact]
    public void Calculate_PerAssetOverrideOutOfRange_RejectsNamingTheAssetKey()
    {
        var request = Request(Sane());
        request.AssetAssumptions = new Dictionary<string, AssetAssumptionDto>
        {
            ["LOAN-000042"] = new() { Cdr = 900.0 },
        };

        var message = RejectionMessage(Calculate(request),
            because: "per-asset overrides (graam-flows#5) bypass the deal-level values and need the same guard");

        message.Should().Contain("LOAN-000042",
            because: "over a loan-level tape of thousands of rows, only the asset key makes the error findable");
        message.Should().Contain("cdr").And.Contain("900");
    }

    [Fact]
    public void Calculate_PerAssetVectorOutOfRange_RejectsNamingAssetKeyAndIndex()
    {
        var request = Request(Sane());
        request.AssetAssumptions = new Dictionary<string, AssetAssumptionDto>
        {
            // 240, not 140: severity legitimately exceeds 100 (liquidation costs), so
            // the bound for this field is 200.
            ["LOAN-000042"] = new() { SeverityVector = new[] { 40.0, 40.0, 240.0 } },
        };

        var message = RejectionMessage(Calculate(request),
            because: "per-asset vectors are the densest input surface and the easiest place to hide a bad value");

        message.Should().Contain("LOAN-000042").And.Contain("severityVector[2]").And.Contain("240");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(100.0)]
    public void Calculate_BoundaryValues_AreAccepted(double boundary)
    {
        var assumptions = Sane();
        assumptions.Cpr = boundary;
        assumptions.Cdr = boundary;
        assumptions.Severity = boundary;
        // Not hardcoded to 0: delinquency = 0 is the single assignment that masks the
        // advancing divisor bug (CfCore builds delAdvInt with divisor 1.0 where
        // severity and delinquency use 100.0), and pinning it here would have let this
        // test look like evidence that advancing = 100 is sound. It is not — see the
        // follow-up noted on graam-harmony #4476. This test's claim is narrower: the
        // RANGE is inclusive at both ends.
        assumptions.Delinquency = boundary;
        assumptions.Advancing = boundary;

        var result = Calculate(Request(assumptions));

        result.Result.Should().BeOfType<OkObjectResult>(
            $"{boundary} is a legal rate — the range is inclusive, and rejecting it would be a regression "
            + "(100% CPR and 100% severity are both meaningful stresses)");
    }

    [Fact]
    public void Calculate_OrdinaryAssumptions_StillProduceCashflows()
    {
        var result = Calculate(Request(Sane()));

        var ok = result.Result.Should().BeOfType<OkObjectResult>(
            "the guard must not cost a normal request its 200").Subject;
        var response = ok.Value.Should().BeOfType<CalcCollateralResponse>().Subject;
        response.Cashflows.Should().NotBeEmpty("a valid request must still project collateral");
        response.Cashflows.Should().OnlyContain(cf => !double.IsNaN(cf.UnscheduledPrincipal),
            because: "no valid request should ever emit a non-finite cashflow");
    }

    [Fact]
    public void Calculate_SmmPrepaymentType_DescribesCprAsMonthlyNotAnnual()
    {
        var assumptions = Sane();
        assumptions.PrepaymentType = "SMM";
        assumptions.Cpr = 1000;

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "an out-of-range value is out of range under every convention");

        message.Should().Contain("MONTHLY",
            because: "under prepaymentType SMM the cpr field is a monthly hazard, and telling the user it is "
                     + "an annual CPR would send them to fix the wrong thing");
        message.Should().NotContain("CPR is an annual percentage",
            because: "the annual-CPR wording belongs only to the CPR convention");
    }

    [Fact]
    public void Calculate_OrigMdrDefaultType_DescribesCdrAsMonthlyOnOriginalBalance()
    {
        var assumptions = Sane();
        assumptions.DefaultType = "ORIGMDR";
        assumptions.Cdr = 150;

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "the cdr field means a different thing under each default convention");

        message.Should().Contain("MONTHLY").And.Contain("ORIGINAL",
            because: "ORIGMDR is a monthly rate on the ORIGINAL balance, and the message must say so");
    }

    [Fact]
    public void Calculate_UnknownPrepaymentType_IsRejectedRatherThanSilentlyModelledAsCpr()
    {
        var assumptions = Sane();
        assumptions.PrepaymentType = "PSA";

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "ParsePrepaymentType falls back to CPR for ANY unrecognized string, so a PSA speed used to "
                     + "be silently modelled as a CPR — a wrong answer with no error at all");

        message.Should().Contain("prepaymentType").And.Contain("PSA");
        message.Should().Contain("CPR").And.Contain("ABS").And.Contain("SMM",
            because: "a rejection must list the conventions the engine does model");
    }

    [Fact]
    public void Calculate_UnknownDefaultType_IsRejectedRatherThanSilentlyModelledAsCdr()
    {
        var assumptions = Sane();
        assumptions.DefaultType = "SDA";

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "ParseDefaultType has the same fall-back-to-CDR behaviour");

        message.Should().Contain("defaultType").And.Contain("SDA");
        message.Should().Contain("CDR").And.Contain("MDR").And.Contain("ORIGMDR");
    }

    [Theory]
    [InlineData("CPR")]
    [InlineData("cpr")]
    [InlineData("ABS")]
    [InlineData("smm")]
    [InlineData("")]
    [InlineData(null)]
    public void Calculate_RecognizedPrepaymentType_IsAccepted(string? prepaymentType)
    {
        var assumptions = Sane();
        assumptions.PrepaymentType = prepaymentType!;

        var result = Calculate(Request(assumptions));

        result.Result.Should().BeOfType<OkObjectResult>(
            "every convention the engine models — in any casing, and the omitted default — must still be accepted; "
            + "a census of graam-harmony and graam-web found these are the only values ever sent");
    }

    // ---- PolyPaths string vectors (the Priority-2 shape) ----

    [Theory]
    [InlineData("1000")]
    [InlineData("1000R12,6.0")]
    [InlineData("202501,1000R12,6.0")]
    public void Calculate_PolyPathsStringVectorOutOfRange_IsRejected(string vectorStr)
    {
        var assumptions = Sane();
        assumptions.CprVectorStr = vectorStr;

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "the *VectorStr shape WINS over the scalars whenever it is present, so leaving it "
                     + "unvalidated left the whole guard bypassable — and post-fix it is worse than pre-fix, "
                     + "because the saturating de-annualization turns it into a plausible 200 OK full-prepay "
                     + "projection instead of the NaN that /api/waterfall would at least have caught");

        message.Should().Contain("cprVectorStr").And.Contain("1000").And.Contain("100");
    }

    [Fact]
    public void Calculate_PolyPathsStringVectorGoesBadLater_NamesTheOffendingPeriod()
    {
        var assumptions = Sane();
        // Starts in range and ramps out of it, so only a per-period check catches it.
        assumptions.CdrVectorStr = "1.0R6,900.0";

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "a ramp can be in range at period 0 and far out of it later");

        message.Should().Contain("cdrVectorStr").And.Contain("period",
            because: "naming the period is the string-vector equivalent of naming the array index");
    }

    [Theory]
    [InlineData("6.0")]
    [InlineData("1.0R12,6.0")]
    [InlineData("100")]
    public void Calculate_PolyPathsStringVectorInRange_IsAccepted(string vectorStr)
    {
        var assumptions = Sane();
        assumptions.CprVectorStr = vectorStr;

        Calculate(Request(assumptions)).Result.Should().BeOfType<OkObjectResult>(
            "the legacy PolyPaths shape must keep working — validating it must not amount to banning it");
    }

    [Fact]
    public void Calculate_UnparseablePolyPathsString_IsRejectedWithTheExpectedFormat()
    {
        var assumptions = Sane();
        assumptions.CprVectorStr = "not-a-vector";

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "an unparseable vector used to surface as a raw parser exception message");

        message.Should().Contain("cprVectorStr").And.Contain("PolyPaths",
            because: "the message must show the reader what shape was expected");
    }

    // ---- shadowing: only validate what the engine will actually read ----

    [Fact]
    public void Calculate_VectorPresent_MessageNamesTheVectorNotTheShadowedScalar()
    {
        var assumptions = Sane();
        // Exactly what harmony's client sends for a list-valued assumption: the vector
        // plus a synthetic mean scalar that nobody typed. Both are out of range here.
        assumptions.CprVector = new[] { 20.0, 40.0, 900.0 };
        assumptions.Cpr = (20.0 + 40.0 + 900.0) / 3.0; // 320, the client's "scalar fallback (average)"

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "CreateAssumptions' Priority-1 branch uses CprVector and ignores Cpr entirely");

        message.Should().Contain("cprVector[2]").And.Contain("900",
            because: "the offending value is the vector element the engine will actually read");
        message.Should().NotContain("assumptions.cpr =",
            because: "the paired scalar is a mean harmony synthesized and the projection never reads it — "
                     + "naming it would point the user at a field they never filled in");
    }

    [Fact]
    public void Calculate_VectorInRangeButShadowedScalarIsNot_IsAccepted()
    {
        var assumptions = Sane();
        assumptions.CprVector = new[] { 6.0, 6.0, 6.0 };
        assumptions.Cpr = 900.0; // shadowed: never read

        Calculate(Request(assumptions)).Result.Should().BeOfType<OkObjectResult>(
            "a value the engine never reads cannot make the projection wrong, and rejecting it would 400 a "
            + "request that would have computed correctly");
    }

    [Fact]
    public void Calculate_TypedVectorPresent_ShadowsThePolyPathsStringEntirely()
    {
        var assumptions = Sane();
        assumptions.CprVector = new[] { 6.0, 6.0 };
        assumptions.CprVectorStr = "9000"; // Priority 1 beats Priority 2: never parsed

        Calculate(Request(assumptions)).Result.Should().BeOfType<OkObjectResult>(
            "hasArrays wins over hasVectorStrs in CreateAssumptions, so the string is dead input here — the "
            + "validator mirrors the engine's priority ladder rather than checking every field it can see");
    }

    // ---- severity above 100 is legitimate ----

    [Theory]
    [InlineData(110.0)]
    [InlineData(150.0)]
    [InlineData(200.0)]
    public void Calculate_SeverityAboveOneHundred_IsAccepted(double severity)
    {
        var assumptions = Sane();
        assumptions.Cdr = 10.0;
        assumptions.Severity = severity;

        Calculate(Request(assumptions)).Result.Should().BeOfType<OkObjectResult>(
            "severity above 100 is the standard liquidation-costs-exceed-balance convention and the engine "
            + "models it correctly and monotonically (measured: sev=110 gives recovery -874.16 and loss at "
            + "110% of defaults, finite); rejecting it would 400 a valid input and tell an analyst their "
            + "deliberate 110 was a units mistake");
    }

    [Fact]
    public void Calculate_SeverityFarAboveOneHundred_IsStillRejected()
    {
        var assumptions = Sane();
        assumptions.Severity = 4000;

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "the widened bound is a units-mistake heuristic, not an open door");

        message.Should().Contain("assumptions.severity").And.Contain("4000").And.Contain("200",
            because: "the message must quote the bound that actually applies to this field, not a generic 100");
    }

    // ---- all problems at once ----

    [Fact]
    public void Calculate_SeveralBadFields_ReportsAllOfThemInOneResponse()
    {
        var assumptions = Sane();
        assumptions.Cpr = 1000;
        assumptions.Cdr = 500;
        assumptions.Severity = 4000;

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "the Scenarios screen submits several boxes at once");

        message.Should().Contain("assumptions.cpr").And.Contain("assumptions.cdr").And.Contain("assumptions.severity",
            because: "first-error-wins would cost the user one round trip per bad field, which is the opposite "
                     + "of what a PR about actionable messages should ship");
        message.Should().Contain("3", because: "the summary must say how many problems were found");
    }

    // ---- whitespace ----

    [Theory]
    [InlineData(" SMM ")]
    [InlineData("smm\t")]
    public void Calculate_PaddedPrepaymentType_IsAcceptedAndStillResolvesToSmm(string prepaymentType)
    {
        var assumptions = Sane();
        assumptions.PrepaymentType = prepaymentType;
        assumptions.Cpr = 1000;

        var message = RejectionMessage(Calculate(Request(assumptions)),
            because: "padding should not change what a convention means");

        message.Should().Contain("MONTHLY",
            because: "the trim in AssumptionValidation is paired with the one in ParsePrepaymentType — if only "
                     + "the validator trimmed, ' SMM ' would pass validation and then be modelled as CPR, "
                     + "which is worse than rejecting it outright");
    }
}
