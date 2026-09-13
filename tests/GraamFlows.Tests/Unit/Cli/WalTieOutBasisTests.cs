using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Cli.Services;
using Xunit;

namespace GraamFlows.Tests.Unit.Cli;

/// <summary>
///     The three inputs a WAL tie-out is struck from, none of which the CLI used to take from
///     the deal (graam-flows#88):
///     <list type="number">
///         <item>
///             the first-pay anchor every repline is originated at — read from per-tranche
///             <c>firstPayDate</c> only, and otherwise <c>DateTime.Today.AddMonths(1)</c>, which
///             made the same deal tie on one day and miss on the next;
///         </item>
///         <item>the prepayment convention — hard-coded to ABS, the auto-ABS convention, on RMBS;</item>
///         <item>
///             the call basis — the deal carries both a dated optional redemption and a balance
///             clean-up, and the published "to redemption date" table is struck to the dated one.
///         </item>
///     </list>
///     The anchor tests below pin the RESOLVED date against a value the deal states. That is the
///     point: an assertion that merely accepts whatever the wall clock yields cannot fail on the
///     defect it is meant to hold shut.
/// </summary>
public class WalTieOutBasisTests
{
    private static readonly DateTime TrancheFirstPay = new(2025, 4, 25);
    private static readonly DateTime DealFirstPayment = new(2025, 5, 20);
    private static readonly DateTime ClosingDate = new(2025, 4, 14);

    private static DealModelFile Model(
        DateTime? trancheFirstPay = null,
        DateTime? dealFirstPayment = null,
        DateTime? closingDate = null,
        DateTime? projectionDate = null,
        WalAssumptions? assumptions = null,
        List<TriggerDto>? triggers = null)
    {
        return new DealModelFile
        {
            Deal = new DealDto
            {
                DealName = "TIEOUT 2025-1",
                Triggers = triggers,
                Tranches =
                {
                    new TrancheDto
                    {
                        TrancheName = "A1",
                        OriginalBalance = 100_000_000,
                        PayDay = 25,
                        FirstPayDate = trancheFirstPay
                    }
                }
            },
            FirstPaymentDate = dealFirstPayment,
            ClosingDate = closingDate,
            ProjectionDate = projectionDate,
            PoolStratification = new PoolStratificationSection
            {
                WeightedAverageApr = 7.5,
                WeightedAverageRemainingTerm = 358,
                TotalBalance = 100_000_000,
                Pools =
                [
                    new PoolEntry
                    {
                        PoolNum = 1,
                        AggregateBalance = 100_000_000,
                        GrossApr = 7.5,
                        OriginalTermMonths = 360,
                        RemainingTermMonths = 358
                    }
                ]
            },
            WalScenarios = new WalScenariosSection { Assumptions = assumptions }
        };
    }

    private static TriggerDto Trigger(string name, string type, string param) =>
        new() { TriggerName = name, TriggerType = type, TriggerParam = param, GroupNum = "1" };

    // ---------------------------------------------------------------- first-pay anchor (#1)

    [Fact]
    public void PerTrancheFirstPayDate_IsTheAnchor()
    {
        var model = Model(trancheFirstPay: TrancheFirstPay, dealFirstPayment: DealFirstPayment,
            closingDate: ClosingDate);

        CollateralBuilder.GetFirstPayDate(model).Should().Be(TrancheFirstPay);
    }

    [Fact]
    public void DealLevelFirstPaymentDate_IsReached_WhenNoTrancheCarriesOne()
    {
        // The defect in one line: this deal states its payment date once, at deal level, and the
        // tranches carry only a payDay. DealModelFile did not even bind firstPaymentDate, so the
        // stated date was unreachable in principle and every repline was originated one month
        // from the day of the run.
        var model = Model(dealFirstPayment: DealFirstPayment, closingDate: ClosingDate);

        CollateralBuilder.GetFirstPayDate(model).Should().Be(DealFirstPayment);
    }

    [Fact]
    public void ClosingDate_IsTheLastResort()
    {
        var model = Model(closingDate: ClosingDate);

        CollateralBuilder.GetFirstPayDate(model).Should().Be(ClosingDate);
    }

