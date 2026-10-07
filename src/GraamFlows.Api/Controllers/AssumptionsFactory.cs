using GraamFlows.Api.Models;
using GraamFlows.Assumptions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;

namespace GraamFlows.Api.Controllers;

/// <summary>
///     Turns an <see cref="AssumptionsDto" /> into engine assumptions.
///
///     Extracted from <see cref="CalcCollateralController" /> for graam-harmony#5577. The
///     waterfall endpoint needs the SAME conversion to project reinvested cohorts, and the
///     alternative - a second copy there - is how the two would drift: a prepayment mode
///     parsed one way for a posted pool and another way for the collateral bought against
///     it would stay invisible until someone tied the two out.
/// </summary>
internal static class AssumptionsFactory
{
    internal static DealLevelAssumptions CreateAssumptions(DateTime projectionDate, int anchorAbsT, AssumptionsDto dto)
    {
        // Priority 1: Per-period arrays (e.g., cdrVector: [10.4, 9.8, 8.2, ...])
        var hasArrays = dto.CprVector != null || dto.CdrVector != null ||
                        dto.SeverityVector != null || dto.DelinquencyVector != null ||
                        dto.AdvancingVector != null;

        if (hasArrays)
        {
            var vpr = dto.CprVector != null
                ? new ArrayVector(anchorAbsT, dto.CprVector)
                : (IAnchorableVector)new ConstVector(anchorAbsT, dto.Cpr);
            var cdr = dto.CdrVector != null
                ? new ArrayVector(anchorAbsT, dto.CdrVector)
                : (IAnchorableVector)new ConstVector(anchorAbsT, dto.Cdr);
            var sev = dto.SeverityVector != null
                ? new ArrayVector(anchorAbsT, dto.SeverityVector)
                : (IAnchorableVector)new ConstVector(anchorAbsT, dto.Severity);
            var delinq = dto.DelinquencyVector != null
                ? new ArrayVector(anchorAbsT, dto.DelinquencyVector)
                : (IAnchorableVector)new ConstVector(anchorAbsT, dto.Delinquency);
            var adv = dto.AdvancingVector != null
                ? new ArrayVector(anchorAbsT, dto.AdvancingVector)
                : (IAnchorableVector)new ConstVector(anchorAbsT, dto.Advancing);

            var prepayType = ParsePrepaymentType(dto.PrepaymentType);
            var defaultType = ParseDefaultType(dto.DefaultType);
            var delinqType = prepayType == PrepaymentTypeEnum.ABS
                ? DelinqRateTypeEnum.PctOrigBal
                : DelinqRateTypeEnum.PctCurrBal;

            var assetAssumps = new AssetAssumptions(prepayType, vpr,
                defaultType, cdr, sev,
                delinqType, delinq, adv, adv);
            return new DealLevelAssumptions(projectionDate, assetAssumps)
            {
                WeightedAverageRemainingTerm = dto.Wam
            };
        }

        // Priority 2: PolyPaths format strings (legacy)
        var hasVectorStrs = !string.IsNullOrEmpty(dto.CprVectorStr) ||
                            !string.IsNullOrEmpty(dto.CdrVectorStr) ||
                            !string.IsNullOrEmpty(dto.SeverityVectorStr) ||
                            !string.IsNullOrEmpty(dto.DelinquencyVectorStr) ||
                            !string.IsNullOrEmpty(dto.AdvancingVectorStr);

        if (hasVectorStrs)
        {
            var vprStr = dto.CprVectorStr ?? dto.Cpr.ToString();
            var cdrStr = dto.CdrVectorStr ?? dto.Cdr.ToString();
            var sevStr = dto.SeverityVectorStr ?? dto.Severity.ToString();
            var dqStr = dto.DelinquencyVectorStr ?? dto.Delinquency.ToString();
            var advStr = dto.AdvancingVectorStr ?? dto.Advancing.ToString();

            return DealLevelAssumptions.CreateConstAssumptions(
                projectionDate, anchorAbsT, vprStr, cdrStr, sevStr, dqStr, advStr);
        }

        // Priority 3: Scalar values
        if (string.Equals(dto.PrepaymentType?.Trim(), "ABS", StringComparison.OrdinalIgnoreCase))
        {
            return DealLevelAssumptions.CreateAbsAssumptions(
                projectionDate, anchorAbsT,
                dto.Cpr, dto.Cdr, dto.Severity, dto.Delinquency, 0, dto.Wam);
        }

        var scalarPrepayType = ParsePrepaymentType(dto.PrepaymentType);
        var scalarDefaultType = ParseDefaultType(dto.DefaultType);

        // Direct-monthly hazards (SMM/MDR) can't flow through the CPR/CDR
        // CreateConstAssumptions helper without being de-annualized, so build
        // the AssetAssumptions explicitly when either mode is requested.
        if (scalarPrepayType == PrepaymentTypeEnum.SMM ||
            scalarDefaultType == DefaultTypeEnum.MDR ||
            scalarDefaultType == DefaultTypeEnum.ORIGMDR)
        {
            var assetAssumps = new AssetAssumptions(
                scalarPrepayType, new ConstVector(anchorAbsT, dto.Cpr),
                scalarDefaultType, new ConstVector(anchorAbsT, dto.Cdr),
                new ConstVector(anchorAbsT, dto.Severity),
                DelinqRateTypeEnum.PctCurrBal, new ConstVector(anchorAbsT, dto.Delinquency),
                new ConstVector(anchorAbsT, dto.Advancing), new ConstVector(anchorAbsT, dto.Advancing));
            return new DealLevelAssumptions(projectionDate, assetAssumps);
        }

        return DealLevelAssumptions.CreateConstAssumptions(
            projectionDate, anchorAbsT,
            dto.Cpr, dto.Cdr, dto.Severity, dto.Delinquency, dto.Advancing);
    }

    // The Trim() here is paired with the one in AssumptionValidation: the validator
    // accepts " SMM " as SMM, so this must resolve it to SMM too. Trimming in only one
    // of the two would be worse than trimming in neither — a padded string would pass
    // validation and then be silently modelled as CPR (graam-harmony #4476).
    internal static PrepaymentTypeEnum ParsePrepaymentType(string? prepaymentType)
    {
        var value = prepaymentType?.Trim();
        if (string.Equals(value, "ABS", StringComparison.OrdinalIgnoreCase))
            return PrepaymentTypeEnum.ABS;
        if (string.Equals(value, "SMM", StringComparison.OrdinalIgnoreCase))
            return PrepaymentTypeEnum.SMM;
        return PrepaymentTypeEnum.CPR;
    }

    /// <summary>See <see cref="ParsePrepaymentType"/> for why the trim is paired.</summary>
    internal static DefaultTypeEnum ParseDefaultType(string? defaultType)
    {
        var value = defaultType?.Trim();
        if (string.Equals(value, "MDR", StringComparison.OrdinalIgnoreCase))
            return DefaultTypeEnum.MDR;
        if (string.Equals(value, "ORIGMDR", StringComparison.OrdinalIgnoreCase))
            return DefaultTypeEnum.ORIGMDR;
        return DefaultTypeEnum.CDR;
    }

}
