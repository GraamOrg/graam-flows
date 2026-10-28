using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     <c>severityType: "LossAmount"</c>: severity / severityVector is the pool's loss in dollars for
///     each period. It is spread over that period's defaulted principal pro rata (one severity
///     fraction for every asset), capped at what defaulted, and recovered at t + recoveryLag. Loss
///     with no defaults to land on is reported in severityResolution, never carried forward.
/// </summary>
public class SeverityLossAmountTests
{
    private static readonly DateTime ProjectionDate = new(2024, 6, 1);

    private static AssetDto Loan(string id, double balance, string group = "1") => new()
    {
        AssetName = id,
        AssetId = id,
        InterestRateType = "FRM",
        OriginalDate = new DateTime(2023, 6, 1),
        OriginalBalance = balance,
        CurrentBalance = balance,
        OriginalInterestRate = 6.0,
        CurrentInterestRate = 6.0,
        OriginalAmortizationTerm = 360,
        GroupNum = group,
    };

    private static CalcCollateralRequest Request(double loss, double cdr = 6.0, int lag = 0, params AssetDto[] assets) => new()
    {
        Assets = assets.Length > 0 ? assets.ToList() : new List<AssetDto> { Loan("A", 6_000_000), Loan("B", 4_000_000) },
        ProjectionDate = ProjectionDate,
        Assumptions = new AssumptionsDto
        {
            Cpr = 5.0,
            Cdr = cdr,
            Severity = loss,
            SeverityType = "LossAmount",
            RecoveryLag = lag,
        },
    };

    private static ActionResult Post(CalcCollateralRequest request) =>
        new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(request).Result!;

    private static CalcCollateralResponse Ok(CalcCollateralRequest request) =>
        Post(request).Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<CalcCollateralResponse>().Subject;

    private static string Refusal(CalcCollateralRequest request) =>
        Post(request).Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()!;

    [Fact]
    public void A_Constant_Loss_With_Enough_Defaults_Books_Exactly_Each_Period()
    {
        // 6 CDR on $10MM defaults ~$50K a month early on; a $10K loss lands in full while it can.
        var response = Ok(Request(10_000));
        var covered = response.Cashflows.Where(cf => cf.DefaultedPrincipal >= 10_000).ToList();

        covered.Should().HaveCountGreaterThan(100);
        covered.Should().OnlyContain(cf => Math.Abs(cf.CollateralLoss - 10_000) < 1e-6);
        response.SeverityResolution!.LossBooked.Should().BeApproximately(response.Cashflows.Sum(cf => cf.CollateralLoss), 1e-3);
    }

    [Fact]
    public void A_Loss_Above_A_Periods_Defaults_Is_Capped_And_The_Rest_Reported()
    {
        var response = Ok(Request(10_000));
        var thin = response.Cashflows.Where(cf => cf.DefaultedPrincipal < 10_000).ToList();
        thin.Should().NotBeEmpty("the pool's defaults fall below the stated loss as it pays down");

        thin.Should().OnlyContain(cf => Math.Abs(cf.CollateralLoss - cf.DefaultedPrincipal) < 1e-6);
        var unbooked = response.SeverityResolution!.UnbookedByPeriod.ToDictionary(u => u.Period, u => u.Amount);
        foreach (var cf in thin.Where(cf => 10_000 - cf.DefaultedPrincipal > 0.005))
            unbooked[cf.Period].Should().BeApproximately(10_000 - cf.DefaultedPrincipal, 1e-6);
    }

    [Fact]
    public void With_No_Defaults_The_Whole_Loss_Is_Reported_Unbooked()
    {
        var response = Ok(Request(25_000, cdr: 0.0));
        var resolution = response.SeverityResolution!;

        response.Cashflows.Should().OnlyContain(cf => cf.CollateralLoss == 0.0);
        resolution.SeverityType.Should().Be("LossAmount");
        resolution.LossBooked.Should().Be(0.0);
        resolution.UnbookedByPeriod.Should().HaveCount(response.Cashflows.Count)
            .And.OnlyContain(u => u.Amount == 25_000);
        resolution.LossRequested.Should().Be(25_000.0 * response.Cashflows.Count);
    }

