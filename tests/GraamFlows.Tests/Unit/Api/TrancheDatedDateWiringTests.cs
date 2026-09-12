using System.Reflection;
using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Objects.DataObjects;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     A class's DATED DATE — when it starts accruing — which is not always the deal's
///     closing date.
///
///     `FirstSettleDate` decides the first period's accrual, and until now the only way to
///     state it was the deal's `ClosingDate`. A note whose first Accrual Period begins BEFORE
///     closing is bought with accrued interest, and the gap is real money: OBX 2025-NQM6
///     closes 2025-04-14, first pays 2025-04-25, and states a thirty-day first Accrual
///     Period, so its notes are dated 2025-03-25. The closing-date derivation books eleven
///     days of interest where the document books thirty — measured, 730,090 against
///     1,991,154 on Class A-1.
///
///     `CapFirstAccrualToOnePeriod` does not reach it: that caps a stub LONGER than one
///     period, and deliberately preserves a short one (a settlement a few days before the
///     first pay date is a real shape). A dated date is the other direction, and nothing
///     could express it.
///
///     BOTH READERS, for the reason `FirstPeriodCollateralPolicyWiringTests` exists: the API
///     controller and the CLI runner consume the SAME `DealDto`, and a field only one of them
///     honours is worse than one neither honours, because the request looks obeyed on
///     whichever path the caller did not take. That has happened here once already.
///
///     Reflection rather than production visibility: `BuildDeal` is private static on both,
///     and widening it to test it would be the tail wagging the dog.
/// </summary>
public class TrancheDatedDateWiringTests
{
    private static readonly DateTime FactorDate = new(2024, 1, 1);
    private static readonly DateTime FirstPay = new(2025, 4, 25);
    private static readonly DateTime Closing = new(2025, 4, 14);
    private static readonly DateTime Dated = new(2025, 3, 25);

    private static IDeal Build(Type owner, DealDto dto)
    {
        var m = owner.GetMethod("BuildDeal", BindingFlags.NonPublic | BindingFlags.Static);
        m.Should().NotBeNull($"{owner.Name}.BuildDeal must exist for this seam to be testable");
        return (IDeal)m!.Invoke(null, new object?[] { dto, FactorDate, null })!;
    }

    private static DealDto Dto(DateTime? dated, DateTime? closing, DateTime? firstPay) => new()
    {
        DealName = "dated-date",
        WaterfallType = "UnifiedStructure",
        ClosingDate = closing,
        Tranches = new List<TrancheDto>
        {
            new()
            {
                TrancheName = "A1",
                OriginalBalance = 1_000_000,
                FirstPayDate = firstPay,
                FirstSettleDate = dated,
            },
        },
    };

    public static IEnumerable<object[]> Readers => new List<object[]>
    {
        new object[] { typeof(WaterfallController) },
        new object[] { typeof(GraamFlows.Cli.Services.WaterfallRunner) },
    };

    [Theory]
    [MemberData(nameof(Readers))]
    public void EveryReaderCarriesAStatedDatedDate(Type reader)
    {
        Build(reader, Dto(Dated, Closing, FirstPay)).Tranches.Single().FirstSettleDate
            .Should().Be(Dated,
                "a dated date the caller stated must not be replaced by the closing date");
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void AStatedDatedDateOUTRANKS_theDealClosingDate(Type reader)
    {
        // The whole point: the two are different dates on a deal that is dated before it
        // closes, and the class-level statement is the specific one.
        var deal = Build(reader, Dto(Dated, Closing, FirstPay));
        deal.Tranches.Single().FirstSettleDate.Should().NotBe(Closing);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void SilenceFallsBackToTheClosingDate_EXACTLY_AS_BEFORE(Type reader)
    {
        // Backward compatibility, asserted rather than asserted-about: every existing caller
        // omits this field, and the derivation they have always had must be untouched.
        Build(reader, Dto(null, Closing, FirstPay)).Tranches.Single().FirstSettleDate
            .Should().Be(Closing);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void SilenceWithNoClosingDateStillFallsBackToOnePeriodBeforeFirstPay(Type reader)
    {
        // The second rung of the pre-existing ladder, kept because adding a rung above a
        // ladder is exactly how the lower ones stop being exercised.
        Build(reader, Dto(null, null, FirstPay)).Tranches.Single().FirstSettleDate
            .Should().Be(FirstPay.AddMonths(-1));
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void SilenceWithNoDatesAtAllStillFallsBackToTheFactorDate(Type reader)
    {
        Build(reader, Dto(null, null, null)).Tranches.Single().FirstSettleDate
            .Should().Be(FactorDate);
    }
}
