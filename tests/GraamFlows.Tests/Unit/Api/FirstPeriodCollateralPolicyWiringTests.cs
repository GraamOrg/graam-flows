using System.Reflection;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     The DTO seam. `CollateralAccrualStartDate` and `FirstPeriodCollateralPolicy` decide
///     whether a deal's pre-first-pay collateral is spent or excluded, and the only way a
///     caller can state either is through this mapping.
///
///     It went unpinned once already: the API controller copied both fields and the CLI
///     runner — reading the SAME DealDto — copied neither, so a caller who correctly stated
///     a policy on the CLI path had it silently discarded and ran the default. Deleting both
///     controller lines left the whole suite green. A field that only one of two readers
///     maps is worse than one neither maps, because the request looks honoured.
///
///     Reflection rather than production visibility: `BuildDeal` is private static on both,
///     and widening it to test it would be the tail wagging the dog.
/// </summary>
public class FirstPeriodCollateralPolicyWiringTests
{
    private static readonly DateTime FactorDate = new(2024, 1, 1);

    private static IDeal Build(Type owner, DealDto dto)
    {
        var m = owner.GetMethod("BuildDeal", BindingFlags.NonPublic | BindingFlags.Static);
        m.Should().NotBeNull($"{owner.Name}.BuildDeal must exist for this seam to be testable");
        return (IDeal)m!.Invoke(null, new object?[] { dto, FactorDate, null })!;
    }

    private static DealDto Dto(string? policy, DateTime? accrualStart = null) => new()
    {
        DealName = "wiring",
        WaterfallType = "ComposableStructure",
        FirstPeriodCollateralPolicy = policy,
        CollateralAccrualStartDate = accrualStart,
        Tranches = new List<TrancheDto>(),
    };

    public static IEnumerable<object[]> Readers => new List<object[]>
    {
        new object[] { typeof(WaterfallController) },
        new object[] { typeof(GraamFlows.Cli.Services.WaterfallRunner) },
    };

    [Theory]
    [MemberData(nameof(Readers))]
    public void EveryReaderCarriesTheStatedPolicy(Type reader)
    {
        Build(reader, Dto("Drop")).FirstPeriodCollateralPolicyEnum
            .Should().Be(FirstPeriodCollateralPolicyEnum.Drop,
                "a policy the caller stated must not be silently replaced by the default");
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void EveryReaderCarriesTheStatedAccrualBoundary(Type reader)
    {
        Build(reader, Dto("Fold", new DateTime(2024, 3, 1))).CollateralAccrualStartDate
            .Should().Be(new DateTime(2024, 3, 1));
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void EveryReaderDefaultsToAlignWhenTheRequestIsSilent(Type reader)
    {
        var deal = Build(reader, Dto(null));
        deal.FirstPeriodCollateralPolicyEnum.Should().Be(FirstPeriodCollateralPolicyEnum.Align);
        deal.CollateralAccrualStartDate.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void EveryReaderRejectsAnUnrecognisedPolicyAtBuildTime(Type reader)
    {
        // Not when the branch is first reached. The enum is only consulted inside the
        // pre-boundary branch, so before this the SAME typo threw on a deal that happened
        // to have a stub period and ran silently on one that did not.
        var act = () => Build(reader, Dto("Stubb"));

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<ArgumentException>()
            .WithMessage("*not recognised*");
    }
}
