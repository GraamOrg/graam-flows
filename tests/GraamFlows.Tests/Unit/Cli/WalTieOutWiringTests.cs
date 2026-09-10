using FluentAssertions;
using GraamFlows.Api.Models;
using GraamFlows.Cli.Services;
using Xunit;

namespace GraamFlows.Tests.Unit.Cli;

/// <summary>
///     The resolvers in <see cref="WalTieOutBasisTests" /> decide the anchor, the prepayment
///     convention and the call basis. These tests pin that what they decide actually REACHES the
///     engine, by running the whole grid on a small synthetic deal and comparing runs against
///     each other.
///
///     That distinction is the whole defect: the convention and the call basis were both settled
///     at the <c>runner.Run(...)</c> call site as literals, so a resolver that returns the right
///     answer while the call site still passes <c>useAbsPrepayment: true</c> is not a fix. Every
///     assertion below is a comparison between two runs of the same deal, so it fails if the
///     stated input stops reaching the run.
/// </summary>
public class WalTieOutWiringTests
{
    private static readonly DateTime FirstPay = new(2025, 4, 25);
    private static readonly DateTime Closing = new(2025, 4, 14);

    private static PayableStructureDto Seq(params string[] tranches) =>
        new() { Type = "SEQ", Tranches = tranches.ToList() };

    private static TrancheDto Note(string name, double balance, int order, double coupon, bool withFirstPay) => new()
    {
        TrancheName = name,
        OriginalBalance = balance,
        SubordinationOrder = order,
        CouponType = "Fixed",
        FixedCoupon = coupon,
        CashflowType = "PI",
        TrancheType = "Offered",
        DayCount = "30/360",
        PayDay = 25,
        FirstPayDate = withFirstPay ? FirstPay : null
    };

    /// <summary>
    ///     A two-class sequential deal on a 30-year 7.5% pool, carrying both redemptions the
    ///     issue is about: a dated one in April 2029 and a 30% balance clean-up.
    /// </summary>
    private static DealModelFile Deal(
        WalAssumptions? assumptions = null,
        bool trancheFirstPayDates = true,
        bool dealLevelFirstPaymentDate = false,
        List<double>? speeds = null)
    {
        var grid = speeds ?? [0.0, 40.0];
        return new DealModelFile
        {
            Deal = new DealDto
            {
                DealName = "SYNTH 2025-1",
                Triggers =
                [
                    new TriggerDto
                    {
                        TriggerName = "CleanUp", TriggerType = "COLLATERAL_VALUE",
                        TriggerParam = "30", GroupNum = "1"
                    },
                    new TriggerDto
                    {
                        TriggerName = "OptionalRedemption", TriggerType = "DATE_TERMINATION",
                        TriggerParam = "2029-04-01", GroupNum = "1"
                    }
                ],
                UnifiedWaterfall = new UnifiedWaterfallDto
                {
                    ExecutionOrder =
                    [
                        "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED",
                        "PRINCIPAL_RECOVERY", "WRITEDOWN"
                    ],
                    Steps =
                    [
                        new WaterfallStepDto { Type = "INTEREST", Structure = Seq("A", "B") },
                        new WaterfallStepDto { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A", "B") },
                        new WaterfallStepDto { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A", "B") },
                        new WaterfallStepDto { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A", "B") },
                        new WaterfallStepDto { Type = "WRITEDOWN", Structure = Seq("B", "A") }
                    ]
                },
                Tranches =
                {
                    Note("A", 80_000_000, 0, 5.0, trancheFirstPayDates),
                    Note("B", 20_000_000, 1, 6.0, trancheFirstPayDates)
                }
            },
            ClosingDate = Closing,
            FirstPaymentDate = dealLevelFirstPaymentDate ? FirstPay : null,
            PoolStratification = new PoolStratificationSection
            {
                WeightedAverageApr = 7.5,
                WeightedAverageRemainingTerm = 358,
                TotalBalance = 100_000_000,
                Pools =
                [
                    new PoolEntry
                    {
                        PoolNum = 1, AggregateBalance = 100_000_000, GrossApr = 7.5,
                        OriginalTermMonths = 360, RemainingTermMonths = 358
                    }
                ]
            },
            WalScenarios = new WalScenariosSection
            {
                AbsPercentages = grid,
                Assumptions = assumptions ?? new WalAssumptions(),
                // The expected column is irrelevant here: these tests compare COMPUTED values
                // between runs, never against a published grid.
                Tranches =
                [
                    new WalTrancheEntry
                    {
                        TrancheName = "A",
                        WalToCall = grid.Select(_ => (double?)0.0).ToList(),
                        WalToMaturity = grid.Select(_ => (double?)0.0).ToList()
                    }
                ]
            }
        };
    }

    private static List<double> Computed(DealModelFile deal) =>
        new WalValidator().Validate(deal, 0.10, false).Select(r => Math.Round(r.ComputedWal, 4)).ToList();

    [Fact]
    public void TheGridIsStruckInCpr_UnlessTheDealSaysAbs()
    {
        var declaredCpr = Computed(Deal(new WalAssumptions { PrepaymentType = "CPR" }));
        var silent = Computed(Deal(new WalAssumptions { PricingSpeedCpr = 25.0 }));
        var declaredAbs = Computed(Deal(new WalAssumptions { PrepaymentType = "ABS" }));

        silent.Should().Equal(declaredCpr,
            "a deal that states a CPR pricing speed and nothing else is a CPR grid; the run used " +
            "to apply the auto-ABS convention to every deal regardless");
        declaredAbs.Should().NotEqual(declaredCpr,
            "and a deal that DOES state ABS must still get ABS — the convention has to reach the run");
    }

    [Fact]
    public void TheDatedRedemptionIsWhatTheRunTerminatesOn()
    {
        var resolved = Computed(Deal());
        var dated = Computed(Deal(new WalAssumptions { CallTriggers = ["OptionalRedemption"] }));
        var cleanUp = Computed(Deal(new WalAssumptions { CallTriggers = ["CleanUp"] }));

        resolved.Should().Equal(dated,
            "the deal declares a dated redemption, so that is the basis the grid is struck to");
        resolved.Should().NotEqual(cleanUp,
            "and the balance clean-up is a materially different basis — at 40% CPR it trips years " +
            "earlier, which is what collapsed every fast column");
    }

    [Fact]
    public void TheAnswerIsTheSame_WhetherTheAnchorIsStatedOnTheTranchesOrOnTheDeal()
    {
        // Defect #1 end to end. Both models state the same 25 April 2025 first payment; one puts
        // it on the tranches, the other only at deal level. Before the fix the second was
        // unreachable and every repline was originated one month from the day of the run, so
        // these two lists differed — and the second list differed from itself day to day.
        var onTranches = Computed(Deal());
        var onDeal = Computed(Deal(trancheFirstPayDates: false, dealLevelFirstPaymentDate: true));

        onDeal.Should().Equal(onTranches);
    }

    [Fact]
    public void AFasterSpeedShortensTheSeniorWal_ItDoesNotCollapseIt()
    {
        // Under the hard-coded ABS convention the pool retired within a couple of years at every
        // speed on the grid and the fast columns computed ~0.00. The senior class here amortises
        // toward the April 2029 redemption instead.
        var wals = Computed(Deal(speeds: [0.0, 10.0, 40.0]));

        wals.Should().HaveCount(3);
        wals[0].Should().BeGreaterThan(wals[1]);
        wals[1].Should().BeGreaterThan(wals[2]);
        wals[2].Should().BeGreaterThan(1.0,
            "a 40% CPR senior running to a 2029 call is years long, not months");
    }
}
