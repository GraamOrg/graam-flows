using FluentAssertions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.TypeEnum;
using Xunit;

namespace GraamFlows.Tests.Unit.Reinvestment;

/// <summary>
/// Domain-level behavior and validation for the reinvestment input model
/// (graam-flows#48): the plain balance target (scalar or per-period), synthetic
/// par pricing, default eligible-proceeds, and the throw-based Validate rules.
/// </summary>
public class ReinvestmentConfigTests
{
    private static ReinvestTemplate Bullet(double allocPct) => new()
    {
        AllocationPct = allocPct,
        TermMonths = 60,
        CouponRate = 6.0
    };

    private static ReinvestmentConfig Valid(params ReinvestTemplate[] templates) => new()
    {
        ReinvestEndDate = new DateTime(2029, 6, 1),
        Target = 1_000_000,
        Templates = templates.Length == 0 ? new[] { Bullet(100) } : templates
    };

    [Fact]
    public void TargetAt_ScalarTarget_ReturnsConstant()
    {
        var cfg = Valid();
        cfg.TargetAt(0).Should().Be(1_000_000);
        cfg.TargetAt(50).Should().Be(1_000_000);
    }

    [Fact]
    public void TargetAt_Schedule_ClampsPastEnd()
    {
        var cfg = Valid() with { TargetSchedule = new[] { 900.0, 800.0, 700.0 } };
        cfg.TargetAt(0).Should().Be(900.0);
        cfg.TargetAt(2).Should().Be(700.0);
        cfg.TargetAt(99).Should().Be(700.0); // clamps to last
        cfg.TargetAt(-1).Should().Be(900.0); // clamps to first
    }

    [Fact]
    public void EligibleProceeds_DefaultsToScheduledPlusPrepay()
    {
        var cfg = Valid();
        cfg.EligibleProceeds.Should().Be(EligibleProceeds.ScheduledPrincipal | EligibleProceeds.Prepayments);
        cfg.EligibleProceeds.HasFlag(EligibleProceeds.Recoveries).Should().BeFalse();
    }

    [Fact]
    public void EffectivePrice_SyntheticIsAlwaysPar()
    {
        var synthetic = new ReinvestTemplate { IsSynthetic = true, Price = 95.0 };
        synthetic.EffectivePrice.Should().Be(100.0);

        var cash = new ReinvestTemplate { IsSynthetic = false, Price = 99.5 };
        cash.EffectivePrice.Should().Be(99.5);
    }

    [Fact]
    public void IsInWindow_RespectsStartAndEnd()
    {
        var cfg = Valid() with
        {
            ReinvestStartDate = new DateTime(2026, 6, 1),
            ReinvestEndDate = new DateTime(2029, 6, 1)
        };
        cfg.IsInWindow(new DateTime(2026, 1, 1)).Should().BeFalse(); // before start
        cfg.IsInWindow(new DateTime(2027, 6, 1)).Should().BeTrue();
        cfg.IsInWindow(new DateTime(2030, 1, 1)).Should().BeFalse(); // after end
    }

    [Fact]
    public void Validate_HappyPath_DoesNotThrow()
    {
        var act = () => Valid().Validate("TESTDEAL");
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_MissingEndDate_Throws()
    {
        var cfg = Valid() with { ReinvestEndDate = default };
        cfg.Invoking(c => c.Validate("D")).Should().Throw<InvalidOperationException>()
            .WithMessage("*reinvestEndDate*");
    }

    [Fact]
    public void Validate_StartAfterEnd_Throws()
    {
        var cfg = Valid() with { ReinvestStartDate = new DateTime(2030, 1, 1) };
        cfg.Invoking(c => c.Validate("D")).Should().Throw<InvalidOperationException>()
            .WithMessage("*on or before*");
    }

    [Fact]
    public void Validate_HoldbackOutOfRange_Throws()
    {
        var cfg = Valid() with { Holdback = 1.5 };
        cfg.Invoking(c => c.Validate("D")).Should().Throw<InvalidOperationException>()
            .WithMessage("*holdback*");
    }

    [Fact]
    public void Validate_NoTemplates_Throws()
    {
        var cfg = Valid() with { Templates = Array.Empty<ReinvestTemplate>() };
        cfg.Invoking(c => c.Validate("D")).Should().Throw<InvalidOperationException>()
            .WithMessage("*at least one template*");
    }

    [Fact]
    public void Validate_AllocationsDoNotSumTo100_Throws()
    {
        var cfg = Valid(Bullet(60), Bullet(30)); // sums to 90
        cfg.Invoking(c => c.Validate("D")).Should().Throw<InvalidOperationException>()
            .WithMessage("*sum to 100*");
    }

    [Fact]
    public void Validate_MultipleTemplatesSummingTo100_DoesNotThrow()
    {
        var cfg = Valid(Bullet(60), Bullet(40));
        cfg.Invoking(c => c.Validate("D")).Should().NotThrow();
    }
}
