using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using Xunit;

namespace GraamFlows.Tests.Unit.Util;

/// <summary>
///     graam-flows#102: every index this engine names can be stored and read back.
///
///     The API's waterfall path carried its own copy of the index-to-field mapping, and that copy
///     had a case for ten of the twenty-two members — nine swap tenors were missing, so a caller
///     who spelled <c>Swap7Y</c> exactly right parsed the enum, hit no unknown-name path, and had
///     the rate dropped on the floor. <c>MarketData</c>'s fields are bare doubles, so a dropped
///     index reads back 0.0 and a floating note prices at its margin alone.
///
///     The test is written over <c>Enum.GetValues</c> rather than over a list of names on purpose:
///     it is the coverage guarantee itself. A member added to <see cref="MarketDataInstEnum" />
///     without a case on either switch fails here, with no test edit needed to notice.
/// </summary>
public class MarketDataIndexRoundTripTests
{
    public static TheoryData<MarketDataInstEnum> EveryIndex()
    {
        var data = new TheoryData<MarketDataInstEnum>();
        foreach (var inst in Enum.GetValues<MarketDataInstEnum>())
            if (inst != MarketDataInstEnum.None)
                data.Add(inst);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryIndex))]
    public void EveryIndexStoresAndReadsBackItsOwnRate(MarketDataInstEnum inst)
    {
        var md = new MarketData();

        md.SetValueForIndex(inst, 4.25);

        md.ValueForIndex(inst).Should().Be(4.25,
            "an index this engine names must survive being stored — a member with no case on "
            + "either switch is silently 0.0, which prices a floater at its margin alone");
    }

    [Fact]
    public void OneIndexSetDoesNotDisturbAnother()
    {
        // The failure the per-member test cannot see: two cases wired to the same field. Every
        // index gets a distinct rate, and every index must read back its own.
        var md = new MarketData();
        var expected = new Dictionary<MarketDataInstEnum, double>();
        var rate = 1.0;
        foreach (var inst in Enum.GetValues<MarketDataInstEnum>())
        {
            if (inst == MarketDataInstEnum.None) continue;
            rate += 0.125;
            expected[inst] = rate;
            md.SetValueForIndex(inst, rate);
        }

        foreach (var (inst, want) in expected)
            md.ValueForIndex(inst).Should().Be(want,
                "{0} must not share a field with another index", inst);
    }

    [Fact]
    public void AMemberWithNoCaseThrowsRatherThanVanishing()
    {
        // The guard that makes the round-trip test above enforceable in the future: a value that
        // is not a member at all stands in for a member added without a case. It must throw, not
        // quietly do nothing.
        var md = new MarketData();
        var notAMember = (MarketDataInstEnum)9_999;

        var set = () => md.SetValueForIndex(notAMember, 4.25);

        set.Should().Throw<ArgumentException>(
            "silently ignoring an index is exactly the failure #102 is about");
    }

    [Fact]
    public void NoneIsNotAStorableIndex()
    {
        // None means "this instrument has no index" (see CfCore.BuildMarketRateArrays). Storing a
        // rate under it would make the read side answer a number for a fixed-rate instrument.
        var md = new MarketData();

        var set = () => md.SetValueForIndex(MarketDataInstEnum.None, 4.25);

        set.Should().Throw<ArgumentException>();
    }
}
