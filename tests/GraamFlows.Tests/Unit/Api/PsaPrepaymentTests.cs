using FluentAssertions;
using GraamFlows;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.AssetCashflowEngine;
using GraamFlows.Assumptions;
using GraamFlows.Objects.DataObjects;
using GraamFlows.Objects.Functions;
using GraamFlows.Objects.TypeEnum;
using GraamFlows.Objects.Util;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GraamFlows.Tests.Unit.Api;

/// <summary>
///     <c>prepaymentType: "PSA"</c>: cpr carries a PSA speed, and each asset runs the standard ramp
///     (0.2% CPR in its first month, +0.2% a month, 6% from month 30, all times speed/100) on its own
///     age from its origination date. Every engine-level check here is against the same pool run as
///     the per-period CPR vector the ramp should produce, so it pins the convention, not a number.
/// </summary>
public class PsaPrepaymentTests
{
    private static readonly DateTime ProjectionDate = new(2024, 6, 1);
    private const int Periods = 360;

    private static AssetDto Loan(string id, DateTime originalDate) => new()
    {
        AssetName = id,
        AssetId = id,
        InterestRateType = "FRM",
        OriginalDate = originalDate,
        OriginalBalance = 300_000.0,
        CurrentBalance = 300_000.0,
        OriginalInterestRate = 6.5,
        CurrentInterestRate = 6.5,
        OriginalAmortizationTerm = 360,
        GroupNum = "1",
    };

    // Originated the month before the projection: age 1 in the first projected row.
    private static AssetDto NewLoan(string id = "NEW") => Loan(id, ProjectionDate.AddMonths(-1));

    private static CalcCollateralRequest Request(string prepaymentType, double cpr, params AssetDto[] assets) => new()
    {
        Assets = assets.ToList(),
        ProjectionDate = ProjectionDate,
        Assumptions = new AssumptionsDto { Cpr = cpr, Cdr = 0.0, PrepaymentType = prepaymentType },
    };

    private static ActionResult Post(CalcCollateralRequest request) =>
        new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(request).Result!;

    private static CalcCollateralResponse Ok(CalcCollateralRequest request) =>
        Post(request).Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<CalcCollateralResponse>().Subject;

    private static CalcCollateralRequest CprRamp(double speed, int firstAge, AssetDto asset)
    {
        var request = Request("CPR", 0.0, asset);
        request.Assumptions.CprVector = Enumerable.Range(0, Periods)
            .Select(row => speed / 100.0 * 6.0 * Math.Min(firstAge + row, 30) / 30.0)
            .ToArray();
        return request;
    }

    private static void ShouldMatch(List<PeriodCashflowDto> actual, List<PeriodCashflowDto> expected)
    {
        actual.Should().HaveCount(expected.Count);
        for (var i = 0; i < actual.Count; i++)
            actual[i].UnscheduledPrincipal.Should().BeApproximately(expected[i].UnscheduledPrincipal, 1e-6, $"period {i}");
    }

    [Fact]
    public void The_Ramp_Is_0_2_Percent_In_Month_1_And_6_Percent_From_Month_30()
    {
        PsaRamp.Cpr(100, 1).Should().BeApproximately(0.2, 1e-12);
        PsaRamp.Cpr(100, 15).Should().BeApproximately(3.0, 1e-12);
        PsaRamp.Cpr(100, 30).Should().BeApproximately(6.0, 1e-12);
        PsaRamp.Cpr(100, 200).Should().BeApproximately(6.0, 1e-12);
        PsaRamp.Cpr(5000, 30).Should().Be(100.0, "the CPR saturates at 100");
    }

    [Fact]
    public void A_New_Loan_At_100_Psa_Runs_The_Ramp_To_6_Percent_At_Month_30()
    {
        ShouldMatch(Ok(Request("PSA", 100, NewLoan())).Cashflows, Ok(CprRamp(100, 1, NewLoan())).Cashflows);
    }

    [Fact]
    public void A_Loan_Seasoned_40_Months_Runs_Flat_6_Percent_From_Period_0()
    {
        var seasoned = Loan("OLD", ProjectionDate.AddMonths(-40));

        ShouldMatch(Ok(Request("PSA", 100, seasoned)).Cashflows, Ok(Request("CPR", 6.0, seasoned)).Cashflows);
    }

    [Fact]
    public void A_Loan_Seasoned_10_Months_Starts_Partway_Up_The_Ramp()
    {
        var seasoned = Loan("MID", ProjectionDate.AddMonths(-10));

        ShouldMatch(Ok(Request("PSA", 100, seasoned)).Cashflows, Ok(CprRamp(100, 10, seasoned)).Cashflows);
    }

    [Fact]
    public void Two_Hundred_Psa_Doubles_Every_Periods_Cpr()
    {
        ShouldMatch(Ok(Request("PSA", 200, NewLoan())).Cashflows, Ok(CprRamp(200, 1, NewLoan())).Cashflows);
    }

