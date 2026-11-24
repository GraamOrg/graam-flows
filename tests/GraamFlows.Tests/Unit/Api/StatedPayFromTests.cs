using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Api.Transformers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     A tranche may state how it is paid (<see cref="TrancheDto.PayFrom" />) in the engine's own
///     terms, instead of the engine deriving it from the class's other fields and its name. A
///     stated pay source is used as stated; one the class cannot be paid by is refused; an absent
///     one leaves the derivation exactly as it was.
/// </summary>
public class StatedPayFromTests
{
    // ---- What a stated pay source selects ----

    [Fact]
    public void A_stated_servicing_strip_pays_from_the_servicing_fee_whatever_its_name()
    {
        // No "IOS" in the name and not a Reference tranche: the derivation would make it
        // Sequential. Stated, it pays from the servicing fee.
        var structures = Build(Io("SS1", payFrom: "ExcessServicing"));

        PayFrom(structures, "SS1").Should().Be("ExcessServicing");
    }

    [Fact]
    public void A_stated_pay_source_is_not_overridden_by_the_name()
    {
        // The name and the Reference type would derive ExcessServicing; the statement wins.
        var structures = Build(new TrancheDto
        {
            TrancheName = "AIOS", CashflowType = "IO", TrancheType = "Reference",
            NotionalReference = "A", PayFrom = "Notional"
        });

        PayFrom(structures, "AIOS").Should().Be("Notional");
        structures.Single(s => s.ClassGroupName == "AIOS").ExchangableTranche.Should().Be("A");
    }

    [Fact]
    public void A_notional_on_several_classes_keeps_the_whole_list()
    {
        var structures = Build(Io("X", payFrom: "Notional", notionalReference: "A, B"));

        PayFrom(structures, "X").Should().Be("Notional");
        structures.Single(s => s.ClassGroupName == "X").ExchangableTranche.Should().Be("A, B");
    }

    [Fact]
    public void A_stated_pay_source_is_matched_without_case()
    {
        PayFrom(Build(Io("SS1", payFrom: "excessservicing")), "SS1").Should().Be("ExcessServicing");
    }

    [Fact]
    public void An_absent_pay_source_derives_as_before()
    {
        var structures = Build(
            new TrancheDto { TrancheName = "AIOS", CashflowType = "IO", TrancheType = "Reference" },
            Io("AX", notionalReference: "A"),
            new TrancheDto { TrancheName = "XS", CashflowType = "IO", CouponType = "ExcessInterest" });

        PayFrom(structures, "A").Should().Be("Sequential");
        PayFrom(structures, "AIOS").Should().Be("ExcessServicing");
        PayFrom(structures, "AX").Should().Be("Notional");
        PayFrom(structures, "XS").Should().Be("Sequential");
    }

    // ---- What is refused ----

