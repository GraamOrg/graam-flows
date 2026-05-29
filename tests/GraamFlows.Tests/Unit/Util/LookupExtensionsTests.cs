using FluentAssertions;
using GraamFlows.Util.Collections;
using Xunit;

namespace GraamFlows.Tests.Unit.Util;

/// <summary>
/// Regression tests for <see cref="LookupExtensions"/>.
///
/// Reported by harmony issue #1164: the engine's
/// <c>"Sequence contains no matching element"</c> exception text is
/// entirely generic, making name-mismatch bugs (hyphen vs no-hyphen,
/// case differences, stale tranche refs) forensic to debug. These
/// helpers re-raise with the missing key, the available candidates,
/// and a Levenshtein-nearest suggestion so callers see exactly what
/// was being looked up and what was actually there.
/// </summary>
public class LookupExtensionsTests
{
    private record Item(string Name);

    private static readonly List<Item> CapitalStack = new()
    {
        new("A1"),
        new("A2"),
        new("A3"),
        new("M1"),
        new("B1"),
    };

    [Fact]
    public void SingleByName_FoundExactlyOnce_ReturnsMatch()
    {
        var match = CapitalStack.SingleByName(i => i.Name, "M1", "test");
        match.Should().Be(new Item("M1"));
    }

    [Fact]
    public void SingleByName_NotFound_ThrowsWithKeyAndAvailableList()
    {
        // The #1147 reproducer: waterfall references "A-1" but the
        // capital stack is "A1, A2, A3" (no hyphens). The error should
        // name the missing key, the available candidates, and the
        // closest match.
        Action act = () => CapitalStack.SingleByName(i => i.Name, "A-1", "DynamicGroup.Initialize");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("DynamicGroup.Initialize")
                .And.Contain("'A-1' not found")
                .And.Contain("A1").And.Contain("A2").And.Contain("M1")
                .And.Contain("Closest match: 'A1'");
    }

    [Fact]
    public void SingleByName_NullTarget_RendersNullSentinel()
    {
        Action act = () => CapitalStack.SingleByName(i => i.Name, null, "ctx");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("'(null)' not found");
    }

    [Fact]
    public void SingleByName_EmptySource_ReportsEmptyAvailable()
    {
        var empty = new List<Item>();

        Action act = () => empty.SingleByName(i => i.Name, "A1", "ctx");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("'A1' not found")
                .And.Contain("Available: [(empty)]");
    }

    [Fact]
    public void SingleByName_DuplicateMatches_ReportsCount()
    {
        var dupes = new List<Item> { new("A1"), new("A1") };

        Action act = () => dupes.SingleByName(i => i.Name, "A1", "ctx");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("ctx: 'A1' matched 2 candidates");
    }

    [Fact]
    public void SingleByName_CaseInsensitive_FindsMatchWhenComparisonAllows()
    {
        var match = CapitalStack.SingleByName(
            i => i.Name,
            "m1",
            "ctx",
            StringComparison.InvariantCultureIgnoreCase);
        match.Should().Be(new Item("M1"));
    }

    [Fact]
    public void SingleByName_DistantTarget_OmitsClosestMatch()
    {
        // Edit distance from "Z99" to every candidate is >= 2 and the
        // threshold for a useful suggestion is max(2, len/2). For len-3
        // strings the threshold is 2, so "Z99" -> "A1" (distance 3)
        // should not produce a "Closest match" suffix.
        Action act = () => CapitalStack.SingleByName(i => i.Name, "Z99", "ctx");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain("Closest match");
    }

    [Fact]
    public void FirstByName_FoundFirst_ReturnsMatch()
    {
        var dupes = new List<Item> { new("A1"), new("A1") };
        var match = dupes.FirstByName(i => i.Name, "A1", "ctx");
        match.Should().Be(new Item("A1"));
    }

    [Fact]
    public void FirstByName_NotFound_ThrowsWithSameDiagnostic()
    {
        Action act = () => CapitalStack.FirstByName(i => i.Name, "A-2", "RulesHost.COUPON");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("RulesHost.COUPON")
                .And.Contain("'A-2' not found")
                .And.Contain("Closest match: 'A2'");
    }
}