    [Fact]
    public void EveryReplineIsOriginatedAtTheDeclaredAnchor_NotAtTheRunDate()
    {
        // The consumer, not just the resolver: the anchor reaches each built asset. A wall-clock
        // fallback would put OriginalDate one month from today, which is what this deal's
        // POOL_2 repline showed in the field — origDate 2026-10-10 on a deal that closed in
        // April 2025.
        var model = Model(dealFirstPayment: DealFirstPayment, closingDate: ClosingDate);

        var assets = new CollateralBuilder().BuildAssets(model);

        assets.Should().NotBeEmpty();
        assets.Should().OnlyContain(a => a.OriginalDate == DealFirstPayment);
        assets.Should().NotContain(a => a.OriginalDate == DateTime.Today.AddMonths(1),
            "the anchor must come from the deal, never from the day the command runs");
    }

    [Fact]
    public void AnUnresolvableAnchorFailsLoudly_RatherThanInventingOne()
    {
        // A tie-out that silently invents an anchor is worse than one that refuses to run: the
        // result looks authoritative and is unattributable. Returning ANY date here — today plus
        // a month included — is the failure this test exists to catch.
        var model = Model();

        var act = () => CollateralBuilder.GetFirstPayDate(model);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*firstPaymentDate*")
            .WithMessage("*closingDate*");
    }

    [Fact]
    public void ProjectionDateResolvesFromTheDeal_InPreferenceOrder()
    {
        WalValidator.ResolveProjectionDate(
                Model(trancheFirstPay: TrancheFirstPay, projectionDate: new DateTime(2025, 6, 1)))
            .Should().Be(new DateTime(2025, 6, 1));

        WalValidator.ResolveProjectionDate(
                Model(trancheFirstPay: TrancheFirstPay,
                    assumptions: new WalAssumptions { FirstDistributionDate = new DateTime(2025, 5, 25) }))
            .Should().Be(new DateTime(2025, 5, 25));

        WalValidator.ResolveProjectionDate(Model(trancheFirstPay: TrancheFirstPay))
            .Should().Be(TrancheFirstPay);

        WalValidator.ResolveProjectionDate(Model(dealFirstPayment: DealFirstPayment))
            .Should().Be(DealFirstPayment);

        var act = () => WalValidator.ResolveProjectionDate(Model());
        act.Should().Throw<InvalidOperationException>(
            "no leg of this resolution may fall back to the wall clock");
    }

    // ----------------------------------------------------------- prepayment convention (#2)

    [Fact]
    public void PrepaymentConventionDefaultsToCpr_NotToTheAutoAbsConvention()
    {
        WalValidator.ResolveUseAbsPrepayment(null).Should().BeFalse();
        WalValidator.ResolveUseAbsPrepayment(new WalAssumptions()).Should().BeFalse();

        // An NQM term sheet's own assumptions block: a CPR pricing speed and nothing else. Under the
        // hard-coded ABS convention, speed "5" retired the pool in ~20 months and every non-zero
        // column computed ~0.00.
        WalValidator.ResolveUseAbsPrepayment(new WalAssumptions { PricingSpeedCpr = 25.0 })
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("ABS", true)]
    [InlineData("abs", true)]
    [InlineData(" ABS ", true)]
    [InlineData("CPR", false)]
    [InlineData("cpr", false)]
    public void DeclaredPrepaymentTypeIsHonoured(string declared, bool expectAbs)
    {
        WalValidator.ResolveUseAbsPrepayment(new WalAssumptions { PrepaymentType = declared })
            .Should().Be(expectAbs);
    }

