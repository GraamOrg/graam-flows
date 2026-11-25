using FluentAssertions;
using GraamFlows.Api.Controllers;
using GraamFlows.Api.Models;
using GraamFlows.AssetCashflowEngine;
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
///     An asset's prepayment penalty schedule (<see cref="IAsset.PrepaymentPenalties" />): a
///     multifamily loan whose voluntary prepayments pay a stated percent of the principal prepaid,
///     stepping down by date ("10% ... for 12 payment dates, thereafter 9%, 8% ..."). The penalty is
///     its own collateral channel (<see cref="PeriodCashflows.PrepaymentPenalty" />), outside the
///     pool's interest. Synthetic numbers.
/// </summary>
public class PrepaymentPenaltyTests
{
    private const double RatePct = 5.0;
    private const double Balance = 20_000_000.0;
    private const int Term = 360;
    private static readonly DateTime FirstProjDate = new(2026, 10, 1);

    // No penalty in October and November, 10% from December, 5% from March, none from June.
    private static List<PrepaymentPenaltyStep> Schedule() => new()
    {
        new(new DateTime(2026, 12, 1), 10.0),
        new(new DateTime(2027, 3, 1), 5.0),
        new(new DateTime(2027, 6, 1), 0.0)
    };

    private static double ExpectedPct(DateTime date) => date < new DateTime(2026, 12, 1) ? 0.0
        : date < new DateTime(2027, 3, 1) ? 10.0
        : date < new DateTime(2027, 6, 1) ? 5.0
        : 0.0;

    private static Asset Loan(List<PrepaymentPenaltyStep>? schedule) => new()
    {
        AssetName = "MF1",
        AssetId = "MF1",
        InterestRateType = InterestRateType.FRM,
        OriginalDate = FirstProjDate,
        OriginalBalance = Balance,
        CurrentBalance = Balance,
        BalanceAtIssuance = Balance,
        OriginalInterestRate = RatePct,
        CurrentInterestRate = RatePct,
        OriginalAmortizationTerm = Term,
        GroupNum = "1",
        PrepaymentPenalties = schedule
    };

    private static List<PeriodCashflows> Run(IAsset asset, double cpr, double cdr = 0.0)
    {
        var anchorAbsT = DateUtil.CalcAbsT(FirstProjDate);
        IAssetAssumptions Assump(IAsset _) => new AssetAssumptions(
            PrepaymentTypeEnum.CPR, new ConstVector(anchorAbsT, cpr),
            DefaultTypeEnum.CDR, new ConstVector(anchorAbsT, cdr),
            new ConstVector(anchorAbsT, 40.0));
        var result = CfCore.GenerateAssetCashflows(new List<IAsset> { asset }, FirstProjDate, null, Assump, null!);
        return result.PeriodCashflows.OrderBy(p => p.CashflowDate).ToList();
    }

    [Fact]
    public void A_Prepayment_Pays_The_Step_Percent_Of_The_Principal_Prepaid_In_Each_Month()
    {
        var periods = Run(Loan(Schedule()), cpr: 12.0);

        foreach (var p in periods.Take(12))
            p.PrepaymentPenalty.Should().BeApproximately(
                p.UnscheduledPrincipal * ExpectedPct(p.CashflowDate) / 100.0, 1e-6, $"{p.CashflowDate:yyyy-MM}");
        periods[2].PrepaymentPenalty.Should().BeGreaterThan(0, "December 2026 is the first month of the 10% step");
        periods.Take(2).Should().OnlyContain(p => p.PrepaymentPenalty == 0);
        periods.Skip(8).Should().OnlyContain(p => p.PrepaymentPenalty == 0, "the schedule ends with a step of 0");
    }

    [Fact]
    public void The_Schedule_Changes_No_Other_Cashflow()
    {
        var with = Run(Loan(Schedule()), cpr: 12.0);
        var without = Run(Loan(null), cpr: 12.0);

        with.Should().HaveCount(without.Count);
        for (var i = 0; i < with.Count; i++)
        {
            with[i].UnscheduledPrincipal.Should().Be(without[i].UnscheduledPrincipal);
            with[i].ScheduledPrincipal.Should().Be(without[i].ScheduledPrincipal);
            with[i].Interest.Should().Be(without[i].Interest, "the penalty is not the pool's interest");
            with[i].Balance.Should().Be(without[i].Balance);
            without[i].PrepaymentPenalty.Should().Be(0);
        }
    }