    [Fact]
    public void An_unknown_pay_source_is_refused()
    {
        var act = () => Build(Io("X", payFrom: "Pool"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*'X' states payFrom 'Pool'*");
    }

    [Theory]
    [InlineData("Notional")]
    [InlineData("ExcessServicing")]
    public void A_class_with_principal_cannot_be_paid_on_a_notional_or_the_servicing_fee(string payFrom)
    {
        var act = () => Build(new TrancheDto
            { TrancheName = "X", CashflowType = "PI", NotionalReference = "A", PayFrom = payFrom });
        act.Should().Throw<InvalidOperationException>().WithMessage("*'X'*not interest-only*");
    }

    [Fact]
    public void A_notional_that_follows_nothing_is_refused()
    {
        var act = () => Build(Io("X", payFrom: "Notional"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*names nothing its notional follows*");
    }

    [Fact]
    public void A_notional_that_follows_a_class_the_deal_lacks_is_refused()
    {
        // Derived, such a notional finds no class to follow and silently pays nothing.
        var act = () => Build(Io("X", payFrom: "Notional", notionalReference: "A,A9"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*follows 'A9'*not a tranche of this deal*");
    }

    [Fact]
    public void An_exchange_with_no_components_is_refused()
    {
        var act = () => Build(Io("AB", payFrom: "Exchange"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*'AB'*no exchange shares*");
    }

    [Fact]
    public void An_exchange_with_its_components_is_paid_from_them()
    {
        var shares = new List<ExchangeShareDto>
        {
            new()
            {
                ExchangeTranche = "AB",
                Shares = new List<ExShareDto> { new() { TrancheName = "A" }, new() { TrancheName = "B" } }
            }
        };
        var structures = UnifiedWaterfallBuilder.BuildDealStructures(Waterfall(),
            Tranches(new TrancheDto { TrancheName = "AB", CashflowType = "IO", PayFrom = "Exchange" }), shares);

        PayFrom(structures, "AB").Should().Be("Exchange");
        structures.Single(s => s.ClassGroupName == "AB").ExchangableTranche.Should().Be("A,B");
    }

    [Fact]
    public void An_accrual_class_stating_another_pay_source_is_refused()
    {
        var waterfall = Waterfall();
        waterfall.Steps.Add(new WaterfallStepDto
            { Type = "ACCRUAL", Tranche = "B", Structure = Seq("A", "B") });

        var act = () => UnifiedWaterfallBuilder.BuildDealStructures(waterfall,
            new List<TrancheDto>
            {
                new() { TrancheName = "A" },
                new() { TrancheName = "B", PayFrom = "ExcessServicing" }
            });
        act.Should().Throw<InvalidOperationException>().WithMessage("*'B' is named by an ACCRUAL step*");

        var ok = UnifiedWaterfallBuilder.BuildDealStructures(waterfall,
            new List<TrancheDto> { new() { TrancheName = "A" }, new() { TrancheName = "B", PayFrom = "Sequential" } });
        PayFrom(ok, "B").Should().Be("Accrual");
    }

    // ---- The notional, run through the engine ----

    private static readonly DateTime FirstPayDate = new(2026, 2, 25);
    private const double A1 = 30_000_000, A2 = 30_000_000, B = 40_000_000, Pool = A1 + A2 + B;

    [Fact]
    public void A_notional_on_two_classes_follows_their_sum()
    {
        var response = Run(new TrancheDto
        {
            TrancheName = "X", OriginalBalance = A1 + A2, CashflowType = "IO", CouponType = "Fixed",
            FixedCoupon = 1.0, NotionalReference = "A1,A2", PayFrom = "Notional",
            FirstPayDate = FirstPayDate, PayDay = FirstPayDate.Day, SubordinationOrder = 50
        });

        var a1 = response.TrancheCashflows["A1"];
        var a2 = response.TrancheCashflows["A2"];
        var x = response.TrancheCashflows["X"];
        for (var i = 0; i < x.Count; i++)
            x[i].Balance.Should().BeApproximately(a1[i].Balance + a2[i].Balance, 1.0,
                $"X's notional is A1 + A2 in period {i + 1}");
        x.Sum(c => c.ScheduledPrincipal + c.UnscheduledPrincipal).Should().BeApproximately(0, 1.0);
        x.Sum(c => c.Interest).Should().BeGreaterThan(0, "an INTEREST step names X, so it is paid its coupon");
        // The classes pay down before B does, so a pool notional would be larger.
        var a2Gone = Enumerable.Range(0, a2.Count).First(i => a2[i].Balance < 1.0);
        x[a2Gone].Balance.Should().BeApproximately(0, 1.0, "the notional is gone once A1 and A2 are");
    }

    [Fact]
    public void A_notional_on_the_pool_is_its_own_face_scaled_by_the_pool()
    {
        const double face = Pool / 2;
        var collateral = Collateral();
        var response = Run(new TrancheDto
        {
            TrancheName = "XP", OriginalBalance = face, CashflowType = "IO", CouponType = "Fixed",
            FixedCoupon = 1.0, NotionalReference = "GROUP_1", PayFrom = "Notional",
            FirstPayDate = FirstPayDate, PayDay = FirstPayDate.Day, SubordinationOrder = 50
        }, collateral);

        var xp = response.TrancheCashflows["XP"];
        for (var i = 0; i < xp.Count && i < collateral.Count; i++)
            xp[i].Balance.Should().BeApproximately(face * collateral[i].Balance / Pool, 1.0,
                $"XP's notional is half the pool in period {i + 1}, not the whole pool");
    }

    // ---- helpers ----

    private static List<DealStructureDto> Build(params TrancheDto[] extra) =>
        UnifiedWaterfallBuilder.BuildDealStructures(Waterfall(), Tranches(extra));

    private static List<TrancheDto> Tranches(params TrancheDto[] extra) =>
        new List<TrancheDto> { new() { TrancheName = "A" }, new() { TrancheName = "B" } }.Concat(extra).ToList();

    private static TrancheDto Io(string name, string? payFrom = null, string? notionalReference = null) => new()
        { TrancheName = name, CashflowType = "IO", PayFrom = payFrom, NotionalReference = notionalReference };

    private static string PayFrom(List<DealStructureDto> structures, string name) =>
        structures.Single(s => s.ClassGroupName == name).PayFrom;

    private static UnifiedWaterfallDto Waterfall() => new()
    {
        Steps = new List<WaterfallStepDto>
        {
            new() { Type = "INTEREST", Structure = Seq("A", "B") },
            new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A", "B") },
            new() { Type = "WRITEDOWN", Structure = Seq("B", "A") }
        }
    };

    private static PayableStructureDto Seq(params string[] tranches) => new()
        { Type = "SEQ", Tranches = tranches.ToList() };

    private static WaterfallResponse Run(TrancheDto io, List<PeriodCashflowDto>? collateral = null)
    {
        var request = new WaterfallRequest
        {
            ProjectionDate = FirstPayDate.AddMonths(-1),
            CollateralCashflows = collateral ?? Collateral(),
            Deal = new DealDto
            {
                DealName = "STATED_PAY_FROM_TEST",
                WaterfallType = "ComposableStructure",
                ClosingDate = FirstPayDate.AddMonths(-1),
                Tranches = new List<TrancheDto> { Bond("A1", A1, 0), Bond("A2", A2, 1), Bond("B", B, 2), io },
                UnifiedWaterfall = new UnifiedWaterfallDto
                {
                    ExecutionOrder = new List<string>
                        { "INTEREST", "PRINCIPAL_SCHEDULED", "PRINCIPAL_UNSCHEDULED", "PRINCIPAL_RECOVERY", "WRITEDOWN" },
                    Steps = new List<WaterfallStepDto>
                    {
                        new() { Type = "INTEREST", Structure = Seq("A1", "A2", io.TrancheName, "B") },
                        new() { Type = "PRINCIPAL", Source = "scheduled", Default = Seq("A1", "A2", "B") },
                        new() { Type = "PRINCIPAL", Source = "unscheduled", Default = Seq("A1", "A2", "B") },
                        new() { Type = "PRINCIPAL", Source = "recovery", Default = Seq("A1", "A2", "B") },
                        new() { Type = "WRITEDOWN", Structure = Seq("B", "A2", "A1") }
                    }
                }
            }
        };
        var result = new WaterfallController(NullLogger<WaterfallController>.Instance).Execute(request);
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull("the waterfall request should succeed");
        return (ok!.Value as WaterfallResponse)!;
    }

    private static TrancheDto Bond(string name, double balance, int order) => new()
    {
        TrancheName = name, OriginalBalance = balance, CashflowType = "PI", CouponType = "Fixed",
        FixedCoupon = 5.0, SubordinationOrder = order, FirstPayDate = FirstPayDate, PayDay = FirstPayDate.Day
    };

    private static List<PeriodCashflowDto> Collateral()
    {
        var periods = new List<PeriodCashflowDto>();
        var balance = Pool;
        const double wac = 6.0;
        for (var i = 0; i < 36 && balance > 1.0; i++)
        {
            var interest = balance * wac / 100 / 12;
            var scheduled = i == 35 ? balance : balance * 0.05;
            var unscheduled = i == 35 ? 0.0 : balance * 0.02;
            var end = balance - scheduled - unscheduled;
            periods.Add(new PeriodCashflowDto
            {
                Period = i + 1, CashflowDate = FirstPayDate.AddMonths(i), GroupNum = "1",
                BeginBalance = balance, Balance = end, ScheduledPrincipal = scheduled,
                UnscheduledPrincipal = unscheduled, Interest = interest, NetInterest = interest, Wac = wac
            });
            balance = end;
        }

        return periods;
    }
}