    [Fact]
    public void AnUnrecognisedPrepaymentTypeIsRejected_NotQuietlyReinterpreted()
    {
        var act = () => WalValidator.ResolveUseAbsPrepayment(new WalAssumptions { PrepaymentType = "SMM" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*SMM*");
    }

    // -------------------------------------------------------------------- call basis (#3)

    [Fact]
    public void ADatedRedemptionOutranksTheBalanceCleanUp()
    {
        // That NQM deal carries both. Its published table holds the subordinate classes at 3.95y
        // at every speed from 0% to 40% CPR — the signature of a dated call; the balance call
        // walks them down to ~2.4y at 40%. Measured: dated-only ties all 63 points (RMSE 0.043y),
        // engaging both misses 56 of 63 (RMSE 2.70y), the clean-up alone misses all 63 (RMSE 3.02y).
        var model = Model(trancheFirstPay: TrancheFirstPay, triggers:
        [
            Trigger("CleanUp", "COLLATERAL_VALUE", "30"),
            Trigger("OptionalRedemption", "DATE_TERMINATION", "2029-04-01")
        ]);

        WalValidator.ResolveCallTriggerNames(model).Should().Equal("OptionalRedemption");
    }

    [Fact]
    public void TheCleanUpIsTheBasis_WhenTheDealDeclaresNoDatedRedemption()
    {
        // The auto-ABS shape, unchanged by this fix.
        var model = Model(trancheFirstPay: TrancheFirstPay, triggers:
        [
            Trigger("CleanUp", "COLLATERAL_VALUE", "10")
        ]);

        WalValidator.ResolveCallTriggerNames(model).Should().Equal("CleanUp");
    }

    [Fact]
    public void ADeclaredCallBasisWins_AndIsHowTheEarliestOfBothIsAskedFor()
    {
        var model = Model(trancheFirstPay: TrancheFirstPay,
            assumptions: new WalAssumptions { CallTriggers = ["CleanUp", "OptionalRedemption"] },
            triggers:
            [
                Trigger("CleanUp", "COLLATERAL_VALUE", "30"),
                Trigger("OptionalRedemption", "DATE_TERMINATION", "2029-04-01")
            ]);

        WalValidator.ResolveCallTriggerNames(model).Should().Equal("CleanUp", "OptionalRedemption");
    }

    [Fact]
    public void NonTerminationTriggersAreNeverPartOfTheCallBasis()
    {
        // RunToCall forecasts EVERY optional trigger the deal declares as always-triggering.
        // A step-down date or a delinquency test is not a redemption and must not terminate a run.
        var model = Model(trancheFirstPay: TrancheFirstPay, triggers:
        [
            Trigger("StepDownDate", "FORMULA_CONDITION", "n/a"),
            Trigger("DelinquencyTest", "DELINQ_TRIGGER_SUB_1", "0.05"),
            Trigger("CleanUp", "COLLATERAL_VALUE", "30")
        ]);

        WalValidator.ResolveCallTriggerNames(model).Should().Equal("CleanUp");
    }

    [Fact]
    public void ADealWithNoTerminationTriggerResolvesNoCallBasis()
    {
        var model = Model(trancheFirstPay: TrancheFirstPay, triggers:
        [
            Trigger("StepDownDate", "FORMULA_CONDITION", "n/a")
        ]);

        WalValidator.ResolveCallTriggerNames(model).Should().BeEmpty();
    }

    [Fact]
    public void ACallBasisThatMatchesNoTriggerOnTheDealIsRejected()
    {
        // Left unchecked this runs to maturity while the report says "to call" — the same class
        // of silent mislabelling the whole issue is about.
        var model = Model(trancheFirstPay: TrancheFirstPay, triggers:
        [
            Trigger("CleanUp", "COLLATERAL_VALUE", "30")
        ]);
        var assets = new CollateralBuilder().BuildAssets(model);

        var act = () => new WaterfallRunner().Run(model, assets, TrancheFirstPay, 10, 0, 0, 0,
            runToCall: true, callTriggerNames: ["OptionalRedemption"]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OptionalRedemption*")
            .WithMessage("*to-maturity*");
    }

    [Fact]
    public void TheCallBasisIsReportedByName()
    {
        WalValidator.DescribeCallBasis(new List<string>()).Should().NotContain("[");
        WalValidator.DescribeCallBasis(new List<string> { "OptionalRedemption" })
            .Should().Be("OptionalRedemption");
        WalValidator.DescribeCallBasis(new List<string> { "CleanUp", "OptionalRedemption" })
            .Should().Be("earliest of [CleanUp, OptionalRedemption]");
    }
}