    [Fact]
    public void A_Default_Pays_No_Penalty()
    {
        var periods = Run(Loan(Schedule()), cpr: 0.0, cdr: 10.0);

        periods.Take(12).Should().Contain(p => p.DefaultedPrincipal > 0);
        periods.Should().OnlyContain(p => p.PrepaymentPenalty == 0);
    }

    [Fact]
    public void A_Schedule_Out_Of_Date_Order_Is_Refused()
    {
        var asset = Loan(new List<PrepaymentPenaltyStep>
        {
            new(new DateTime(2027, 3, 1), 5.0),
            new(new DateTime(2026, 12, 1), 10.0)
        });

        var act = () => PrepaymentPenalties.PctByRow(asset, DateUtil.CalcAbsT(FirstProjDate), 24);
        act.Should().Throw<ArgumentException>().WithMessage("*date order*");
    }

    [Fact]
    public void A_Percent_Outside_0_To_100_Is_Refused()
    {
        var asset = Loan(new List<PrepaymentPenaltyStep> { new(new DateTime(2026, 12, 1), 110.0) });

        var act = () => PrepaymentPenalties.PctByRow(asset, DateUtil.CalcAbsT(FirstProjDate), 24);
        act.Should().Throw<ArgumentException>().WithMessage("*0 to 100*");
    }

    [Fact]
    public void A_Step_That_Starts_Before_The_Projection_Applies_From_Its_First_Row()
    {
        var asset = Loan(new List<PrepaymentPenaltyStep>
        {
            new(new DateTime(2025, 1, 1), 8.0),
            new(new DateTime(2027, 1, 1), 0.0)
        });

        var pct = PrepaymentPenalties.PctByRow(asset, DateUtil.CalcAbsT(FirstProjDate), 6)!;
        pct.Should().Equal(8.0, 8.0, 8.0, 0.0, 0.0, 0.0);
    }

    [Fact]
    public void The_Response_Returns_The_Penalty_And_Names_The_Assets_Whose_Schedule_Ran()
    {
        AssetDto Dto(string id, List<PrepaymentPenaltyStepDto>? schedule) => new()
        {
            AssetName = id,
            AssetId = id,
            InterestRateType = "FRM",
            OriginalDate = FirstProjDate,
            OriginalBalance = Balance,
            CurrentBalance = Balance,
            OriginalInterestRate = RatePct,
            CurrentInterestRate = RatePct,
            OriginalAmortizationTerm = Term,
            GroupNum = "1",
            PrepaymentPenalties = schedule
        };
        var schedule = Schedule().Select(s => new PrepaymentPenaltyStepDto { From = s.From, Pct = s.Pct }).ToList();

        var result = new CalcCollateralController(NullLogger<CalcCollateralController>.Instance).Calculate(
            new CalcCollateralRequest
            {
                Assets = new List<AssetDto> { Dto("MF1", schedule), Dto("MF2", null) },
                ProjectionDate = FirstProjDate,
                Assumptions = new AssumptionsDto { Cpr = 12.0, Cdr = 0.0 }
            }).Result!;

        var response = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<CalcCollateralResponse>().Subject;
        response.PrepaymentPenaltyAssetIds.Should().Equal("MF1");
        // Two equal loans prepay alike; only MF1 pays a penalty, so the pool's is half its prepayment's.
        var december = response.Cashflows.Single(c => c.CashflowDate.Year == 2026 && c.CashflowDate.Month == 12);
        december.PrepaymentPenalty.Should().BeApproximately(december.UnscheduledPrincipal / 2 * 0.10, 1e-4);
        response.Cashflows.Sum(c => c.PrepaymentPenalty).Should().BeGreaterThan(0);
    }
}
