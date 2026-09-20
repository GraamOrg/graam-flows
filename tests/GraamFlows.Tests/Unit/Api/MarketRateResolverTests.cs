using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Api.Validation;
using GraamFlows.Objects.TypeEnum;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     graam-flows#102: the one place a caller's <c>marketRates</c> becomes typed curves.
///
///     Both endpoints used to parse the dictionary inline and <c>continue</c> past anything that
///     did not resolve, so an entry the caller supplied was dropped without a word. These pin what
///     the shared resolver refuses, what it merely reports, and where the line between the two
///     sits — that line is a compatibility decision, not a detail.
/// </summary>
public class MarketRateResolverTests
{
    private static List<double[]> Flat(double rate) => new() { new[] { 0.0, rate } };

    [Fact]
    public void ARequestWithNoCurvesPricesOnTheAssumedRateAndSaysSo()
    {
        // The one fallback #102 keeps: a fixed-rate caller ignores the index entirely, and
        // removing it would break every such request. What changes is that it is now disclosed.
        var resolution = MarketRateResolver.Resolve(null);

        resolution.Error.Should().BeNull("a curve-less request is not an error");
        resolution.UsesAssumedRate.Should().BeTrue();

        var dto = resolution.Describe();
        dto.IndexSource.Should().Be(MarketRateSource.Assumed,
            "a run priced on an assumed index must be distinguishable from a correct one by "
            + "reading the response");
        dto.AssumedRate.Should().Be(5.0);
        dto.Warnings.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SuppliedCurvesThatAllResolveAreNotFlaggedAtAll()
    {
        var resolution = MarketRateResolver.Resolve(new Dictionary<string, List<double[]>>
        {
            ["Libor1M"] = Flat(4.0),
            ["Sofr30Avg"] = Flat(4.3),
        });

        resolution.Error.Should().BeNull();
        resolution.UsesAssumedRate.Should().BeFalse();
        resolution.Curves.Keys.Should().BeEquivalentTo(new[]
            { MarketDataInstEnum.Libor1M, MarketDataInstEnum.Sofr30Avg });

        var dto = resolution.Describe();
        dto.IndexSource.Should().Be(MarketRateSource.Supplied);
        dto.AssumedRate.Should().BeNull();
        dto.Warnings.Should().BeNull("an ordinary run must not cry wolf");
    }

    [Fact]
    public void SuppliedCurvesThatAllFailToResolveAreAnError()
    {
        // The B/C fix. "Curve-less request" is the documented justification for the flat rate,
        // and this request is not curve-less — it asked for something and got a number it never
        // supplied.
        var resolution = MarketRateResolver.Resolve(new Dictionary<string, List<double[]>>
        {
            ["SOFR_3M"] = Flat(4.3),
        });

        resolution.Error.Should().NotBeNull();
        resolution.Error.Should().Contain("SOFR_3M", "the error must name what was rejected");
        resolution.Error.Should().Contain("Sofr30Avg", "and the legal values the caller may use");
    }

    [Fact]
    public void APartialMismatchResolvesWhatLandedAndReportsWhatDidNot()
    {
        // THE COMPATIBILITY LINE. Rejecting here would take down a caller that sends a
        // recognized name alongside an unrecognized one today. The good curve must still drive
        // the run, and the dropped name must be named in the response — which is the half the
        // old code never did.
        var resolution = MarketRateResolver.Resolve(new Dictionary<string, List<double[]>>
        {
            ["Libor1M"] = Flat(4.0),
            ["SOFR_3M"] = Flat(4.3),
        });

        resolution.Error.Should().BeNull(
            "one unrecognized name among good ones must not fail the whole request");
        resolution.Curves.Keys.Should().BeEquivalentTo(new[] { MarketDataInstEnum.Libor1M });

        var dto = resolution.Describe();
        dto.IndexSource.Should().Be(MarketRateSource.Supplied);
        dto.UnrecognizedIndices.Should().ContainSingle().Which.Should().Be("SOFR_3M");
        dto.Warnings.Should().NotBeNullOrEmpty(
            "a dropped entry the caller supplied must be readable from the response");
    }

    [Fact]
    public void ACurveWithNoUsablePointIsNotACurve()
    {
        // A known name carrying nothing resolves to the provider's fallback exactly like a name
        // that was never sent, so it must count as "nothing landed" rather than as a curve.
        var resolution = MarketRateResolver.Resolve(new Dictionary<string, List<double[]>>
        {
            ["Libor1M"] = new(),
        });

        resolution.Error.Should().NotBeNull();
        resolution.Error.Should().Contain("Libor1M");
    }

    [Fact]
    public void AShortRowIsNotAUsablePoint()
    {
        // The waterfall path skipped a row shorter than [offset, rate] and the collateral path
        // dropped it one layer down in CurveRateProvider. Either way a curve of nothing but short
        // rows was indistinguishable from a curve that was never sent.
        var resolution = MarketRateResolver.Resolve(new Dictionary<string, List<double[]>>
        {
            ["Libor1M"] = new() { new[] { 0.0 } },
        });

        resolution.Error.Should().NotBeNull();
    }

    [Theory]
    [InlineData("SOFR_3M")]
    [InlineData("SOFR1M")]
    [InlineData("sofr")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnknownSpellingDoesNotResolve(string name)
    {
        MarketRateResolver.TryParseIndex(name, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("5")]
    [InlineData("999")]
    [InlineData("-1")]
    public void AnOrdinalIsNotAnIndexName(string name)
    {
        // Enum.TryParse answers Swap2Y for "5" and a non-member cast for "999". A caller writing
        // a number is naming a different index than the one they meant.
        MarketRateResolver.TryParseIndex(name, out _).Should().BeFalse(
            "resolving an ordinal silently substitutes one index for another");
    }

    [Fact]
    public void NoneIsNotAnIndexACallerCanSupply()
    {
        // None means "this instrument has no index". Letting it through would put a curve under
        // a key that MarketData has no field for.
        MarketRateResolver.TryParseIndex("None", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("libor1m")]
    [InlineData("LIBOR1M")]
    [InlineData("  Libor1M  ")]
    public void ASpellingThatDiffersOnlyInCaseOrPaddingStillResolves(string name)
    {
        // Case-insensitive parsing predates #102 and callers rely on it; trimming matches the
        // pairing in CalcCollateralController.ParsePrepaymentType.
        MarketRateResolver.TryParseIndex(name, out var inst).Should().BeTrue();
        inst.Should().Be(MarketDataInstEnum.Libor1M);
    }

    [Fact]
    public void EveryIndexTheEngineNamesIsOfferedToTheCaller()
    {
        // The error message is only useful if it is complete, and it is derived from the enum so
        // that it cannot go stale.
        foreach (var inst in Enum.GetValues<MarketDataInstEnum>())
        {
            if (inst == MarketDataInstEnum.None) continue;
            MarketRateResolver.LegalIndexNames.Should().Contain(inst.ToString());
            MarketRateResolver.TryParseIndex(inst.ToString(), out var parsed).Should().BeTrue();
            parsed.Should().Be(inst);
        }
    }
}
