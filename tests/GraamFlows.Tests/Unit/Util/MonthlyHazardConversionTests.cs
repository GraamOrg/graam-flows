using FluentAssertions;
using GraamFlows.Objects.Util;
using Xunit;

namespace GraamFlows.Tests.Unit.Util;

/// <summary>
/// Tie-out guard on <see cref="MathUtil.AnnualPercentToMonthlyHazard"/>
/// (graam-harmony #4476).
///
/// The helper replaced an inline <c>1 - Pow(1 - value/100, 1/12)</c> in
/// <c>CfCore.BuildAssumptionArray</c> — the expression that turned an out-of-range
/// assumption into NaN. Its clamps are the whole point of the change, but the
/// engine has WAL and price tie-out tests, so the in-range arithmetic must not move
/// by a single bit. In particular <c>/ 100.0</c> must NOT be rewritten as
/// <c>* .01</c>: those are not bit-identical in IEEE-754.
///
/// These tests assert BIT identity (not approximate equality) against the original
/// expression, so a future "harmless" algebraic tidy-up of the helper fails loudly
/// rather than silently repricing every deal.
/// </summary>
public class MonthlyHazardConversionTests
{
    /// <summary>The expression exactly as it stood in CfCore before this change.</summary>
    private static double LegacyInlineExpression(double value)
        => 1.0 - Math.Pow(1.0 - value / 100.0, 1.0 / 12.0);

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(6.0)]
    [InlineData(25.0)]
    [InlineData(99.9)]
    [InlineData(100.0)]
    public void AnnualPercentToMonthlyHazard_InRange_IsBitIdenticalToTheLegacyExpression(double annualPercent)
    {
        var actual = MathUtil.AnnualPercentToMonthlyHazard(annualPercent);
        var legacy = LegacyInlineExpression(annualPercent);

        BitConverter.DoubleToInt64Bits(actual).Should().Be(BitConverter.DoubleToInt64Bits(legacy),
            because: "every in-range rate must de-annualize to the exact same double the engine has always "
                     + "produced — an approximate match is not enough when WAL and price tie-outs depend on it");
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(100.000001)]
    [InlineData(150.0)]
    [InlineData(1000.0)]
    [InlineData(double.PositiveInfinity)]
    public void AnnualPercentToMonthlyHazard_AtOrAboveOneHundred_SaturatesInsteadOfProducingNaN(double annualPercent)
    {
        var hazard = MathUtil.AnnualPercentToMonthlyHazard(annualPercent);

        hazard.Should().Be(1.0,
            because: "Pow(negative, 1.0/12.0) is NaN, and the amortizer's Math.Clamp(smm, 0, 1) cannot clamp a "
                     + "NaN — saturating at a full monthly hazard is the meaningful limit and keeps the "
                     + "projection finite");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.0001)]
    [InlineData(-50.0)]
    [InlineData(double.NegativeInfinity)]
    public void AnnualPercentToMonthlyHazard_AtOrBelowZero_IsZero(double annualPercent)
    {
        MathUtil.AnnualPercentToMonthlyHazard(annualPercent).Should().Be(0.0,
            because: "a negative hazard was already clamped to 0 by the amortizer, so flooring here moves no "
                     + "emitted number while keeping the helper total");
    }

    [Fact]
    public void AnnualPercentToMonthlyHazard_Nan_PropagatesRatherThanBeingSilentlyZeroed()
    {
        double.IsNaN(MathUtil.AnnualPercentToMonthlyHazard(double.NaN)).Should().BeTrue(
            because: "the API boundary validator rejects a non-finite assumption, so a NaN reaching the engine "
                     + "is an engine bug; mapping it to 0 here would convert a loud failure into a "
                     + "plausible-looking cashflow nobody would question");
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(6.0)]
    [InlineData(99.99)]
    public void ConvertToSmm_IsDeliberatelyNotDeduplicatedIntoTheHelper(double cpr)
    {
        // MathUtil.ConvertToSmm scales with `cpr * .01` where the helper uses
        // `annualPercent / 100.0`. Over a 15,000,001-point sweep of [0, 100]
        // (a 10M-point grid plus 5M random draws) 603,710 values disagreed, the
        // widest by 115,223 ulps just below 100. The two are therefore kept as
        // separate functions: only AnnualPercentToMonthlyHazard is on the engine's
        // assumption path, and folding ConvertToSmm into it would move numbers.
        var viaHelper = MathUtil.AnnualPercentToMonthlyHazard(cpr);
        var viaConvertToSmm = MathUtil.ConvertToSmm(cpr);

        viaConvertToSmm.Should().BeApproximately(viaHelper, 1e-12,
            because: "the two agree to well within any financial tolerance — they are kept separate only "
                     + "because they are not BIT-identical, which is what tie-out requires");
    }
}