    [Fact]
    public void Per_Period_Arrays_Carry_A_Psa_Speed_Too()
    {
        var request = Request("PSA", 0.0, NewLoan());
        request.Assumptions.CprVector = Enumerable.Repeat(150.0, Periods).ToArray();

        ShouldMatch(Ok(request).Cashflows, Ok(CprRamp(150, 1, NewLoan())).Cashflows);
    }

    [Fact]
    public void A_Loan_Without_An_Origination_Date_Starts_At_Age_1_And_Is_Reported()
    {
        var undated = Loan("UNDATED", default);
        var future = Loan("FUTURE", ProjectionDate.AddMonths(3));

        var response = Ok(Request("PSA", 100, undated));
        ShouldMatch(response.Cashflows, Ok(CprRamp(100, 1, Loan("UNDATED", default))).Cashflows);

        var resolution = Ok(Request("PSA", 100, NewLoan("A"), undated, future)).PrepaymentResolution!;
        resolution.PrepaymentType.Should().Be("PSA");
        resolution.AssetsAgedFromOriginalDate.Should().Be(1);
        resolution.AssetsWithoutOriginalDate.Should().Be(2);
        resolution.AssetIdsWithoutOriginalDate.Should().Equal("UNDATED", "FUTURE");
    }

    [Fact]
    public void Psa_Counts_Are_Absent_Under_Other_Conventions()
    {
        var resolution = Ok(Request("CPR", 6, NewLoan())).PrepaymentResolution!;

        resolution.AssetsAgedFromOriginalDate.Should().BeNull();
        resolution.AssetsWithoutOriginalDate.Should().BeNull();
    }

    [Theory]
    [InlineData(300.0)]
    [InlineData(5000.0)]
    public void A_Psa_Speed_Above_100_Is_Accepted(double speed)
    {
        Ok(Request("PSA", speed, NewLoan())).Cashflows.Should().NotBeEmpty();
    }

    [Fact]
    public void A_Psa_Speed_Above_5000_Is_Refused_In_Psa_Words()
    {
        var error = Post(Request("PSA", 6000, NewLoan())).Should().BeOfType<BadRequestObjectResult>().Subject.Value!
            .ToString();

        error.Should().Contain("assumptions.cpr").And.Contain("PSA is a speed").And.Contain("5000");
    }

    [Fact]
    public void Psa_With_PolyPaths_Strings_Is_Refused()
    {
        var request = Request("PSA", 100, NewLoan());
        request.Assumptions.CprVectorStr = "100";

        Post(request).Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()
            .Should().Contain("PSA").And.Contain("cprVector");
    }

    [Fact]
    public void A_Reinvested_Cohort_Ramps_From_Its_Purchase_Not_At_The_Raw_Speed()
    {
        // Before PSA had a branch here, the reinvestment path de-annualized the speed as a CPR, so
        // 100 PSA read as 100% CPR and every cohort prepaid in full in the month it was bought.
        var firstProj = new DateTime(2026, 6, 1);
        var basePool = Enumerable.Range(0, 24).Select(p => new PeriodCashflows
        {
            CashflowDate = firstProj.AddMonths(p),
            GroupNum = "1",
            BeginBalance = 1_000_000.0 - 10_000.0 * p,
            Balance = 1_000_000.0 - 10_000.0 * (p + 1),
            ScheduledPrincipal = 10_000.0,
            Interest = 4_000.0,
        }).ToList();
        var cfg = new ReinvestmentConfig
        {
            ReinvestStartDate = firstProj,
            ReinvestEndDate = firstProj.AddMonths(1),
            Target = 1_000_000.0,
            Templates = new[]
            {
                new ReinvestTemplate
                {
                    AllocationPct = 100, IsSynthetic = true, AmortizationType = AmortizationType.Bullet,
                    CouponRate = 5.0, TermMonths = 60,
                },
            },
        };
        var anchor = DateUtil.CalcAbsT(firstProj);
        IAssetAssumptions Assumps(PrepaymentTypeEnum type, double speed) => new AssetAssumptions(
            type, new ConstVector(anchor, speed), DefaultTypeEnum.CDR, new ConstVector(anchor, 0.0),
            new ConstVector(anchor, 0.0));

        var psa = CfCore.BuildReinvestmentCashflows(basePool, cfg, firstProj, Assumps(PrepaymentTypeEnum.PSA, 100), null);
        var cpr = CfCore.BuildReinvestmentCashflows(basePool, cfg, firstProj, Assumps(PrepaymentTypeEnum.CPR, 6), null);

        double Prepaid(IList<PeriodCashflows> cfs, int months) =>
            cfs.Where(c => c.CashflowDate < firstProj.AddMonths(months)).Sum(c => Math.Max(c.UnscheduledPrincipal, 0));

        Prepaid(psa, 12).Should().BeGreaterThan(0.0);
        Prepaid(psa, 12).Should().BeLessThan(0.5 * Prepaid(cpr, 12),
            "a cohort bought new is at most 2.4% CPR in its first year at 100 PSA, against a flat 6%");
    }
}
