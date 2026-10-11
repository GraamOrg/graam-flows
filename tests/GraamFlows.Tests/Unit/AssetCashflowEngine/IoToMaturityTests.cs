using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.Assumptions;
using GraamFlows.Domain;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.AssetCashflowEngine;

/// <summary>
/// A loan that is interest-only to maturity repays ON its maturity date.
///
/// The IO branch paid only interest in the maturity month and let the balance fall through to the
/// next period's balloon, so every IO-to-maturity loan — the shape a bullet takes over this endpoint,
/// which carries no amortization type — repaid one month late. Measured on a reinvesting CLO tied
/// out against a reference model, every collateral loan matured a month after the reference's, and
/// the ones maturing in a Payment-Date month slipped into the next quarter's distribution, so the
/// senior class amortised a quarter late and over-earned ~0.3%.
/// </summary>
public class IoToMaturityTests
{
    private static readonly DateTime Proj = new(2026, 1, 20);
    private static readonly DateTime Orig = new(2025, 12, 23);
    private const int Term = 24;

    private static List<PeriodCashflowDto> Run(int? ioTerm)
    {
        var ok = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(new CalcCollateralRequest
        {
            Assets = new List<AssetDto>
            {
                new()
                {
                    AssetName = "L", AssetId = "L", InterestRateType = "FRM", OriginalDate = Orig,
                    OriginalBalance = 1_000_000, CurrentBalance = 1_000_000, OriginalInterestRate = 6.0,
                    CurrentInterestRate = 6.0, OriginalAmortizationTerm = Term, GroupNum = "1",
                    IsIO = ioTerm != null, IOTerm = ioTerm
                }
            },
            ProjectionDate = Proj,
            Assumptions = new AssumptionsDto { Cpr = 0, Cdr = 0, Severity = 0 }
        }).Result.Should().BeOfType<OkObjectResult>().Subject;
        return ((CalcCollateralResponse)ok.Value!).Cashflows.OrderBy(c => c.Period).ToList();
    }

    [Fact]
    public void An_io_to_maturity_loan_repays_in_its_maturity_month()
    {
        var rows = Run(ioTerm: Term);
        var paid = rows.Where(r => r.ScheduledPrincipal > 1).ToList();
        paid.Should().ContainSingle("the whole balance repays once, at maturity");
        paid[0].CashflowDate.Should().Be(new DateTime(Orig.Year, Orig.Month, Proj.Day).AddMonths(Term),
            "the original date plus the term: December 2025 + 24 months is December 2027");
        paid[0].ScheduledPrincipal.Should().BeApproximately(1_000_000, 0.01);
        paid[0].Interest.Should().BeApproximately(1_000_000 * 0.06 / 12, 0.01, "its last month's interest, not a month more");
        rows.Where(r => r.CashflowDate > paid[0].CashflowDate).Sum(r => r.Interest).Should().Be(0);
    }

    [Fact]
    public void It_matures_with_a_true_bullet()
    {
        var bullet = new Asset
        {
            AssetName = "B", AssetId = "B", GroupNum = "1", InterestRateType = InterestRateType.FRM,
            AmortizationType = AmortizationType.Bullet, OriginalDate = Orig, OriginalBalance = 1_000_000,
            CurrentBalance = 1_000_000, OriginalInterestRate = 6.0, CurrentInterestRate = 6.0,
            OriginalAmortizationTerm = Term
        };
        var anchor = DateUtil.CalcAbsT(Proj);
        var zero = new AssetAssumptions(PrepaymentTypeEnum.CPR, new ConstVector(anchor, 0.0),
            DefaultTypeEnum.CDR, new ConstVector(anchor, 0.0), new ConstVector(anchor, 0.0));
        var cf = CfCore.GenerateAssetCashflows(new List<IAsset> { bullet }, Proj, null, _ => zero, null);
        var bulletMaturity = cf.PeriodCashflows.Single(p => p.ScheduledPrincipal > 1).CashflowDate;
        Run(ioTerm: Term).Single(r => r.ScheduledPrincipal > 1).CashflowDate.Should().Be(bulletMaturity);
    }

    [Fact]
    public void An_io_period_shorter_than_the_term_still_amortises_after_it()
    {
        var rows = Run(ioTerm: 6);
        rows.Take(6).Should().OnlyContain(r => r.ScheduledPrincipal < 0.01, "interest-only for six months");
        rows.Skip(6).Take(Term - 6 - 1).Should().OnlyContain(r => r.ScheduledPrincipal > 0, "then level-pay amortization");
        rows.Sum(r => r.ScheduledPrincipal).Should().BeApproximately(1_000_000, 1.0);
    }
}