    [Fact]
    public void The_Recovery_Lands_Recovery_Lag_Periods_After_The_Default()
    {
        var cashflows = Ok(Request(10_000, lag: 6)).Cashflows;

        for (var t = 0; t < 24; t++)
            cashflows[t + 6].RecoveryPrincipal.Should().BeApproximately(cashflows[t].DefaultedPrincipal - 10_000, 1e-6, $"period {t}");
        cashflows.Take(6).Should().OnlyContain(cf => cf.RecoveryPrincipal == 0.0);
    }

    [Fact]
    public void Each_Group_Takes_The_Loss_Pro_Rata_To_What_It_Defaulted()
    {
        var response = Ok(Request(10_000, assets: new[] { Loan("A", 6_000_000, "1"), Loan("B", 4_000_000, "2") }));
        var byPeriod = response.Cashflows.GroupBy(cf => cf.CashflowDate).OrderBy(g => g.Key).Take(24);
        byPeriod.Should().OnlyContain(g => g.Count() == 2);

        foreach (var period in byPeriod)
        {
            period.Sum(cf => cf.CollateralLoss).Should().BeApproximately(10_000, 1e-6);
            var severities = period.Select(cf => cf.CollateralLoss / cf.DefaultedPrincipal).ToList();
            severities.Max().Should().BeApproximately(severities.Min(), 1e-12);
        }
    }

    [Fact]
    public void A_Severity_Vector_States_Each_Periods_Loss()
    {
        var request = Request(0);
        request.Assumptions.SeverityVector = new[] { 5_000.0, 20_000.0, 0.0, 15_000.0 };

        var cashflows = Ok(request).Cashflows;

        cashflows.Take(4).Select(cf => Math.Round(cf.CollateralLoss, 6)).Should().Equal(5_000, 20_000, 0, 15_000);
    }

    [Fact]
    public void Without_A_Type_Severity_Is_A_Percent_And_The_Response_Says_So()
    {
        var request = Request(40);
        request.Assumptions.SeverityType = null;

        var response = Ok(request);

        response.SeverityResolution!.SeverityType.Should().Be("Pct");
        response.SeverityResolution.LossRequested.Should().BeNull();
        response.Cashflows.Where(cf => cf.DefaultedPrincipal > 0)
            .Should().OnlyContain(cf => Math.Abs(cf.CollateralLoss - 0.4 * cf.DefaultedPrincipal) < 1e-6);
    }

    [Fact]
    public void A_Negative_Loss_Is_Refused()
    {
        Refusal(Request(-1)).Should().Contain("assumptions.severity").And.Contain("DOLLARS").And.Contain("cannot be negative");
    }

    [Fact]
    public void A_Loss_Above_The_Pools_Balance_Is_Refused_As_A_Units_Mistake()
    {
        Refusal(Request(10_000_001)).Should().Contain("assumptions.severity").And.Contain("10,000,000")
            .And.Contain("units mistake");
    }

    [Fact]
    public void A_Loss_Above_Two_Hundred_Is_Not_Refused_As_A_Percent()
    {
        Ok(Request(250_000)).SeverityResolution!.SeverityType.Should().Be("LossAmount");
    }

    [Fact]
    public void LossAmount_With_Per_Asset_Severities_Is_Refused()
    {
        var request = Request(10_000);
        request.AssetAssumptions = new Dictionary<string, AssetAssumptionDto> { ["A"] = new() { Severity = 40 } };

        Refusal(request).Should().Contain("LossAmount").And.Contain("assetAssumptions");
    }

    [Fact]
    public void LossAmount_With_PolyPaths_Strings_Is_Refused()
    {
        var request = Request(10_000);
        request.Assumptions.CdrVectorStr = "6.0";

        Refusal(request).Should().Contain("LossAmount").And.Contain("severityVector");
    }

    [Fact]
    public void An_Unknown_Severity_Type_Is_Refused()
    {
        var request = Request(10_000);
        request.Assumptions.SeverityType = "Dollars";

        Refusal(request).Should().Contain("assumptions.severityType").And.Contain("LossAmount");
    }
}
